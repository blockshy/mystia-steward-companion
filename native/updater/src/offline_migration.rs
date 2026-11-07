//! v1.3.1 公开 Tauri 包的一次性离线安装入口。
//!
//! 本模块只迁移程序安装树，不读取业务配置值、不转换存档或 JSON schema。配置/核心/存档
//! 仅计算摘要并保护不变；新版首次启动的数据布局迁移由 Mod 自身负责。默认预览完全不写盘。
use crate::{client_exit, package_manifest, replace_plugin_directory_using, InstallProgress};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::{
    collections::{BTreeMap, HashSet},
    fs,
    io::{self, Read},
    path::{Path, PathBuf},
};

/// 来源为公开 v1.3.1 的三个原始文件；只比较安装内容，不依赖可伪造的文件版本资源或目录名称。
pub(crate) const LEGACY_FILES: [(&str, u64, &str); 3] = [
    (
        "MystiaStewardCompanion.BepInEx.dll",
        2482176,
        "07688282f987f6ad031b90b4884083cc8452eb0aad22330c0449aa445822b3c6",
    ),
    (
        "mystia-steward-companion-updater.exe",
        349184,
        "c377a09063024386af1ec058db110391040077136024f44676cef48bd68529f7",
    ),
    (
        "companion/mystia-steward-companion.exe",
        10388992,
        "fa7a5ef5e6a7f5f4a11e9a9b9516fa051f9828aa0a1bb3d0aff740cbf0c4b9d0",
    ),
];
type Tree = BTreeMap<String, String>;

#[derive(Clone)]
struct Options {
    game: PathBuf,
    package: PathBuf,
    save: PathBuf,
    backup: PathBuf,
    execute: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    mode: &'static str,
    source_version: &'static str,
    target_version: String,
    game_directory: PathBuf,
    backup_directory: PathBuf,
    writes_performed: bool,
}

/// 显式模式不显示普通在线更新窗口，也不发送客户端退出命令；操作者必须事先退出相关程序。
pub fn run(args: &[String]) -> Result<String, String> {
    let options = parse(args)?;
    perform(&options, &mut |_, _| Ok(()))
}

fn parse(args: &[String]) -> Result<Options, String> {
    let mut values = BTreeMap::new();
    let mut execute = false;
    let mut index = 0;
    while index < args.len() {
        let key = args[index].as_str();
        if key == "--execute" {
            if execute {
                return Err("duplicate --execute".into());
            }
            execute = true;
            index += 1;
            continue;
        }
        if !["--game-dir", "--package-dir", "--save-dir", "--backup-dir"].contains(&key) {
            return Err(format!("unknown offline migration argument: {key}"));
        }
        let value = args
            .get(index + 1)
            .filter(|value| !value.starts_with("--"))
            .ok_or_else(|| format!("missing value for {key}"))?;
        if values.insert(key, PathBuf::from(value)).is_some() {
            return Err(format!("duplicate {key}"));
        }
        index += 2;
    }
    let mut take = |key| {
        values.remove(key).ok_or_else(|| format!("missing {key}; use --migrate-v1-3-1 --game-dir <absolute> --package-dir <absolute> --save-dir <absolute> --backup-dir <new absolute directory> [--execute]"))
    };
    Ok(Options {
        game: take("--game-dir")?,
        package: take("--package-dir")?,
        save: take("--save-dir")?,
        backup: take("--backup-dir")?,
        execute,
    })
}

/// 检查路径的所有现存祖先；不存在的输出叶子允许保留，但悬空链接、目录联接同样拒绝。
pub fn ensure_no_links(path: &Path) -> Result<(), String> {
    for ancestor in path
        .ancestors()
        .filter(|value| !value.as_os_str().is_empty())
    {
        match fs::symlink_metadata(ancestor) {
            Ok(meta) => {
                let mut linked = meta.file_type().is_symlink();
                #[cfg(windows)]
                {
                    use std::os::windows::fs::MetadataExt;
                    linked |= meta.file_attributes() & 0x400 != 0;
                }
                if linked {
                    return Err(format!("linked path rejected: {}", ancestor.display()));
                }
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.to_string()),
        }
    }
    Ok(())
}

