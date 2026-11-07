//! 校验删除旧根 CFG 所需的数据布局证明。
//!
//! 此模块只读取固定目录中的记录、备份和新配置，不删除文件，也不把迁移备份列为清理对象。
//! 这里证明的是“旧原件仍有可核验副本且新配置具备基本结构”，并非完整的 BepInEx 绑定验收。
//! 调用方执行清理时仍须要求 `--confirm-migrated`，确认新版已经成功使用，并锁定文件身份、
//! 绑定证明文件摘要、在删除前重新验证；单次路径读取不能代替上述并发修改防护。

use crate::offline_migration::ensure_no_links;
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::{
    collections::HashSet,
    fs,
    io::{self, Read},
    path::{Path, PathBuf},
};

const CONFIG_NAME: &str = "com.tyukki.mystia-steward-companion.cfg";
const MAX_CONFIG_BYTES: u64 = 1024 * 1024;
const MAX_BACKUP_BYTES: u64 = 64 * 1024 * 1024;
const ALLOWED_FILES: [&str; 8] = [
    "legacy-plugin.cfg",
    "current-plugin.cfg",
    "favorites.json",
    "custom-recipes.json",
    "custom-recipes.json.v1.backup",
    "companion-devices.json",
    "game-focus-overlay.json",
    "game-focus-overlay.json.v1.bak",
];

/// 证明文件始终保留；`legacy_config` 仅在旧原件与受保护备份逐字节一致时给出。
/// 调用方不能将 `evidence` 当成待删除清单，也不能仅凭其中的路径跳过执行阶段的重新验证。
pub struct LayoutProof {
    pub evidence: Vec<PathBuf>,
    pub legacy_config: Option<PathBuf>,
}

// 全部字段必填，派生反序列化器同时拒绝重复字段和未知字段；不能先转为 Value，
// 否则重复 JSON 键可能先被折叠。此结构与 C# layoutVersion=1 的持久化字段逐项对应。
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LayoutRecord {
    layout_version: u32,
    status: String,
    backup_directory: String,
    configuration_sha256: String,
    files: Vec<BackupEntry>,
}

#[derive(Deserialize, PartialEq)]
#[serde(deny_unknown_fields)]
struct BackupEntry {
    key: String,
    length: u64,
    sha256: String,
}

/// 仅接受指定游戏根下的固定布局，不从记录读取绝对路径或任意相对路径。
/// 旧 CFG 已不存在时返回空候选；存在但缺少一致备份时拒绝整个证明，防止删除唯一原件。
pub fn validate(game: &Path) -> Result<LayoutProof, String> {
    if !game.is_absolute() {
        return Err("清理布局证明需要绝对游戏目录。".into());
    }
    ensure_no_links(game)?;
    if !fs::metadata(game)
        .map_err(|_| "无法读取游戏目录。")?
        .is_dir()
    {
        return Err("游戏根路径必须是目录。".into());
    }
    let config_root = game.join("BepInEx/config");
    let data_root = config_root.join("MystiaStewardCompanion");
    let state_path = data_root.join("layout-state.json");
    let state = read_record(&state_path, "complete")?;
    let backup_root = data_root
        .join("migration-backups")
        .join(&state.backup_directory);
    let manifest_path = backup_root.join("manifest.json");
    let manifest = read_record(&manifest_path, "prepared")?;
    if manifest.backup_directory != state.backup_directory
        || manifest.configuration_sha256 != state.configuration_sha256
        || manifest.files != state.files
    {
        return Err("布局记录与迁移备份清单不一致，不能清理旧配置。".into());
    }

    let mut evidence = vec![state_path, manifest_path];
    let mut legacy_copy = None;
    for entry in &state.files {
        // key 已经过固定白名单验证；业务 JSON 只核对原字节，不进行 schema 转换或默认补全。
        let path = backup_root.join(&entry.key);
        let bytes = read_required(&path, MAX_BACKUP_BYTES)?;
        if bytes.len() as u64 != entry.length || hash(&bytes) != entry.sha256 {
            return Err("迁移备份大小或 SHA-256 不匹配，不能清理旧配置。".into());
        }
        if entry.key == "legacy-plugin.cfg" {
            legacy_copy = Some(bytes);
        }
        evidence.push(path);
    }
    let prepared_path = backup_root.join("prepared-plugin.cfg");
    if hash(&read_required(&prepared_path, MAX_CONFIG_BYTES)?) != state.configuration_sha256 {
        return Err("待发布配置的迁移备份摘要不匹配，不能清理旧配置。".into());
    }
    evidence.push(prepared_path);

    let current_path = data_root.join(CONFIG_NAME);
    validate_current_config(&read_required(&current_path, MAX_CONFIG_BYTES)?)?;
    evidence.push(current_path);

    // 新配置经过真实绑定后会补充默认项，用户也可合法轮换 Token；不要求它仍等于 prepared。
    // 删除旧根文件的依据是其原件副本完整，而不是新旧 CFG 现在仍逐字节一致。
    let legacy_path = config_root.join(CONFIG_NAME);
    let legacy_config = match read_optional(&legacy_path, MAX_CONFIG_BYTES)? {
        None => None,
        Some(bytes) => {
            let preserved = legacy_copy.ok_or("旧根配置没有对应迁移备份，不能删除唯一原件。")?;
            if bytes != preserved {
                return Err("旧根配置在迁移后发生变化，不能删除尚未备份的修改。".into());
            }
            Some(legacy_path)
        }
    };
    Ok(LayoutProof {
        evidence,
        legacy_config,
    })
}

