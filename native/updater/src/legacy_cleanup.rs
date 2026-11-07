//! 公开 v1.3.1 残留的显式离线清理。预览不写盘；执行绑定同一份文件身份与内容计划。
//!
//! 永久删除不具备多文件原子回滚能力。执行前完成全部准入，随后逐项记录结果；中途失败停止，
//! 已删除文件不伪装成可以恢复的事务。业务配置、共享父目录和未知附加文件均不推断为垃圾。
use crate::{legacy_cleanup_layout, offline_migration, package_manifest};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

const ZIP_HASH: &str = "e916a73a0ef9c692ea628b499b08087f44c8b1e8c0c0296b8a0391cf02511389";
const MAX_OBJECTS: usize = 32768;
const MAX_RECORD: u64 = 4 * 1024 * 1024;

#[derive(Default)]
struct Options {
    game: PathBuf,
    client_data: bool,
    backup: Option<PathBuf>,
    zip: Option<PathBuf>,
    execute: bool,
    confirmed: bool,
    digest: Option<String>,
}

/// 文件标识与摘要同时参与授权，拒绝“同路径换文件”以及内容相同但身份已变的执行请求。
#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
struct Bound {
    path: PathBuf,
    scope: PathBuf,
    directory: bool,
    bytes: u64,
    sha256: String,
    identity: String,
    attributes: u32,
    written: u64,
    children: Vec<String>,
}
#[derive(Clone, Serialize)]
struct Candidate {
    object: Bound,
    reason: String,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Plan {
    schema_version: u32,
    game: PathBuf,
    files: Vec<Candidate>,
    directories: Vec<Bound>,
    evidence: Vec<Bound>,
    warnings: Vec<String>,
}

/// 正式入口从 Known Folders 获取当前用户的旧客户端位置；测试只通过私有函数注入人工目录。
pub fn run(args: &[String]) -> Result<String, String> {
    let options = parse(args)?;
    let roots = if options.client_data {
        crate::legacy_cleanup_client::legacy_roots()?
    } else {
        Vec::new()
    };
    run_with_roots(&options, &roots, &mut |_, _| Ok(()))
}

fn parse(args: &[String]) -> Result<Options, String> {
    let mut result = Options::default();
    let mut seen = BTreeSet::new();
    let mut index = 0;
    while index < args.len() {
        let key = args[index].as_str();
        if !seen.insert(key) {
            return Err(format!("duplicate cleanup argument: {key}"));
        }
        match key {
            "--execute" => result.execute = true,
            "--confirm-migrated" => result.confirmed = true,
            "--include-legacy-client-data" => result.client_data = true,
            "--game-dir" | "--migration-backup-dir" | "--legacy-package-zip" | "--plan-sha256" => {
                index += 1;
                let value = args
                    .get(index)
                    .filter(|value| !value.starts_with("--"))
                    .ok_or_else(|| format!("missing value for {key}"))?;
                match key {
                    "--game-dir" => result.game = PathBuf::from(value),
                    "--migration-backup-dir" => result.backup = Some(PathBuf::from(value)),
                    "--legacy-package-zip" => result.zip = Some(PathBuf::from(value)),
                    _ => result.digest = Some(value.clone()),
                }
            }
            _ => return Err(format!("unknown cleanup argument: {key}")),
        }
        index += 1;
    }
    if result.game.as_os_str().is_empty() {
        return Err("missing --game-dir".into());
    }
    if result.execute
        && (!result.confirmed
            || !result
                .digest
                .as_ref()
                .is_some_and(|value| valid_hash(value)))
    {
        return Err("execution requires --confirm-migrated and the preview --plan-sha256".into());
    }
    if !result.execute && (result.confirmed || result.digest.is_some()) {
        return Err("confirmation and plan digest are execution-only arguments".into());
    }
    Ok(result)
}
fn valid_hash(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}
fn ordinary(path: &Path) -> Result<PathBuf, String> {
    if !path.is_absolute() {
        return Err("cleanup paths must be absolute".into());
    }
    offline_migration::ensure_no_links(path)?;
    path.canonicalize().map_err(|error| error.to_string())
}
fn exists(path: &Path) -> Result<bool, String> {
    path.try_exists().map_err(|error| error.to_string())
}
fn path_key(path: &Path) -> String {
    path.to_string_lossy()
        .replace('/', "\\")
        .trim_start_matches("\\\\?\\")
        .to_lowercase()
}
fn inside(path: &Path, scope: &Path) -> bool {
    let value = path_key(path);
    let prefix = path_key(scope);
    value == prefix || value.starts_with(&(prefix.trim_end_matches('\\').to_string() + "\\"))
}
fn overlaps(a: &Path, b: &Path) -> bool {
    inside(a, b) || inside(b, a)
}
/// 缺失候选也规范化现存祖先，用于重跑空计划的边界判定；不创建其父目录。
fn normalized_selection(path: &Path) -> Result<PathBuf, String> {
    if !path.is_absolute()
        || path.components().any(|value| {
            matches!(
                value,
                std::path::Component::ParentDir | std::path::Component::CurDir
            )
        })
    {
        return Err("noncanonical cleanup selection".into());
    }
    offline_migration::ensure_no_links(path)?;
    if exists(path)? {
        return ordinary(path);
    }
    let parent = path.parent().ok_or("missing selection parent")?;
    Ok(normalized_selection(parent)?.join(path.file_name().ok_or("missing selection name")?))
}
fn validate_client_scopes(
    game: &Path,
    backup: Option<&Path>,
    roots: &[PathBuf],
) -> Result<(), String> {
    let flutter = roots
        .first()
        .and_then(|path| path.parent())
        .map(|parent| parent.join("mystia-steward-companion/flutter-v1"));
    let flutter = flutter.as_deref().map(normalized_selection).transpose()?;
    for root in roots {
        let scope = if root
            .file_name()
            .is_some_and(|name| name == ".window-state.json")
        {
            root.parent().ok_or("missing preference parent")?
        } else {
            root.as_path()
        };
        let scope = normalized_selection(scope)?;
        if overlaps(&scope, game)
            || backup.is_some_and(|path| overlaps(&scope, path))
            || flutter.as_ref().is_some_and(|path| overlaps(&scope, path))
        {
            return Err(
                "legacy client data overlaps game, migration evidence, or Flutter storage".into(),
            );
        }
    }
    Ok(())
}

/// 每个候选先检查所有祖先，再经已打开句柄确认最终路径；路径文本不是删除对象的唯一依据。
#[cfg(windows)]
fn open_object(path: &Path, delete: bool) -> Result<fs::File, String> {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::{
        Foundation::GENERIC_READ,
        Storage::FileSystem::{
            DELETE, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ,
        },
    };
    offline_migration::ensure_no_links(path)?;
    fs::OpenOptions::new()
        .read(true)
        .access_mode(GENERIC_READ | if delete { DELETE } else { 0 })
        // 允许只读复验；任何写入、重命名或另一个 DELETE 请求均不被共享准入。
        .share_mode(FILE_SHARE_READ)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)
        .map_err(|error| format!("cannot exclusively inspect {}: {error}", path.display()))
}
#[cfg(not(windows))]
fn open_object(_: &Path, _: bool) -> Result<fs::File, String> {
    Err("legacy cleanup is supported only on Windows".into())
}