fn existing_directory(path: &Path) -> Result<PathBuf, String> {
    if !path.is_absolute() {
        return Err("all migration paths must be absolute".into());
    }
    ensure_no_links(path)?;
    if !path.is_dir() {
        return Err(format!("directory not found: {}", path.display()));
    }
    path.canonicalize().map_err(|error| error.to_string())
}
fn overlaps(a: &Path, b: &Path) -> bool {
    a.starts_with(b) || b.starts_with(a)
}
fn validate_options(value: &Options) -> Result<Options, String> {
    let game = existing_directory(&value.game)?;
    let package = existing_directory(&value.package)?;
    let save = existing_directory(&value.save)?;
    if !value.backup.is_absolute() || value.backup.file_name().is_none() {
        return Err("backup directory must be an absolute new path".into());
    }
    ensure_no_links(&value.backup)?;
    if value.backup.exists() {
        return Err(
            "backup directory already exists; inspect the previous transaction before retrying"
                .into(),
        );
    }
    let parent = existing_directory(value.backup.parent().ok_or("missing backup parent")?)?;
    let backup = parent.join(value.backup.file_name().unwrap());
    let plugin = game.join("BepInEx/plugins/mystia-steward-companion");
    existing_directory(&plugin)?;
    if overlaps(&package, &game)
        || overlaps(&save, &plugin)
        || overlaps(&backup, &game)
        || overlaps(&backup, &package)
        || overlaps(&backup, &save)
    {
        return Err("package, backup, plugin and protected save paths overlap".into());
    }
    // 整目录替换必须使用同卷 rename；不在 EXDEV 后退化为逐文件复制。
    if game.components().next() != parent.components().next() {
        return Err("backup and game must be on the same volume".into());
    }
    Ok(Options {
        game,
        package,
        save,
        backup,
        execute: value.execute,
    })
}

fn fingerprint(path: &Path) -> Result<String, String> {
    let mut file = fs::File::open(path).map_err(|error| error.to_string())?;
    let mut digest = Sha256::new();
    let mut bytes = [0u8; 65536];
    let mut size = 0u64;
    loop {
        let count = file.read(&mut bytes).map_err(|error| error.to_string())?;
        if count == 0 {
            break;
        }
        digest.update(&bytes[..count]);
        size += count as u64;
    }
    Ok(format!("F:{size}:{:x}", digest.finalize()))
}

/// 配置与存档仅以流式摘要进入记录，不把内容或凭据复制进安装日志；目录也参与前后比较。
fn snapshot(root: &Path) -> Result<Tree, String> {
    ensure_no_links(root)?;
    let mut result = Tree::new();
    if !root.exists() {
        result.insert(".".into(), "absent".into());
        return Ok(result);
    }
    if !root.is_dir() {
        return Err("snapshot root must be a directory".into());
    }
    result.insert(".".into(), "D".into());
    let mut queue = vec![root.to_path_buf()];
    let mut folded = HashSet::new();
    while let Some(directory) = queue.pop() {
        for item in fs::read_dir(directory).map_err(|error| error.to_string())? {
            let path = item.map_err(|error| error.to_string())?.path();
            ensure_no_links(&path)?;
            let name = path
                .strip_prefix(root)
                .map_err(|error| error.to_string())?
                .to_str()
                .ok_or("non-UTF8 path")?
                .replace('\\', "/");
            if !folded.insert(name.to_lowercase()) {
                return Err("case-conflicting directory entries".into());
            }
            let meta = fs::metadata(&path).map_err(|error| error.to_string())?;
            if meta.is_dir() {
                result.insert(name, "D".into());
                queue.push(path);
            } else if meta.is_file() {
                result.insert(name, fingerprint(&path)?);
            } else {
                return Err("non-regular file rejected".into());
            }
        }
    }
    Ok(result)
}
fn same(expected: &Tree, actual: &Tree, label: &str) -> Result<(), String> {
    if expected != actual {
        Err(format!(
            "{label} changed; original data has not been accepted as a valid replacement"
        ))
    } else {
        Ok(())
    }
}
fn preserved(name: &str) -> bool {
    package_manifest::PRESERVED
        .iter()
        .any(|prefix| name == *prefix || name.starts_with(&format!("{prefix}/")))
}