/// marker 与 manifest 使用相同严格格式，只允许分别处于 complete 和 prepared 状态。
/// 备份目录名称只含固定前缀和 32 位十六进制标识；它不能携带分隔符或路径穿越片段。
fn read_record(path: &Path, expected_status: &str) -> Result<LayoutRecord, String> {
    let bytes = read_required(path, MAX_CONFIG_BYTES)?;
    let record: LayoutRecord = serde_json::from_slice(&bytes)
        .map_err(|_| "布局记录损坏，或包含重复、缺失、未知字段；不能清理旧配置。")?;
    if record.layout_version != 1 {
        return Err("不支持此数据目录布局版本，不能执行旧版清理。".into());
    }
    if record.status != expected_status {
        return Err("数据布局迁移尚未完成，或备份清单状态不正确。".into());
    }
    let suffix = record
        .backup_directory
        .strip_prefix("layout-v1-")
        .unwrap_or("");
    if suffix.len() != 32
        || !suffix.bytes().all(|value| value.is_ascii_hexdigit())
        || !valid_hash(&record.configuration_sha256)
        || record.files.len() > ALLOWED_FILES.len()
    {
        return Err("布局记录中的备份位置、摘要或文件数量无效。".into());
    }
    let mut seen = HashSet::new();
    for entry in &record.files {
        if !ALLOWED_FILES.contains(&entry.key.as_str())
            || !seen.insert(&entry.key)
            || entry.length > MAX_BACKUP_BYTES
            || !valid_hash(&entry.sha256)
        {
            return Err("布局记录包含未知、重复或无效的备份项。".into());
        }
    }
    Ok(record)
}