#[cfg(windows)]
fn handle_metadata(
    file: &fs::File,
    path: &Path,
    scope: &Path,
) -> Result<(String, u32, u64, bool, u64), String> {
    use std::os::windows::{ffi::OsStringExt, io::AsRawHandle};
    use windows_sys::Win32::Storage::FileSystem::{
        GetFileInformationByHandle, GetFinalPathNameByHandleW, BY_HANDLE_FILE_INFORMATION,
        FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT,
    };
    let mut information: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
    if unsafe { GetFileInformationByHandle(file.as_raw_handle(), &mut information) } == 0 {
        return Err(std::io::Error::last_os_error().to_string());
    }
    let directory = information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY != 0;
    if information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0
        || (!directory && information.nNumberOfLinks != 1)
    {
        return Err("linked or multiply linked cleanup object rejected".into());
    }
    let mut buffer = vec![0u16; 32768];
    let count = unsafe {
        GetFinalPathNameByHandleW(
            file.as_raw_handle(),
            buffer.as_mut_ptr(),
            buffer.len() as u32,
            0,
        )
    };
    if count == 0 || count as usize >= buffer.len() {
        return Err("cannot resolve cleanup handle identity".into());
    }
    let resolved = PathBuf::from(std::ffi::OsString::from_wide(&buffer[..count as usize]));
    if !inside(&resolved, scope) || path_key(&resolved) != path_key(path) {
        return Err("cleanup handle escaped its approved path".into());
    }
    let identity = format!(
        "{:08x}:{:08x}{:08x}",
        information.dwVolumeSerialNumber, information.nFileIndexHigh, information.nFileIndexLow
    );
    let written = (u64::from(information.ftLastWriteTime.dwHighDateTime) << 32)
        | u64::from(information.ftLastWriteTime.dwLowDateTime);
    let bytes = (u64::from(information.nFileSizeHigh) << 32) | u64::from(information.nFileSizeLow);
    Ok((
        identity,
        information.dwFileAttributes,
        written,
        directory,
        bytes,
    ))
}
#[cfg(not(windows))]
fn handle_metadata(
    _: &fs::File,
    _: &Path,
    _: &Path,
) -> Result<(String, u32, u64, bool, u64), String> {
    Err("legacy cleanup is supported only on Windows".into())
}

fn inspect_handle(
    file: &mut fs::File,
    path: &Path,
    scope: &Path,
    children: Vec<String>,
) -> Result<Bound, String> {
    let (identity, attributes, written, directory, bytes) = handle_metadata(file, path, scope)?;
    let sha256 = if directory {
        String::new()
    } else {
        let mut digest = Sha256::new();
        let mut buffer = [0u8; 65536];
        loop {
            let length = file.read(&mut buffer).map_err(|error| error.to_string())?;
            if length == 0 {
                break;
            }
            digest.update(&buffer[..length]);
        }
        format!("{:x}", digest.finalize())
    };
    Ok(Bound {
        path: path.into(),
        scope: scope.into(),
        directory,
        bytes: if directory { 0 } else { bytes },
        sha256,
        identity,
        attributes,
        written,
        children,
    })
}
fn children(path: &Path) -> Result<Vec<String>, String> {
    let mut names = Vec::new();
    let mut folded = BTreeSet::new();
    for entry in fs::read_dir(path).map_err(|error| error.to_string())? {
        let entry = entry.map_err(|error| error.to_string())?;
        let name = entry
            .file_name()
            .into_string()
            .map_err(|_| "non-UTF8 cleanup entry")?;
        if !folded.insert(name.to_lowercase()) {
            return Err("case-conflicting cleanup entries".into());
        }
        names.push(name);
    }
    names.sort();
    Ok(names)
}
fn inspect(path: &Path, scope: &Path) -> Result<Bound, String> {
    let path = ordinary(path)?;
    let scope = ordinary(scope)?;
    let list = if path.is_dir() {
        children(&path)?
    } else {
        Vec::new()
    };
    let mut file = open_object(&path, false)?;
    inspect_handle(&mut file, &path, &scope, list)
}
fn file_fingerprint(object: &Bound) -> String {
    format!("F:{}:{}", object.bytes, object.sha256)
}