fn validate_legacy(root: &Path) -> Result<Tree, String> {
    let tree = snapshot(root)?;
    for (name, size, hash) in LEGACY_FILES {
        if tree.get(name) != Some(&format!("F:{size}:{hash}")) {
            return Err(format!(
                "installed file does not match public v1.3.1: {name}"
            ));
        }
    }
    for (name, kind) in &tree {
        if name == "."
            || (name == "companion" && kind == "D")
            || LEGACY_FILES.iter().any(|entry| entry.0 == name)
            || preserved(name)
        {
            continue;
        }
        return Err(format!("unrecognized legacy installation entry; retain and inspect it before migration: {name}"));
    }
    Ok(tree)
}

fn protection(options: &Options) -> Result<Tree, String> {
    let mut result = Tree::new();
    for (prefix, path) in [
        ("config", options.game.join("BepInEx/config")),
        ("core", options.game.join("BepInEx/core")),
        ("save", options.save.clone()),
    ] {
        for (name, value) in snapshot(&path)? {
            result.insert(format!("{prefix}/{name}"), value);
        }
    }
    for entry in fs::read_dir(&options.game).map_err(|error| error.to_string())? {
        let path = entry.map_err(|error| error.to_string())?.path();
        ensure_no_links(&path)?;
        if path.is_file() {
            result.insert(
                format!("game-root/{}", path.file_name().unwrap().to_string_lossy()),
                fingerprint(&path)?,
            );
        }
    }
    Ok(result)
}

fn copy_tree(source: &Path, target: &Path, expected: &Tree) -> Result<(), String> {
    ensure_no_links(target)?;
    fs::create_dir(target).map_err(|error| error.to_string())?;
    for (name, kind) in expected {
        if name == "." {
            continue;
        }
        let destination = target.join(name);
        if kind == "D" {
            fs::create_dir_all(&destination).map_err(|error| error.to_string())?;
        } else {
            fs::create_dir_all(destination.parent().unwrap()).map_err(|error| error.to_string())?;
            fs::copy(source.join(name), &destination).map_err(|error| error.to_string())?;
        }
    }
    same(expected, &snapshot(target)?, "copied tree")?;
    same(expected, &snapshot(source)?, "copy source")
}

/// 采用保守的同名进程拒绝策略；不终止游戏、不发送旧业务/窗口协议，也不把检查称为持续锁定。
pub(crate) fn assert_stopped(game: &Path, plugin: &Path) -> Result<(), String> {
    let mut names = HashSet::from([
        "mystia-steward-companion.exe".to_string(),
        "mystia-steward-companion-updater.exe".to_string(),
    ]);
    let mut executables = Vec::new();
    for entry in fs::read_dir(game).map_err(|error| error.to_string())? {
        let path = entry.map_err(|error| error.to_string())?.path();
        if path.is_file()
            && path
                .extension()
                .is_some_and(|ext| ext.eq_ignore_ascii_case("exe"))
        {
            names.insert(path.file_name().unwrap().to_string_lossy().to_lowercase());
            executables.push(path);
        }
    }
    if executables.is_empty() {
        return Err("game root has no executable; target identity cannot be checked".into());
    }
    check_process_names(&names)?;
    for path in executables {
        client_exit::assert_file_unlocked(&path)?;
    }
    client_exit::assert_unlocked(plugin)
}