fn valid_hash(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn hash(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

/// 只把明确的 NotFound 当成不存在；权限、独占占用、目录替代文件和超限均终止校验。
/// 打开后的元数据和有界读取限制内存占用；路径替换竞争仍由外层稳定句柄负责排除。
fn read_optional(path: &Path, maximum: u64) -> Result<Option<Vec<u8>>, String> {
    ensure_no_links(path)?;
    let file = match fs::File::open(path) {
        Ok(file) => file,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err("无法读取布局证明文件，请检查权限与文件占用。".into()),
    };
    let meta = file.metadata().map_err(|_| "无法检查布局证明文件。")?;
    if !meta.is_file() || meta.len() > maximum {
        return Err("布局证明文件不是普通文件，或超过大小上限。".into());
    }
    let mut bytes = Vec::new();
    file.take(maximum + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| "布局证明文件读取失败。")?;
    if bytes.len() as u64 != meta.len() || bytes.len() as u64 > maximum {
        return Err("布局证明文件在读取期间改变或超过大小上限。".into());
    }
    Ok(Some(bytes))
}

fn read_required(path: &Path, maximum: u64) -> Result<Vec<u8>, String> {
    read_optional(path, maximum)?
        .ok_or_else(|| "缺少布局记录、新配置或迁移备份，不能清理旧配置。".into())
}

/// 只核对新 CFG 的基本语法和凭据传输形式；不复制 BepInEx 的全部类型、枚举或数值转换规则。
/// 未知项可保留，但重复项、无分节键值、非法控制字符以及缺失/空 Token 均不能作为迁移证明。
fn validate_current_config(bytes: &[u8]) -> Result<(), String> {
    let text = std::str::from_utf8(bytes).map_err(|_| "新配置不是有效 UTF-8，不能清理旧配置。")?;
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    if text.is_empty()
        || text
            .chars()
            .any(|value| value.is_control() && !matches!(value, '\n' | '\r' | '\t'))
    {
        return Err("新配置为空或包含非法控制字符，不能清理旧配置。".into());
    }
    let mut section = "";
    let mut seen = HashSet::new();
    let mut token = None;
    for raw in text.split('\n') {
        let line = raw.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if line.starts_with('[') && line.ends_with(']') && line.len() > 2 {
            section = line[1..line.len() - 1].trim();
            if section.is_empty() || section.contains(['[', ']']) {
                return Err("新配置分节格式无效，不能清理旧配置。".into());
            }
            continue;
        }
        let (key, value) = line
            .split_once('=')
            .ok_or("新配置存在无法识别的行，不能清理旧配置。")?;
        let key = key.trim();
        if section.is_empty() || key.is_empty() || !seen.insert((section, key)) {
            return Err("新配置缺少分节、键名为空或配置项重复，不能清理旧配置。".into());
        }
        if section == "LocalApi" && key == "Token" {
            token = Some(value.trim());
        }
    }
    let token = token.ok_or("新配置缺少 Token；请先确认新版已成功启动并完成迁移。")?;
    validate_token_text(token)
}

/// BepInEx 保存的反斜线/引号转义可明确还原成可见字符；其他转义保守拒绝，避免把
/// `\n` 等编码文本误当可用凭据。此处不回显值，也不尝试解释任意 TOML 扩展语法。
fn validate_token_text(token: &str) -> Result<(), String> {
    let mut bytes = token.bytes();
    let mut count = 0;
    while let Some(byte) = bytes.next() {
        if !(b'!'..=b'~').contains(&byte) {
            return Err("新配置 Token 不是可见 ASCII；请先确认新版已成功使用。".into());
        }
        if byte == b'\\' && !matches!(bytes.next(), Some(b'\\' | b'"' | b'\'')) {
            return Err("新配置 Token 包含无法安全确认的转义；请先使用新版校验配置。".into());
        }
        count += 1;
    }
    if !(8..=128).contains(&count) {
        return Err("新配置 Token 为空或长度不兼容；请先确认新版已成功启动。".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{json, Value};
    use std::sync::atomic::{AtomicU64, Ordering};

    static NEXT_FIXTURE: AtomicU64 = AtomicU64::new(0);

    /// 每例只创建独立随机化合成目录；析构只清理自身根，不接触游戏安装或实际配置。
    struct Fixture {
        game: PathBuf,
        root: PathBuf,
        backup: PathBuf,
        state: Value,
    }
    impl Fixture {
        fn new() -> Self {
            let game = std::env::temp_dir().join(format!(
                "mystia-layout-proof-{}-{}-{}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos(),
                NEXT_FIXTURE.fetch_add(1, Ordering::Relaxed)
            ));
            let root = game.join("BepInEx/config/MystiaStewardCompanion");
            let backup = root.join("migration-backups/layout-v1-00112233445566778899aabbccddeeff");
            fs::create_dir_all(&backup).unwrap();
            let original = b"[LocalApi]\nToken = synthetic-original-token\n";
            let prepared = b"[LocalApi]\nToken = synthetic-original-token\n[Diagnostics]\nAggregateModLogPath = \n";
            let business = b"raw JSON bytes; no schema interpretation";
            fs::write(backup.join("legacy-plugin.cfg"), original).unwrap();
            fs::write(backup.join("favorites.json"), business).unwrap();
            fs::write(backup.join("prepared-plugin.cfg"), prepared).unwrap();
            fs::write(root.parent().unwrap().join(CONFIG_NAME), original).unwrap();
            // 新 Token 可合法轮换；删除证明不要求新 CFG 等于 prepared 或旧原件。
            fs::write(
                root.join(CONFIG_NAME),
                b"[LocalApi]\nToken = synthetic-new-token\n",
            )
            .unwrap();
            let state = json!({"layoutVersion":1,"status":"complete", "backupDirectory":"layout-v1-00112233445566778899aabbccddeeff",
            "configurationSha256":hash(prepared),"files":[
                {"key":"legacy-plugin.cfg","length":original.len(),"sha256":hash(original)},
                {"key":"favorites.json","length":business.len(),"sha256":hash(business)}
            ]});
            let value = Self {
                game,
                root,
                backup,
                state,
            };
            value.write_records();
            value
        }
        fn write_records(&self) {
            fs::write(
                self.root.join("layout-state.json"),
                serde_json::to_vec(&self.state).unwrap(),
            )
            .unwrap();
            let mut manifest = self.state.clone();
            manifest["status"] = json!("prepared");
            fs::write(
                self.backup.join("manifest.json"),
                serde_json::to_vec(&manifest).unwrap(),
            )
            .unwrap();
        }
        fn reject(&self) -> String {
            match validate(&self.game) {
                Ok(_) => panic!("应拒绝不完整的布局证明"),
                Err(error) => error,
            }
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.game);
        }
    }

    #[test]
    fn valid_proof_keeps_backups_and_allows_current_token_rotation() {
        let fixture = Fixture::new();
        let proof = validate(&fixture.game).unwrap();
        assert_eq!(
            proof.legacy_config,
            Some(fixture.root.parent().unwrap().join(CONFIG_NAME))
        );
        assert_eq!(proof.evidence.len(), 6);
        assert!(proof.evidence.contains(&fixture.root.join(CONFIG_NAME)));
        assert!(proof
            .evidence
            .contains(&fixture.backup.join("legacy-plugin.cfg")));
        assert!(proof.evidence.iter().all(|path| path.exists()));
        fs::remove_file(fixture.root.parent().unwrap().join(CONFIG_NAME)).unwrap();
        assert!(validate(&fixture.game).unwrap().legacy_config.is_none());
        assert!(fixture.backup.join("legacy-plugin.cfg").exists());
    }

    #[test]
    fn rejects_future_incomplete_unknown_and_conflicting_records() {
        for mutation in [
            "future",
            "incomplete",
            "unknown",
            "missing",
            "unknown-file",
            "duplicate-file",
            "path",
            "bad-hash",
            "oversize",
            "manifest",
        ] {
            let mut fixture = Fixture::new();
            match mutation {
                "future" => fixture.state["layoutVersion"] = json!(2),
                "incomplete" => fixture.state["status"] = json!("prepared"),
                "unknown" => {
                    fixture.state["extra"] = json!(true);
                }
                "missing" => {
                    fixture
                        .state
                        .as_object_mut()
                        .unwrap()
                        .remove("configurationSha256");
                }
                "unknown-file" => fixture.state["files"][0]["key"] = json!("unknown.json"),
                "duplicate-file" => {
                    let entry = fixture.state["files"][0].clone();
                    fixture.state["files"].as_array_mut().unwrap().push(entry);
                }
                "path" => fixture.state["backupDirectory"] = json!("../escape"),
                "bad-hash" => fixture.state["files"][0]["sha256"] = json!("0"),
                "oversize" => fixture.state["files"][0]["length"] = json!(MAX_BACKUP_BYTES + 1),
                "manifest" => {}
                _ => unreachable!(),
            }
            fixture.write_records();
            if mutation == "manifest" {
                let mut manifest = fixture.state.clone();
                manifest["status"] = json!("prepared");
                manifest["configurationSha256"] = json!("0".repeat(64));
                fs::write(
                    fixture.backup.join("manifest.json"),
                    serde_json::to_vec(&manifest).unwrap(),
                )
                .unwrap();
            }
            assert!(!fixture.reject().is_empty(), "{mutation}");
        }
    }

    #[test]
    fn rejects_duplicate_json_fields_before_values_can_be_collapsed() {
        for file in ["layout-state.json", "manifest.json"] {
            for duplicate in ["root", "entry"] {
                let fixture = Fixture::new();
                let path = if file == "layout-state.json" {
                    fixture.root.join(file)
                } else {
                    fixture.backup.join(file)
                };
                let source = fs::read_to_string(&path).unwrap();
                let changed = if duplicate == "root" {
                    source.replacen(
                        "\"layoutVersion\":1",
                        "\"layoutVersion\":1,\"layoutVersion\":1",
                        1,
                    )
                } else {
                    source.replacen(
                        "\"key\":\"legacy-plugin.cfg\"",
                        "\"key\":\"legacy-plugin.cfg\",\"key\":\"legacy-plugin.cfg\"",
                        1,
                    )
                };
                assert_ne!(changed, source);
                fs::write(path, changed).unwrap();
                assert!(fixture.reject().contains("重复"));
            }
        }
    }

    #[test]
    fn rejects_missing_corrupted_or_changed_data_without_deleting_originals() {
        for change in [
            "old-changed",
            "not-backed-up",
            "backup-missing",
            "backup-damaged",
            "prepared-damaged",
            "current-missing",
            "marker-damaged",
        ] {
            let mut fixture = Fixture::new();
            match change {
                "old-changed" => fs::write(
                    fixture.root.parent().unwrap().join(CONFIG_NAME),
                    b"unpreserved modification",
                )
                .unwrap(),
                "not-backed-up" => {
                    fixture.state["files"].as_array_mut().unwrap().remove(0);
                    fixture.write_records();
                }
                "backup-missing" => {
                    fs::remove_file(fixture.backup.join("legacy-plugin.cfg")).unwrap()
                }
                "backup-damaged" => {
                    fs::write(fixture.backup.join("favorites.json"), b"changed").unwrap()
                }
                "prepared-damaged" => {
                    fs::write(fixture.backup.join("prepared-plugin.cfg"), b"changed").unwrap()
                }
                "current-missing" => fs::remove_file(fixture.root.join(CONFIG_NAME)).unwrap(),
                "marker-damaged" => {
                    fs::write(fixture.root.join("layout-state.json"), b"{broken").unwrap()
                }
                _ => unreachable!(),
            }
            fixture.reject();
            assert!(
                fixture.root.parent().unwrap().join(CONFIG_NAME).exists(),
                "{change}"
            );
        }
    }

    #[test]
    fn current_config_check_is_structural_and_never_echoes_credentials() {
        for bytes in [
            b"".as_slice(),
            b"# only a comment",
            b"\xff",
            b"Token = synthetic-token",
            b"[LocalApi]\nToken = \n",
            b"[LocalApi]\nToken = synthetic secret\n",
            b"[LocalApi]\nToken = synthetic\\nsecret\n",
            b"[LocalApi]\nToken = synthetic-token\nToken = duplicated\n",
            b"[LocalApi]\nPort = 32145\n",
        ] {
            let fixture = Fixture::new();
            fs::write(fixture.root.join(CONFIG_NAME), bytes).unwrap();
            let error = fixture.reject();
            assert!(!error.contains("synthetic"));
        }
        validate_current_config(b"\xef\xbb\xbf[LocalApi]\r\nToken = synthetic-\\\\-\\\"-token\r\n[Unknown]\r\nOption = preserved\r\n").unwrap();
        // 其他类型的完整转换属于 Mod 启动职责；清理模块不能宣称接受此文本即证明绑定成功。
        validate_current_config(
            b"[LocalApi]\nToken = synthetic-token\nPort = requires-Bep-validation\n",
        )
        .unwrap();
    }

    #[test]
    fn rejects_evidence_paths_replaced_by_directories() {
        let fixture = Fixture::new();
        let path = fixture.backup.join("legacy-plugin.cfg");
        fs::remove_file(&path).unwrap();
        fs::create_dir(&path).unwrap();
        fixture.reject();
    }

    #[cfg(windows)]
    #[test]
    fn inaccessible_old_config_is_not_treated_as_already_removed() {
        use std::os::windows::fs::OpenOptionsExt;
        let fixture = Fixture::new();
        let lease = fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(fixture.root.parent().unwrap().join(CONFIG_NAME))
            .unwrap();
        assert!(fixture.reject().contains("权限与文件占用"));
        drop(lease);
        assert!(validate(&fixture.game).unwrap().legacy_config.is_some());
    }

    #[cfg(any(unix, windows))]
    #[test]
    fn linked_backup_is_rejected_without_reading_or_removing_its_target() {
        let fixture = Fixture::new();
        let path = fixture.backup.join("legacy-plugin.cfg");
        let outside = fixture.game.join("synthetic-outside.cfg");
        fs::rename(&path, &outside).unwrap();
        let before = fs::read(&outside).unwrap();
        #[cfg(windows)]
        let linked = std::os::windows::fs::symlink_file(&outside, &path);
        #[cfg(unix)]
        let linked = std::os::unix::fs::symlink(&outside, &path);
        if let Err(error) = linked {
            // Windows 非开发者模式可能缺少创建符号链接权限；明确报告未执行，其他错误不能忽略。
            if cfg!(windows) && error.raw_os_error() == Some(1314) {
                eprintln!("SKIP: 当前系统不允许创建符号链接，布局证明链接测试未执行。");
                return;
            }
            panic!("无法创建合成链接夹具：{error}");
        }
        assert!(fixture.reject().contains("linked path rejected"));
        assert_eq!(fs::read(&outside).unwrap(), before);
        fs::remove_file(path).unwrap();
    }
}