struct Builder {
    plan: Plan,
    files: BTreeSet<String>,
    evidence: BTreeSet<String>,
    directories: BTreeSet<String>,
}
impl Builder {
    fn new(game: PathBuf) -> Self {
        Self {
            plan: Plan {
                schema_version: 1,
                game,
                files: Vec::new(),
                directories: Vec::new(),
                evidence: Vec::new(),
                warnings: Vec::new(),
            },
            files: BTreeSet::new(),
            evidence: BTreeSet::new(),
            directories: BTreeSet::new(),
        }
    }
    fn file(&mut self, path: &Path, scope: &Path, reason: &str) -> Result<(), String> {
        let object = inspect(path, scope)?;
        self.bound_file(object, reason)
    }
    fn bound_file(&mut self, object: Bound, reason: &str) -> Result<(), String> {
        if object.directory || object.attributes & 1 != 0 {
            return Err("cleanup candidate is not an ordinary writable file".into());
        }
        if self.evidence.contains(&path_key(&object.path)) {
            return Err("cleanup candidate overlaps protected evidence".into());
        }
        if !self.files.insert(path_key(&object.path)) {
            return Err("overlapping cleanup candidates".into());
        }
        self.plan.files.push(Candidate {
            object,
            reason: reason.into(),
        });
        self.limit()
    }
    fn evidence(&mut self, path: &Path, scope: &Path) -> Result<(), String> {
        let object = inspect(path, scope)?;
        self.bound_evidence(object)
    }
    fn bound_evidence(&mut self, object: Bound) -> Result<(), String> {
        if self.files.contains(&path_key(&object.path)) {
            return Err("cleanup evidence overlaps a deletion candidate".into());
        }
        if self.evidence.insert(path_key(&object.path)) {
            self.plan.evidence.push(object);
        }
        self.limit()
    }
    fn directory(&mut self, path: &Path, scope: &Path) -> Result<(), String> {
        let object = inspect(path, scope)?;
        if !object.directory {
            return Err("cleanup directory candidate is not a directory".into());
        }
        if self.directories.insert(path_key(&object.path)) {
            self.plan.directories.push(object);
        }
        self.limit()
    }
    fn limit(&self) -> Result<(), String> {
        if self.plan.files.len() + self.plan.evidence.len() + self.plan.directories.len()
            > MAX_OBJECTS
        {
            Err("cleanup object count exceeds conservative limit".into())
        } else {
            Ok(())
        }
    }
    fn finish(mut self) -> Plan {
        // 自底向上推导“计划完成后为空”，而非仅因目录属于旧版就把它列为删除候选。
        // 未列入清理的用户文件或子目录会阻止祖先进入计划，重复执行因而不会为永久非空根制造日志。
        let mut removable = self.files.clone();
        let mut kept_directories = BTreeSet::new();
        let mut ordered: Vec<_> = self.plan.directories.iter().collect();
        ordered.sort_by_key(|value| std::cmp::Reverse(value.path.components().count()));
        for directory in ordered {
            if directory
                .children
                .iter()
                .all(|name| removable.contains(&path_key(&directory.path.join(name))))
            {
                let key = path_key(&directory.path);
                removable.insert(key.clone());
                kept_directories.insert(key);
            } else {
                self.plan.warnings.push(format!(
                    "保留含非清理项的目录：{}",
                    directory.path.display()
                ));
            }
        }
        self.plan
            .directories
            .retain(|value| kept_directories.contains(&path_key(&value.path)));
        self.plan
            .files
            .sort_by(|a, b| a.object.path.cmp(&b.object.path));
        self.plan.directories.sort_by(|a, b| a.path.cmp(&b.path));
        self.plan.evidence.sort_by(|a, b| a.path.cmp(&b.path));
        self.plan.warnings.sort();
        self.plan
    }
}