#[cfg(any(windows, feature = "updater-windows-ui-check"))]
pub(crate) fn check_process_names(names: &HashSet<String>) -> Result<(), String> {
    use windows_sys::Win32::{
        Foundation::{CloseHandle, GetLastError, ERROR_NO_MORE_FILES, INVALID_HANDLE_VALUE},
        System::Diagnostics::ToolHelp::{
            CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
            TH32CS_SNAPPROCESS,
        },
    };
    unsafe {
        let handle = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if handle == INVALID_HANDLE_VALUE {
            return Err("cannot enumerate processes; offline installation refused".into());
        }
        let result = (|| {
            let mut entry: PROCESSENTRY32W = std::mem::zeroed();
            entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
            let mut success = Process32FirstW(handle, &mut entry);
            while success != 0 {
                let length = entry
                    .szExeFile
                    .iter()
                    .position(|ch| *ch == 0)
                    .unwrap_or(entry.szExeFile.len());
                let name = String::from_utf16_lossy(&entry.szExeFile[..length]).to_lowercase();
                if entry.th32ProcessID != std::process::id() && names.contains(&name) {
                    return Err(format!(
                        "close the game/client/updater before migration: {name} (PID {})",
                        entry.th32ProcessID
                    ));
                }
                success = Process32NextW(handle, &mut entry);
            }
            if GetLastError() != ERROR_NO_MORE_FILES {
                return Err("process enumeration was incomplete".into());
            }
            Ok(())
        })();
        CloseHandle(handle);
        result
    }
}
#[cfg(not(any(windows, feature = "updater-windows-ui-check")))]
pub(crate) fn check_process_names(_: &HashSet<String>) -> Result<(), String> {
    Err("offline migration is supported only on Windows".into())
}

fn record<T: Serialize>(root: &Path, name: &str, data: &T) -> Result<(), String> {
    ensure_no_links(root)?;
    let bytes = serde_json::to_vec_pretty(data).map_err(|error| error.to_string())?;
    let temporary = root.join(format!(".{name}.tmp"));
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(|error| error.to_string())?;
    use std::io::Write;
    file.write_all(&bytes)
        .and_then(|_| file.sync_all())
        .map_err(|error| error.to_string())?;
    drop(file);
    fs::rename(temporary, root.join(name)).map_err(|error| error.to_string())
}