fn known_payload(path: &Path, name: &str, scope: &Path) -> Result<Option<Bound>, String> {
    let entry = offline_migration::LEGACY_FILES
        .iter()
        .find(|entry| entry.0 == name)
        .ok_or("unknown legacy payload")?;
    let object = inspect(path, scope)?;
    Ok(
        (!object.directory && object.bytes == entry.1 && object.sha256 == entry.2)
            .then_some(object),
    )
}
fn read_record<T: for<'a> Deserialize<'a>>(path: &Path) -> Result<(T, Bound), String> {
    let path = ordinary(path)?;
    let scope = path.parent().ok_or("evidence record parent missing")?;
    let mut file = open_object(&path, false)?;
    let mut bytes = Vec::new();
    (&mut file)
        .take(MAX_RECORD + 1)
        .read_to_end(&mut bytes)
        .map_err(|error| error.to_string())?;
    if bytes.is_empty() || bytes.len() as u64 > MAX_RECORD {
        return Err("invalid cleanup evidence record size".into());
    }
    let parsed = serde_json::from_slice(&bytes).map_err(|error| error.to_string())?;
    file.seek(SeekFrom::Start(0))
        .map_err(|error| error.to_string())?;
    // 解析和绑定同一个受锁句柄，不能将解析后的另一份文件内容偷换成已验证的依据。
    let bound = inspect_handle(&mut file, &path, scope, Vec::new())?;
    Ok((parsed, bound))
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct MigrationResult {
    mode: String,
    source_version: String,
    target_version: String,
    game_directory: PathBuf,
    backup_directory: PathBuf,
    writes_performed: bool,
}

/// 安装备份只释放三项公开程序载荷；旧日志、导出和用户数据即使存在于备份中也逐项保留。
fn discover_backup(builder: &mut Builder, backup: &Path) -> Result<(), String> {
    let backup = ordinary(backup)?;
    if inside(&backup, &builder.plan.game) || inside(&builder.plan.game, &backup) {
        return Err("migration backup overlaps game directory".into());
    }
    let result_path = backup.join("result.json");
    let before_path = backup.join("before-plugin.json");
    let (result, result_bound): (MigrationResult, _) = read_record(&result_path)?;
    if result.mode != "installed"
        || result.source_version != "1.3.1"
        || result.target_version.is_empty()
        || !result.writes_performed
        || path_key(&ordinary(&result.game_directory)?) != path_key(&builder.plan.game)
        || path_key(&ordinary(&result.backup_directory)?) != path_key(&backup)
    {
        return Err("backup is not a successful v1.3.1 migration for this game".into());
    }
    let (before, before_bound): (BTreeMap<String, String>, _) = read_record(&before_path)?;
    for (name, size, hash) in offline_migration::LEGACY_FILES {
        if before.get(name) != Some(&format!("F:{size}:{hash}")) {
            return Err("migration evidence does not identify public v1.3.1".into());
        }
    }
    builder.bound_evidence(result_bound)?;
    builder.bound_evidence(before_bound)?;
    for leaf in ["original-copy", "plugin-backup"] {
        let root = backup.join(leaf);
        if !exists(&root)? {
            continue;
        }
        let mut actual = BTreeMap::new();
        let mut queue = vec![root.clone()];
        while let Some(path) = queue.pop() {
            let object = inspect(&path, &root)?;
            let relative = if path == root {
                ".".into()
            } else {
                path.strip_prefix(&root)
                    .map_err(|error| error.to_string())?
                    .to_string_lossy()
                    .replace('\\', "/")
            };
            let value = if object.directory {
                "D".into()
            } else {
                file_fingerprint(&object)
            };
            if before.get(&relative) != Some(&value) {
                return Err("remaining backup object differs from the recorded original".into());
            }
            actual.insert(relative.clone(), value);
            if object.directory {
                for child in &object.children {
                    queue.push(path.join(child));
                }
            } else if offline_migration::LEGACY_FILES
                .iter()
                .any(|entry| entry.0 == relative)
            {
                builder.bound_file(object, "公开 v1.3.1 冗余程序备份")?;
            } else {
                builder.bound_evidence(object)?;
                builder
                    .plan
                    .warnings
                    .push(format!("保留备份中的附加用户文件：{}", path.display()));
            }
            if actual.len() > MAX_OBJECTS {
                return Err("backup tree exceeds conservative limit".into());
            }
        }
        for name in before.keys() {
            if !actual.contains_key(name)
                && name != "companion"
                && !offline_migration::LEGACY_FILES
                    .iter()
                    .any(|entry| entry.0 == name)
            {
                return Err(
                    "non-program backup data is missing; cleanup evidence is inconsistent".into(),
                );
            }
        }
        // 只考虑承载三项程序的专属目录，未列出的附加目录不参与空目录清理。
        builder.directory(&root, &root)?;
        if exists(&root.join("companion"))? {
            builder.directory(&root.join("companion"), &root)?;
        }
    }
    Ok(())
}

fn discover_client(builder: &mut Builder, path: &Path) -> Result<(), String> {
    offline_migration::ensure_no_links(path)?;
    if !exists(path)? {
        // 显式窗口偏好已清理后，仅考虑它原有的专属空父目录；不触碰更高的 Roaming 共享父目录。
        if path
            .file_name()
            .is_some_and(|name| name == ".window-state.json")
        {
            let parent = path.parent().ok_or("legacy preference parent missing")?;
            if exists(parent)? && children(parent)?.is_empty() {
                builder.directory(parent, parent)?;
            }
        }
        return Ok(());
    }
    let root = ordinary(path)?;
    let window_state = path
        .file_name()
        .is_some_and(|name| name == ".window-state.json");
    if window_state {
        if !root.is_file() {
            return Err("legacy window-state path must be an ordinary file".into());
        }
        let parent = root.parent().ok_or("legacy preference has no parent")?;
        builder.file(&root, parent, "显式选择的旧 Tauri 窗口偏好")?;
        builder.directory(parent, parent)?;
        return Ok(());
    }
    if !root.is_dir() {
        return Err("legacy WebView root must be a directory".into());
    }
    let mut queue = vec![root.clone()];
    while let Some(path) = queue.pop() {
        let object = inspect(&path, &root)?;
        if object.directory {
            builder.directory(&path, &root)?;
            for child in object.children {
                queue.push(path.join(child));
            }
        } else {
            builder.bound_file(object, "显式选择的旧 Tauri WebView 数据，删除后不可恢复")?;
        }
    }
    Ok(())
}

/// 清理不会替换当前插件，所以不对自身 EXE 做目录安装器的全载荷解锁检查。
/// 同名其他 updater、游戏及客户端仍须退出；实际删除对象随后以禁止写入和改名的句柄单独准入。
fn assert_cleanup_stopped(game: &Path) -> Result<(), String> {
    let mut names = std::collections::HashSet::from([
        "mystia-steward-companion.exe".to_string(),
        "mystia-steward-companion-updater.exe".to_string(),
    ]);
    let mut executables = Vec::new();
    for entry in fs::read_dir(game).map_err(|error| error.to_string())? {
        let path = entry.map_err(|error| error.to_string())?.path();
        if path.is_file()
            && path
                .extension()
                .is_some_and(|extension| extension.eq_ignore_ascii_case("exe"))
        {
            names.insert(path.file_name().unwrap().to_string_lossy().to_lowercase());
            executables.push(path);
        }
    }
    if executables.is_empty() {
        return Err("game root has no executable; target identity cannot be checked".into());
    }
    offline_migration::check_process_names(&names)?;
    for path in executables {
        crate::client_exit::assert_file_unlocked(&path)?;
    }
    Ok(())
}

fn build_plan(options: &Options, roots: &[PathBuf]) -> Result<Plan, String> {
    let game = ordinary(&options.game)?;
    if !game.is_dir() {
        return Err("game path must be a directory".into());
    }
    let selected_backup = options.backup.as_deref().map(ordinary).transpose()?;
    if options.client_data {
        validate_client_scopes(&game, selected_backup.as_deref(), roots)?;
    }
    let plugin = game.join("BepInEx/plugins/mystia-steward-companion");
    let manifest = package_manifest::validate(&plugin, true)?;
    assert_cleanup_stopped(&game)?;
    let proof = legacy_cleanup_layout::validate(&game)?;
    let mut builder = Builder::new(game.clone());
    builder.evidence(&plugin.join(package_manifest::NAME), &plugin)?;
    for entry in manifest.files {
        builder.evidence(&plugin.join(entry.path), &plugin)?;
    }
    for path in proof.evidence {
        let scope = path.parent().ok_or("evidence parent missing")?;
        builder.evidence(&path, scope)?;
    }
    if let Some(path) = proof.legacy_config {
        let scope = path.parent().ok_or("legacy CFG parent missing")?;
        builder.file(&path, scope, "已由完成的布局迁移原字节备份覆盖的旧 CFG")?;
    }
    let runner = game.join("BepInEx/config/MystiaStewardCompanion/updates/runner");
    offline_migration::ensure_no_links(&runner)?;
    if exists(&runner)? {
        for name in children(&runner)? {
            if name.len() != 14 || !name.bytes().all(|value| value.is_ascii_digit()) {
                builder
                    .plan
                    .warnings
                    .push(format!("保留未识别的 updater runner 项：{name}"));
                continue;
            }
            let root = runner.join(name);
            let exe = root.join("mystia-steward-companion-updater.exe");
            offline_migration::ensure_no_links(&root)?;
            if !root.is_dir() {
                continue;
            }
            // 时间戳名称不是旧版本身份证明：没有精确旧 EXE 的空 runner 也可能属于新版。
            if !exists(&exe)? {
                continue;
            }
            if let Some(object) =
                known_payload(&exe, "mystia-steward-companion-updater.exe", &root)?
            {
                builder.bound_file(object, "公开 v1.3.1 的旧 updater 运行副本")?;
                builder.directory(&root, &root)?;
            } else {
                builder.plan.warnings.push(format!(
                    "保留非公开 v1.3.1 的 updater runner：{}",
                    root.display()
                ));
            }
        }
    }
    if let Some(path) = &options.backup {
        discover_backup(&mut builder, path)?;
    }
    if let Some(path) = &options.zip {
        if !path.is_absolute() {
            return Err("legacy ZIP path must be absolute".into());
        }
        offline_migration::ensure_no_links(path)?;
        let selected = normalized_selection(path)?;
        if inside(&selected, &game)
            && path_key(selected.parent().ok_or("ZIP parent missing")?)
                != path_key(&game.join("BepInEx/plugins"))
        {
            return Err(
                "legacy ZIP inside the game is allowed only directly under BepInEx/plugins".into(),
            );
        }
        // 同一显式选择在成功清理后可以重新预览；缺失仅表示零候选，不创建目录或猜测替代 ZIP。
        if exists(path)? {
            let path = ordinary(path)?;
            let scope = path.parent().ok_or("legacy ZIP parent missing")?;
            let object = inspect(&path, scope)?;
            if object.directory || object.sha256 != ZIP_HASH {
                return Err("selected ZIP does not match the public v1.3.1 archive".into());
            }
            builder.bound_file(object, "精确匹配公开 v1.3.1 的旧安装 ZIP")?;
        }
    }
    if options.client_data {
        builder.plan.warnings.push("已显式选择旧客户端私有数据：旧连接记录、偏好与 WebView 状态将不可恢复；不会删除 Flutter 数据。".into());
        for root in roots {
            discover_client(&mut builder, root)?;
        }
    }
    builder.plan.warnings.push(
        "永久删除按对象逐项执行；部分完成后不能自动回滚。布局迁移备份和未知附加文件保留。".into(),
    );
    Ok(builder.finish())
}
fn digest(plan: &Plan) -> Result<String, String> {
    Ok(format!(
        "{:x}",
        Sha256::digest(serde_json::to_vec(plan).map_err(|error| error.to_string())?)
    ))
}
fn preview(plan: &Plan, digest: &str) -> serde_json::Value {
    let mut candidates: Vec<_> = plan.files.iter().map(|candidate| serde_json::json!({"path":candidate.object.path,"bytes":candidate.object.bytes,"reason":candidate.reason,"kind":"file"})).collect();
    candidates.extend(plan.directories.iter().map(|object| serde_json::json!({"path":object.path,"bytes":0,"reason":"仅在删除计划文件后为空时移除专属目录","kind":"directory"})));
    serde_json::json!({"mode":"preview","planSha256":digest,"candidates":candidates,"totalBytes":plan.files.iter().map(|value|value.object.bytes).sum::<u64>(),"warnings":plan.warnings,"evidenceCount":plan.evidence.len()})
}

fn run_with_roots(
    options: &Options,
    roots: &[PathBuf],
    checkpoint: &mut impl FnMut(&str, usize) -> Result<(), String>,
) -> Result<String, String> {
    let plan = build_plan(options, roots)?;
    let hash = digest(&plan)?;
    if !options.execute {
        return serde_json::to_string_pretty(&preview(&plan, &hash))
            .map_err(|error| error.to_string());
    }
    if options.digest.as_deref() != Some(&hash) {
        return Err(
            "cleanup plan changed; inspect a new read-only preview before execution".into(),
        );
    }
    execute(&plan, &hash, checkpoint)
}

/// 记录只含路径、摘要与结果；不复制配置/WebView 数据，也不把凭据内容写入日志。
fn record<T: Serialize>(root: &Path, name: &str, value: &T) -> Result<(), String> {
    offline_migration::ensure_no_links(root)?;
    let path = root.join(name);
    let mut file = fs::OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(path)
        .map_err(|error| error.to_string())?;
    file.write_all(&serde_json::to_vec_pretty(value).map_err(|error| error.to_string())?)
        .and_then(|_| file.sync_all())
        .map_err(|error| error.to_string())
}
/// 使用单个追加日志承载全部逐项事件，避免清理上万缓存后又产生上万小文件。
fn event(file: &mut fs::File, kind: &str, index: usize, state: &str) -> Result<(), String> {
    let mut bytes =
        serde_json::to_vec(&serde_json::json!({"kind":kind,"index":index,"state":state}))
            .map_err(|error| error.to_string())?;
    bytes.push(b'\n');
    file.write_all(&bytes)
        .and_then(|_| file.sync_all())
        .map_err(|error| error.to_string())
}
fn revalidate_authority(plan: &Plan) -> Result<(), String> {
    assert_cleanup_stopped(&plan.game)?;
    let plugin = plan.game.join("BepInEx/plugins/mystia-steward-companion");
    package_manifest::validate(&plugin, true)?;
    let proof = legacy_cleanup_layout::validate(&plan.game)?;
    for path in proof.evidence {
        if !plan
            .evidence
            .iter()
            .any(|bound| path_key(&bound.path) == path_key(&path))
        {
            return Err("locked layout proof differs from the preview evidence set".into());
        }
    }
    let legacy = plan
        .game
        .join("BepInEx/config/com.tyukki.mystia-steward-companion.cfg");
    let selected = plan
        .files
        .iter()
        .any(|candidate| path_key(&candidate.object.path) == path_key(&legacy));
    if selected != proof.legacy_config.is_some() {
        return Err("locked legacy CFG proof differs from the deletion plan".into());
    }
    Ok(())
}
#[cfg(windows)]
fn dispose(file: fs::File) -> Result<(), String> {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        FileDispositionInfo, SetFileInformationByHandle, FILE_DISPOSITION_INFO,
    };
    let info = FILE_DISPOSITION_INFO { DeleteFile: true };
    if unsafe {
        SetFileInformationByHandle(
            file.as_raw_handle(),
            FileDispositionInfo,
            &info as *const _ as *const _,
            std::mem::size_of::<FILE_DISPOSITION_INFO>() as u32,
        )
    } == 0
    {
        return Err(std::io::Error::last_os_error().to_string());
    }
    drop(file);
    Ok(())
}
#[cfg(not(windows))]
fn dispose(_: fs::File) -> Result<(), String> {
    Err("legacy cleanup is supported only on Windows".into())
}

fn execute(
    plan: &Plan,
    hash: &str,
    checkpoint: &mut impl FnMut(&str, usize) -> Result<(), String>,
) -> Result<String, String> {
    execute_engine(plan, hash, checkpoint, &mut || revalidate_authority(plan))
}
fn execute_engine(
    plan: &Plan,
    hash: &str,
    checkpoint: &mut impl FnMut(&str, usize) -> Result<(), String>,
    revalidate: &mut impl FnMut() -> Result<(), String>,
) -> Result<String, String> {
    // 所有候选同时取得禁止写/改名的读/DELETE 句柄。只读共享用于锁内语义复验；失败时尚未写记录。
    let mut handles = Vec::new();
    for candidate in &plan.files {
        let mut file = open_object(&candidate.object.path, true)?;
        let now = inspect_handle(
            &mut file,
            &candidate.object.path,
            &candidate.object.scope,
            Vec::new(),
        )?;
        if now != candidate.object {
            return Err("cleanup file identity/content changed before deletion".into());
        }
        handles.push(Some(file));
    }
    // 证据句柄在整个删除阶段保留，只允许并行读取，不允许写入/改名；候选与证据相交已被拒绝。
    let mut evidence_handles = Vec::new();
    for evidence in &plan.evidence {
        let list = if evidence.directory {
            children(&evidence.path)?
        } else {
            Vec::new()
        };
        let mut file = open_object(&evidence.path, false)?;
        if inspect_handle(&mut file, &evidence.path, &evidence.scope, list)? != *evidence {
            return Err("cleanup authority evidence changed".into());
        }
        evidence_handles.push(file);
    }
    for directory in &plan.directories {
        if inspect(&directory.path, &directory.scope)? != *directory {
            return Err("cleanup directory changed after preview".into());
        }
    }
    // 文件句柄不被当作祖先目录锁使用：另外保持关键父链的只读、非 DELETE 共享句柄。
    // 删除专属空目录时仅释放其自己的守卫，父链继续保留；经句柄复核身份后才准许移除该对象。
    let mut anchor_paths = BTreeSet::new();
    for path in plan
        .files
        .iter()
        .map(|value| &value.object.path)
        .chain(plan.evidence.iter().map(|value| &value.path))
        .chain(plan.directories.iter().map(|value| &value.path))
    {
        for parent in path
            .parent()
            .into_iter()
            .flat_map(|value| value.ancestors())
        {
            if parent.file_name().is_some() {
                anchor_paths.insert(parent.to_path_buf());
            }
        }
    }
    for directory in &plan.directories {
        anchor_paths.insert(directory.path.clone());
    }
    let mut anchors = BTreeMap::new();
    for path in anchor_paths {
        let file = open_object(&path, false)?;
        handle_metadata(&file, &path, &path)?;
        anchors.insert(path, file);
    }
    // 语义校验在同一批已锁定字节上重做，避免把首次验证与随后绑定的不同内容混为一份授权。
    revalidate()?;
    checkpoint("after-preflight", 0)?;
    checkpoint("before-journal", 0)?;
    if plan.files.is_empty() && plan.directories.is_empty() {
        return Ok(serde_json::json!({"mode":"cleaned","planSha256":hash,"deleted":[],"bytesDeleted":0,"removedDirectories":[],"retainedDirectories":[],"journalDirectory":null,"warnings":plan.warnings}).to_string());
    }
    let base = plan
        .game
        .join("BepInEx/config/MystiaStewardCompanion/legacy-cleanup");
    offline_migration::ensure_no_links(&base)?;
    fs::create_dir_all(&base).map_err(|error| error.to_string())?;
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| error.to_string())?
        .as_nanos();
    let journal = base.join(format!("{stamp}-{}", std::process::id()));
    fs::create_dir(&journal).map_err(|error| error.to_string())?;
    record(&journal, "plan.json", plan)?;
    let mut events = fs::OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(journal.join("events.jsonl"))
        .map_err(|error| error.to_string())?;
    let mut deleted = Vec::<PathBuf>::new();
    let mut removed = Vec::<PathBuf>::new();
    let mut retained = Vec::<PathBuf>::new();
    let mut bytes = 0u64;
    let result = (|| {
        for (index, candidate) in plan.files.iter().enumerate() {
            checkpoint("before-delete", index)?;
            // 删除意图先落盘；断电可能只留下 intent，后续预览依据现存对象重新确定剩余范围。
            let file = handles[index].as_ref().ok_or("missing cleanup handle")?;
            // 再次核对最终句柄路径；即使祖先被系统外部手段移动，也不得删除已离开批准位置的对象。
            let (identity, attributes, written, directory, size) =
                handle_metadata(file, &candidate.object.path, &candidate.object.scope)?;
            if directory
                || identity != candidate.object.identity
                || attributes != candidate.object.attributes
                || written != candidate.object.written
                || size != candidate.object.bytes
            {
                return Err("cleanup handle identity changed immediately before deletion".into());
            }
            event(&mut events, "file", index, "intent")?;
            dispose(handles[index].take().ok_or("missing cleanup handle")?)?;
            deleted.push(candidate.object.path.clone());
            bytes += candidate.object.bytes;
            event(&mut events, "file", index, "deleted")?;
            checkpoint("after-delete", index)?;
        }
        // 文件全部完成后才按深度逆序删除空目录，不使用 remove_dir_all，也不递归删除未列出的父目录。
        let mut directories: Vec<_> = plan.directories.iter().enumerate().collect();
        directories.sort_by_key(|(_, value)| std::cmp::Reverse(value.path.components().count()));
        // 事件索引始终引用 plan.json 原数组，不能用后序删除的临时排序索引解释审计日志。
        for (index, directory) in directories {
            if !children(&directory.path)?.is_empty() {
                retained.push(directory.path.clone());
                continue;
            }
            anchors.remove(&directory.path);
            let file = open_object(&directory.path, true)?;
            let (identity, attributes, _, is_directory, _) =
                handle_metadata(&file, &directory.path, &directory.scope)?;
            if !is_directory || identity != directory.identity || attributes != directory.attributes
            {
                return Err("cleanup directory identity changed".into());
            }
            event(&mut events, "directory", index, "intent")?;
            dispose(file)?;
            removed.push(directory.path.clone());
            event(&mut events, "directory", index, "deleted")?;
        }
        Ok::<(), String>(())
    })();
    let mut report = serde_json::json!({"mode":if result.is_ok(){"cleaned"}else{"partial"},"planSha256":hash,"deleted":deleted,"bytesDeleted":bytes,"removedDirectories":removed,"retainedDirectories":retained,"journalDirectory":journal,"warnings":plan.warnings});
    if let Err(error) = &result {
        report["error"] = serde_json::json!(error);
    }
    let recording = record(&journal, "result.json", &report);
    drop(evidence_handles);
    if let Err(error) = recording {
        report["mode"] = serde_json::json!("partial");
        report["recordingError"] = serde_json::json!(error);
        return Err(report.to_string());
    }
    let text = serde_json::to_string_pretty(&report).map_err(|error| error.to_string())?;
    if result.is_err() {
        Err(text)
    } else {
        Ok(text)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    // 仅用于引擎的人工文件场景；正式入口始终执行真实包/布局语义复验，不能由 CLI 关闭。
    #[cfg(windows)]
    fn execute_fixture(
        plan: &Plan,
        hash: &str,
        checkpoint: &mut impl FnMut(&str, usize) -> Result<(), String>,
    ) -> Result<String, String> {
        execute_engine(plan, hash, checkpoint, &mut || Ok(()))
    }
    #[test]
    fn cleanup_requires_explicit_consistent_authorization() {
        assert!(parse(&[]).is_err());
        assert!(parse(&["--game-dir".into(), "x".into(), "--execute".into()]).is_err());
        assert!(parse(&[
            "--game-dir".into(),
            "x".into(),
            "--game-dir".into(),
            "y".into()
        ])
        .is_err());
        assert!(parse(&["--game-dir".into(), "x".into(), "--confirm-migrated".into()]).is_err());
        assert!(parse(&[
            "--game-dir".into(),
            "x".into(),
            "--execute".into(),
            "--confirm-migrated".into(),
            "--plan-sha256".into(),
            "a".repeat(64)
        ])
        .is_ok());
    }
    #[cfg(windows)]
    fn fixture(name: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "mystia-cleanup-{name}-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir(&root).unwrap();
        root
    }
    #[cfg(windows)]
    #[test]
    fn cleanup_plan_binds_content_identity_and_rejects_hardlinks() {
        let root = fixture("identity");
        let file = root.join("old.bin");
        fs::write(&file, "old").unwrap();
        let mut builder = Builder::new(root.clone());
        builder.file(&file, &root, "fixture").unwrap();
        let plan = builder.finish();
        let original = digest(&plan).unwrap();
        fs::write(&file, "new").unwrap();
        let mut next = Builder::new(root.clone());
        next.file(&file, &root, "fixture").unwrap();
        assert_ne!(original, digest(&next.finish()).unwrap());
        assert!(execute_fixture(&plan, &original, &mut |_, _| Ok(())).is_err());
        assert!(file.exists());
        assert!(!root.join("BepInEx").exists());
        let link = root.join("alias.bin");
        fs::hard_link(&file, &link).unwrap();
        assert!(inspect(&file, &root).is_err());
    }
    #[cfg(windows)]
    #[test]
    fn cleanup_partial_failure_preserves_remaining_files_and_records_progress() {
        let root = fixture("partial");
        let first = root.join("a.bin");
        let second = root.join("b.bin");
        fs::write(&first, "a").unwrap();
        fs::write(&second, "b").unwrap();
        let evidence = root.join("protected.cfg");
        fs::write(&evidence, "synthetic credential sentinel").unwrap();
        let mut builder = Builder::new(root.clone());
        builder.file(&first, &root, "fixture").unwrap();
        builder.file(&second, &root, "fixture").unwrap();
        builder.evidence(&evidence, &root).unwrap();
        let plan = builder.finish();
        let error = execute_fixture(&plan, &digest(&plan).unwrap(), &mut |point, _| {
            if point == "after-delete" {
                Err("injected interruption".into())
            } else {
                Ok(())
            }
        })
        .unwrap_err();
        let report: serde_json::Value = serde_json::from_str(&error).unwrap();
        assert_eq!(report["mode"], "partial");
        assert_eq!(report["deleted"].as_array().unwrap().len(), 1);
        assert!(!first.exists());
        assert!(second.exists());
        assert_eq!(
            fs::read_to_string(evidence).unwrap(),
            "synthetic credential sentinel"
        );
        assert!(Path::new(report["journalDirectory"].as_str().unwrap())
            .join("result.json")
            .is_file());
        assert_eq!(
            fs::read_dir(report["journalDirectory"].as_str().unwrap())
                .unwrap()
                .count(),
            3
        );
    }
    #[cfg(windows)]
    #[test]
    fn cleanup_client_fixture_keeps_shared_parents_and_unknown_roaming_files() {
        // 仅注入人工路径，绝不调用正式 Known Folders 或通过环境变量覆盖用户资料根。
        let root = fixture("client");
        let local = root.join("legacy-local");
        let roaming = root.join("legacy-roaming");
        let flutter = root.join("flutter-v1");
        for path in [&local, &roaming, &flutter] {
            fs::create_dir(path).unwrap();
        }
        fs::write(local.join("old.leveldb"), "synthetic old webview").unwrap();
        fs::write(roaming.join(".window-state.json"), "{}").unwrap();
        fs::write(roaming.join("unknown.txt"), "keep").unwrap();
        fs::write(flutter.join("current.dpapi"), "keep").unwrap();
        let mut builder = Builder::new(root.clone());
        discover_client(&mut builder, &local).unwrap();
        discover_client(&mut builder, &roaming.join(".window-state.json")).unwrap();
        let plan = builder.finish();
        let report = execute_fixture(&plan, &digest(&plan).unwrap(), &mut |_, _| Ok(())).unwrap();
        assert!(report.contains("cleaned"));
        assert!(!local.exists());
        assert!(roaming.join("unknown.txt").exists());
        assert!(flutter.join("current.dpapi").exists());
        assert!(root.exists());
    }
    #[cfg(windows)]
    #[test]
    fn cleanup_rejects_client_scope_overlap_and_evidence_collision() {
        let root = fixture("scope");
        let game = root.join("game");
        let client = root.join("client");
        let backup = client.join("backup");
        fs::create_dir(&game).unwrap();
        fs::create_dir(&client).unwrap();
        fs::create_dir(&backup).unwrap();
        assert!(validate_client_scopes(&ordinary(&game).unwrap(), None, &[root.clone()]).is_err());
        assert!(validate_client_scopes(
            &ordinary(&game).unwrap(),
            Some(&ordinary(&backup).unwrap()),
            &[client]
        )
        .is_err());
        let protected = root.join("proof.json");
        fs::write(&protected, "{}").unwrap();
        let mut builder = Builder::new(game.clone());
        builder.evidence(&protected, &root).unwrap();
        assert!(builder.file(&protected, &root, "must reject").is_err());
        let mut builder = Builder::new(game);
        builder.file(&protected, &root, "fixture").unwrap();
        assert!(builder.evidence(&protected, &root).is_err());
    }
    #[cfg(windows)]
    #[test]
    fn cleanup_rejects_directory_impersonating_window_state_and_handles_empty_roots() {
        let root = fixture("kind");
        let fake = root.join(".window-state.json");
        fs::create_dir(&fake).unwrap();
        fs::write(fake.join("unknown.txt"), "keep").unwrap();
        let mut builder = Builder::new(root.clone());
        assert!(discover_client(&mut builder, &fake).is_err());
        assert!(fake.join("unknown.txt").exists());
        let fake_local = root.join("legacy-local-file");
        fs::write(&fake_local, "keep").unwrap();
        assert!(discover_client(&mut builder, &fake_local).is_err());
        let empty = root.join("empty-local");
        fs::create_dir(&empty).unwrap();
        let mut builder = Builder::new(root.clone());
        discover_client(&mut builder, &empty).unwrap();
        let plan = builder.finish();
        assert!(plan.files.is_empty());
        assert_eq!(plan.directories.len(), 1);
        execute_fixture(&plan, &digest(&plan).unwrap(), &mut |_, _| Ok(())).unwrap();
        assert!(!empty.exists());
    }
    #[cfg(windows)]
    #[test]
    fn cleanup_locks_allow_readonly_revalidation_and_block_parent_rename() {
        let root = fixture("parent");
        let approved = root.join("approved");
        fs::create_dir(&approved).unwrap();
        let file = approved.join("old.bin");
        fs::write(&file, "old").unwrap();
        let mut builder = Builder::new(root.clone());
        builder.file(&file, &approved, "fixture").unwrap();
        let plan = builder.finish();
        let relocated = root.join("moved-outside-scope");
        let mut moved = false;
        let result = execute_engine(
            &plan,
            &digest(&plan).unwrap(),
            &mut |point, _| {
                if point == "after-preflight" {
                    moved = fs::rename(&approved, &relocated).is_ok();
                }
                Ok(())
            },
            &mut || {
                assert_eq!(fs::read_to_string(&file).unwrap(), "old");
                assert!(fs::write(&file, "external edit").is_err());
                Ok(())
            },
        );
        if moved {
            assert!(result.is_err());
            assert!(relocated.join("old.bin").exists());
        } else {
            assert!(result.is_ok());
            assert!(!file.exists());
        }
    }
    #[cfg(windows)]
    #[test]
    fn cleanup_retained_user_data_makes_repeated_empty_execution_write_nothing() {
        let root = fixture("repeat-empty");
        let backup = root.join("original-copy");
        let companion = backup.join("companion");
        let user_data = backup.join("user-data");
        fs::create_dir_all(&companion).unwrap();
        fs::create_dir(&user_data).unwrap();
        let old_dll = backup.join("old.dll");
        let old_exe = companion.join("old.exe");
        let retained = user_data.join("note.txt");
        fs::write(&old_dll, "synthetic legacy DLL").unwrap();
        fs::write(&old_exe, "synthetic legacy EXE").unwrap();
        fs::write(&retained, "unique user data; keep").unwrap();
        let mut builder = Builder::new(root.clone());
        builder.file(&old_dll, &backup, "fixture").unwrap();
        builder.file(&old_exe, &backup, "fixture").unwrap();
        builder.evidence(&retained, &backup).unwrap();
        builder.directory(&backup, &backup).unwrap();
        builder.directory(&companion, &backup).unwrap();
        let plan = builder.finish();
        assert_eq!(plan.directories.len(), 1);
        assert_eq!(path_key(&plan.directories[0].path), path_key(&companion));
        execute_fixture(&plan, &digest(&plan).unwrap(), &mut |_, _| Ok(())).unwrap();
        assert!(!old_dll.exists());
        assert!(!companion.exists());
        assert_eq!(
            fs::read_to_string(&retained).unwrap(),
            "unique user data; keep"
        );

        // 按剩余对象重建计划：用户数据阻止旧根进入目录候选，空执行也不新增第二个日志目录。
        let journal_root = root.join("BepInEx/config/MystiaStewardCompanion/legacy-cleanup");
        let records_before = fs::read_dir(&journal_root).unwrap().count();
        let mut builder = Builder::new(root);
        builder.evidence(&retained, &backup).unwrap();
        builder.directory(&backup, &backup).unwrap();
        let repeated = builder.finish();
        assert!(repeated.files.is_empty());
        assert!(repeated.directories.is_empty());
        let report: serde_json::Value = serde_json::from_str(
            &execute_fixture(&repeated, &digest(&repeated).unwrap(), &mut |_, _| Ok(())).unwrap(),
        )
        .unwrap();
        assert!(report["journalDirectory"].is_null());
        assert_eq!(fs::read_dir(&journal_root).unwrap().count(), records_before);
        assert!(retained.is_file());
    }
}