/// 故障观察点只由进程内测试注入；公开 CLI 没有破坏性测试参数。异常不会删除备份或失败现场。
fn perform(
    options: &Options,
    observe: &mut impl FnMut(&str, &Path) -> Result<(), String>,
) -> Result<String, String> {
    let options = validate_options(options)?;
    let plugin = options
        .game
        .join("BepInEx/plugins/mystia-steward-companion");
    let manifest = package_manifest::validate(&options.package, false)?;
    let original = validate_legacy(&plugin)?;
    let protected = protection(&options)?;
    let package = snapshot(&options.package)?;
    assert_stopped(&options.game, &plugin)?;
    let mut report = Report {
        mode: "validated-only",
        source_version: "1.3.1",
        target_version: manifest.version,
        game_directory: options.game.clone(),
        backup_directory: options.backup.clone(),
        writes_performed: false,
    };
    if !options.execute {
        return serde_json::to_string_pretty(&report).map_err(|error| error.to_string());
    }

    // 备份目录独占创建，存在即拒绝；中断后不能把部分旧事务当作全新安装继续覆盖。
    ensure_no_links(&options.backup)?;
    fs::create_dir(&options.backup).map_err(|error| error.to_string())?;
    let original_copy = options.backup.join("original-copy");
    let stage = options.backup.join("staged");
    let previous = options.backup.join("plugin-backup");
    let rejected = options.backup.join("rejected-package");
    let status = options.backup.join("install-status.json");
    let result = (|| {
        record(&options.backup, "before-plugin.json", &original)?;
        record(&options.backup, "before-protected.json", &protected)?;
        copy_tree(&plugin, &original_copy, &original)?;
        observe("after-backup", &options.backup)?;
        copy_tree(&options.package, &stage, &package)?;
        same(
            &protected,
            &protection(&options)?,
            "protected data before replacement",
        )?;
        assert_stopped(&options.game, &plugin)?;
        let mut progress = |_: InstallProgress| {};
        replace_plugin_directory_using(&plugin, &stage, &previous, &status, &mut progress, || {
            same(
                &original,
                &validate_legacy(&plugin)?,
                "legacy source before replacement",
            )?;
            for (name, kind) in &original {
                if !preserved(name) {
                    continue;
                }
                let target = stage.join(name);
                if target.exists() {
                    return Err(format!("preserved path conflicts with new package: {name}"));
                }
                if kind == "D" {
                    fs::create_dir_all(&target).map_err(|error| error.to_string())?;
                } else {
                    fs::create_dir_all(target.parent().unwrap())
                        .map_err(|error| error.to_string())?;
                    fs::copy(plugin.join(name), target).map_err(|error| error.to_string())?;
                }
            }
            same(
                &original,
                &snapshot(&plugin)?,
                "legacy source after preservation",
            )?;
            package_manifest::validate(&stage, true)?;
            Ok(())
        })?;
        observe("after-replacement", &plugin)?;
        package_manifest::validate(&plugin, true)?;
        let installed = snapshot(&plugin)?;
        // 自洽清单只能证明当前目录内容有效，不能证明它仍是操作者本次选择的包。
        // 将原清单字节、全部管理载荷及保留树共同固定到最初快照，拒绝另一份自洽包或新增条目。
        let mut expected_installed = package.clone();
        for (name, value) in &original {
            if preserved(name) {
                expected_installed.insert(name.clone(), value.clone());
            }
        }
        same(
            &expected_installed,
            &installed,
            "selected package and preserved data",
        )?;
        same(
            &protected,
            &protection(&options)?,
            "protected data after replacement",
        )?;
        same(
            &original,
            &snapshot(&original_copy)?,
            "retained original copy",
        )?;
        record(&options.backup, "after-plugin.json", &installed)?;
        report.mode = "installed";
        report.writes_performed = true;
        record(&options.backup, "result.json", &report)?;
        crate::write_status(
            &status,
            "succeeded",
            "离线安装已验证完成；配置与存档保持不变。",
            100,
        );
        serde_json::to_string_pretty(&report).map_err(|error| error.to_string())
    })();
    if let Err(error) = result {
        // 共用事务自身处理移动/载荷校验失败；额外的保护区或测试观察失败也恢复完整旧树。
        let recovery = (|| {
            if previous.exists() {
                ensure_no_links(&plugin)?;
                ensure_no_links(&rejected)?;
                ensure_no_links(&previous)?;
                if plugin.exists() {
                    fs::rename(&plugin, &rejected).map_err(|error| error.to_string())?;
                }
                fs::rename(&previous, &plugin).map_err(|error| error.to_string())?;
            }
            same(&original, &snapshot(&plugin)?, "restored legacy tree")?;
            same(
                &protected,
                &protection(&options)?,
                "protected data after failure",
            )
        })();
        let message = format!("offline migration failed: {error}; original/protected verification: {recovery:?}; retain transaction directory {}", options.backup.display());
        let _ = record(
            &options.backup,
            "failure.json",
            &serde_json::json!({"error":error,"restoredAndProtected":recovery.is_ok(),"recoveryError":recovery.err()}),
        );
        crate::write_status(&status, "failed", &message, 100);
        return Err(message);
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn explicit_mode_rejects_ambiguous_arguments_without_io() {
        assert!(parse(&[]).is_err());
        assert!(parse(&["--execute".into(), "--execute".into()]).is_err());
        assert!(parse(&["--unknown".into(), "x".into()]).is_err());
        assert!(parse(&[
            "--game-dir".into(),
            "a".into(),
            "--game-dir".into(),
            "b".into()
        ])
        .is_err());
    }

    #[test]
    fn missing_or_relative_status_never_gains_write_permission() {
        let mut args = std::collections::HashMap::new();
        assert!(crate::explicit_status_path(&args).is_err());
        args.insert("status-file".into(), "update-status.json".into());
        assert!(crate::explicit_status_path(&args).is_err());
        args.insert(
            "status-file".into(),
            std::env::temp_dir()
                .join("must-not-be-created.json")
                .to_string_lossy()
                .into_owned(),
        );
        // 即使给了绝对路径，缺少完整安装上下文时仍不能写失败状态。
        assert!(crate::explicit_status_path(&args).is_err());
    }

    fn fixture(root: &Path, source: &Path, name: &str) -> Options {
        let base = root.join(name);
        fs::create_dir(&base).unwrap();
        let game = base.join("game");
        let plugin = game.join("BepInEx/plugins/mystia-steward-companion");
        fs::create_dir_all(plugin.parent().unwrap()).unwrap();
        copy_tree(source, &plugin, &validate_legacy(source).unwrap()).unwrap();
        for (relative, value) in [
            ("mystia-offline-fixture-game.exe", "fixture, never executed"),
            ("BepInEx/config/other-mod.cfg", "unrelated configuration"),
            (
                "BepInEx/config/com.tyukki.mystia-steward-companion.cfg",
                "synthetic legacy settings",
            ),
            (
                "BepInEx/config/MystiaStewardCompanion/favorites.json",
                "synthetic protected data",
            ),
            ("BepInEx/core/core.dll", "synthetic protected loader"),
            (
                "BepInEx/plugins/mystia-steward-companion/user-data/note.txt",
                "synthetic preserved bytes",
            ),
        ] {
            let path = game.join(relative);
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(path, value).unwrap();
        }
        let package = base.join("package");
        package_manifest::fixture(&package);
        let save = base.join("save");
        fs::create_dir(&save).unwrap();
        fs::write(save.join("slot.memory"), "synthetic game save").unwrap();
        Options {
            game,
            package,
            save,
            backup: base.join("records"),
            execute: false,
        }
    }

    /// 此专项只在显式提供已核对公开 ZIP 的外部测试脚本中运行；不复制或执行用户游戏安装。
    /// 新版使用人工完整清单夹具，因此通过只证明迁移/保护/回退流程，不证明正式新版载荷能运行。
    #[test]
    #[ignore = "requires verified public v1.3.1 ZIP extracted into an isolated fixture"]
    fn real_public_package_migration() {
        let source = PathBuf::from(
            std::env::var_os("MYSTIA_TEST_V131_ROOT").expect("verified fixture source"),
        );
        let root = PathBuf::from(
            std::env::var_os("MYSTIA_TEST_MIGRATION_ROOT").expect("isolated test root"),
        );
        assert!(
            root.is_absolute()
                && root
                    .file_name()
                    .unwrap()
                    .to_string_lossy()
                    .starts_with("mystia-v131-fixture-")
        );
        ensure_no_links(&root).unwrap();
        assert!(!root.exists());
        fs::create_dir(&root).unwrap();
        validate_legacy(&source).unwrap();
        // 普通更新器继续拒绝公开旧包；离线兼容不能渗入常规成套更新的准入条件。
        assert!(package_manifest::validate(&source, true).is_err());

        let mut options = fixture(&root, &source, "success");
        let before = snapshot(options.game.parent().unwrap()).unwrap();
        let preview = perform(&options, &mut |_, _| Ok(())).unwrap();
        assert!(preview.contains("validated-only"));
        same(
            &before,
            &snapshot(options.game.parent().unwrap()).unwrap(),
            "read-only preview",
        )
        .unwrap();
        let protected = protection(&options).unwrap();
        let plugin = options
            .game
            .join("BepInEx/plugins/mystia-steward-companion");
        let original = snapshot(&plugin).unwrap();
        options.execute = true;
        assert!(perform(&options, &mut |_, _| Ok(()))
            .unwrap()
            .contains("installed"));
        package_manifest::validate(&plugin, true).unwrap();
        same(
            &protected,
            &protection(&options).unwrap(),
            "successful protection",
        )
        .unwrap();
        same(
            &original,
            &snapshot(&options.backup.join("original-copy")).unwrap(),
            "immutable original copy",
        )
        .unwrap();
        same(
            &original,
            &snapshot(&options.backup.join("plugin-backup")).unwrap(),
            "renamed original backup",
        )
        .unwrap();
        assert!(perform(&options, &mut |_, _| Ok(()))
            .unwrap_err()
            .contains("already exists"));

        let mut unknown = fixture(&root, &source, "unknown");
        unknown.execute = true;
        fs::write(
            unknown
                .game
                .join("BepInEx/plugins/mystia-steward-companion/unknown.bin"),
            "retain this",
        )
        .unwrap();
        assert!(perform(&unknown, &mut |_, _| Ok(())).is_err());
        assert!(!unknown.backup.exists());

        let mut modified = fixture(&root, &source, "modified-old");
        modified.execute = true;
        fs::write(
            modified.game.join(
                "BepInEx/plugins/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll",
            ),
            "modified",
        )
        .unwrap();
        assert!(perform(&modified, &mut |_, _| Ok(())).is_err());
        assert!(!modified.backup.exists());

        let mut conflict = fixture(&root, &source, "new-package-conflict");
        conflict.execute = true;
        fs::create_dir(conflict.package.join("user-data")).unwrap();
        fs::write(
            conflict.package.join("user-data/note.txt"),
            "conflicting new payload",
        )
        .unwrap();
        assert!(perform(&conflict, &mut |_, _| Ok(())).is_err());
        assert!(!conflict.backup.exists());

        let mut interrupted = fixture(&root, &source, "interrupted");
        interrupted.execute = true;
        let old = snapshot(
            &interrupted
                .game
                .join("BepInEx/plugins/mystia-steward-companion"),
        )
        .unwrap();
        assert!(
            perform(&interrupted, &mut |point, _| if point == "after-backup" {
                Err("simulated interruption before replacement".into())
            } else {
                Ok(())
            })
            .is_err()
        );
        same(
            &old,
            &snapshot(
                &interrupted
                    .game
                    .join("BepInEx/plugins/mystia-steward-companion"),
            )
            .unwrap(),
            "interrupted installation",
        )
        .unwrap();
        assert!(interrupted.backup.join("original-copy").is_dir());
        assert!(perform(&interrupted, &mut |_, _| Ok(()))
            .unwrap_err()
            .contains("already exists"));

        let mut rejected = fixture(&root, &source, "post-install-failure");
        rejected.execute = true;
        let old = snapshot(
            &rejected
                .game
                .join("BepInEx/plugins/mystia-steward-companion"),
        )
        .unwrap();
        let protected = protection(&rejected).unwrap();
        assert!(perform(&rejected, &mut |point, path| {
            if point == "after-replacement" {
                fs::write(
                    path.join("MystiaStewardCompanion.Business.dll"),
                    "injected corruption",
                )
                .unwrap();
            }
            Ok(())
        })
        .is_err());
        same(
            &old,
            &snapshot(
                &rejected
                    .game
                    .join("BepInEx/plugins/mystia-steward-companion"),
            )
            .unwrap(),
            "full rollback",
        )
        .unwrap();
        same(
            &protected,
            &protection(&rejected).unwrap(),
            "rollback protection",
        )
        .unwrap();
        assert!(rejected.backup.join("rejected-package").is_dir());
        assert!(rejected.backup.join("original-copy").is_dir());

        // 改成另一份结构仍有效的清单，不能被“校验当前包自洽”误当成本次所选包成功安装。
        let mut changed_manifest = fixture(&root, &source, "self-consistent-manifest-change");
        changed_manifest.execute = true;
        let old = snapshot(
            &changed_manifest
                .game
                .join("BepInEx/plugins/mystia-steward-companion"),
        )
        .unwrap();
        assert!(perform(&changed_manifest, &mut |point, path| {
            if point == "after-replacement" {
                let manifest = path.join("package-manifest.json");
                let mut value: serde_json::Value =
                    serde_json::from_slice(&fs::read(&manifest).unwrap()).unwrap();
                value["version"] = serde_json::json!("99.0.0");
                fs::write(&manifest, serde_json::to_vec_pretty(&value).unwrap()).unwrap();
                package_manifest::validate(path, true).unwrap();
            }
            Ok(())
        })
        .unwrap_err()
        .contains("selected package"));
        same(
            &old,
            &snapshot(
                &changed_manifest
                    .game
                    .join("BepInEx/plugins/mystia-steward-companion"),
            )
            .unwrap(),
            "manifest replacement rollback",
        )
        .unwrap();

        let mut overlap = fixture(&root, &source, "overlap");
        overlap.execute = true;
        overlap.backup = overlap.game.join("records");
        assert!(perform(&overlap, &mut |_, _| Ok(())).is_err());
        assert!(!overlap.backup.exists());

        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            let mut locked = fixture(&root, &source, "locked");
            locked.execute = true;
            let _handle = fs::OpenOptions::new().read(true).share_mode(0).open(locked.game.join("BepInEx/plugins/mystia-steward-companion/companion/mystia-steward-companion.exe")).unwrap();
            assert!(perform(&locked, &mut |_, _| Ok(())).is_err());
            assert!(!locked.backup.exists());
        }
        println!("Verified public v1.3.1: preview, install, protection, retained backups, unknown/modified/conflicting input, interrupted retry, final-validation rollback, overlap and locked payload refusal. No game executable was started.");
    }
}
