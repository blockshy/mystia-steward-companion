//! 与 C#/Node 共用语义的完整清单验证及附加文件保留。
//! 不执行载荷；拒绝链接、路径别名、大小写重复和未知字段。所有失败在移动旧目录之前返回。
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{collections::{BTreeMap, HashSet}, fs, io, path::{Path, PathBuf}};

pub const NAME: &str = "package-manifest.json";
pub const REQUIRED: [&str; 15] = ["MystiaStewardCompanion.BepInEx.dll", "MystiaStewardCompanion.Contracts.dll",
    "MystiaStewardCompanion.Business.dll", "mystia-steward-companion-updater.exe",
    "companion/mystia-steward-companion.exe", "companion/flutter_windows.dll",
    "companion/msvcp140.dll", "companion/vcruntime140.dll", "companion/vcruntime140_1.dll",
    "companion/data/app.so", "companion/data/icudtl.dat", "companion/data/flutter_assets/AssetManifest.bin",
    "companion/data/flutter_assets/FontManifest.json", "companion/data/flutter_assets/NOTICES.Z",
    "companion/THIRD-PARTY-NOTICES.txt"];
pub const PRESERVED: [&str; 4] = ["logs", "diagnostics", "exports", "user-data"];

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Manifest {
    pub schema_version: u32, pub product: String, pub version: String,
    pub source_commit: String, pub source_digest: String, pub toolchain_sha256: String,
    pub business_protocol_version: u32, pub client_protocol_version: u32, pub files: Vec<Entry>,
}
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Entry { pub path: String, pub size: u64, pub sha256: String }

fn check(value: bool, reason: &str) -> Result<(), String> { if value { Ok(()) } else { Err(reason.into()) } }
fn hex(value: &str, length: usize) -> bool { value.len() == length && value.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)) }
pub fn relative(value: &str) -> Result<(), String> {
    // 路径长度采用 UTF-16 单元，与 Windows/.NET 和 JavaScript 的字符串边界完全一致。
    check(!value.is_empty() && value.encode_utf16().count() <= 240 && !value.starts_with('/')
        && !value.chars().any(|c| c <= '\u{1f}' || c == '\u{7f}' || "\\:<>\"|?*".contains(c)), "unsafe package path")?;
    for part in value.split('/') {
        let stem = part.split('.').next().unwrap_or("").to_ascii_lowercase();
        check(!part.is_empty() && part != "." && part != ".." && !part.ends_with(['.', ' '])
            && !["con","prn","aux","nul"].contains(&stem.as_str())
            && !(stem.len() == 4 && (stem.starts_with("com") || stem.starts_with("lpt")) && matches!(stem.as_bytes()[3], b'1'..=b'9')), "noncanonical package path")?;
    }
    Ok(())
}
fn no_link(path: &Path) -> Result<fs::Metadata, String> {
    let meta = fs::symlink_metadata(path).map_err(|e|e.to_string())?;
    check(!meta.file_type().is_symlink(), "package symlink rejected")?;
    #[cfg(windows)] {
        use std::os::windows::fs::MetadataExt;
        check(meta.file_attributes() & 0x400 == 0, "package reparse point rejected")?;
    }
    Ok(meta)
}
/// 物理树按层验证，文件数据仅在大小上限通过后读取。目录本身也参与大小写冲突检查。
pub fn tree(root: &Path) -> Result<BTreeMap<String, PathBuf>, String> {
    for ancestor in root.ancestors() { if !ancestor.as_os_str().is_empty() { no_link(ancestor)?; } }
    check(no_link(root)?.is_dir(), "package root must be directory")?;
    let mut queue = vec![root.to_path_buf()]; let mut files = BTreeMap::new(); let mut folded = HashSet::new(); let mut total = 0u64;
    while let Some(dir) = queue.pop() {
        for entry in fs::read_dir(dir).map_err(|e|e.to_string())? {
            let path = entry.map_err(|e|e.to_string())?.path(); let meta = no_link(&path)?;
            let name = path.strip_prefix(root).map_err(|e|e.to_string())?.to_str().ok_or("non-UTF8 package path")?.replace('\\', "/");
            relative(&name)?; check(folded.insert(name.to_lowercase()), "package path case conflict")?;
            if meta.is_dir() { queue.push(path); } else {
                check(meta.is_file() && meta.len() <= 536870912 && files.len() < 8193, "package size/count limit")?;
                total += meta.len(); check(total <= 2147483648, "package total size limit")?; files.insert(name, path);
            }
        }
    }
    Ok(files)
}
fn preserved(name: &str) -> bool { PRESERVED.iter().any(|prefix| name == *prefix || name.starts_with(&format!("{prefix}/"))) }
/// 已安装树允许四个显式用户目录；暂存下载树必须严格等于清单集合。
pub fn validate(root: &Path, allow_preserved: bool) -> Result<Manifest, String> {
    let files = tree(root)?; let source = files.get(NAME).ok_or("missing package manifest")?;
    let length = fs::metadata(source).map_err(|e|e.to_string())?.len();
    check(length > 0 && length <= 4194304, "manifest size limit")?;
    let manifest: Manifest = serde_json::from_slice(&fs::read(source).map_err(|e|e.to_string())?).map_err(|e|e.to_string())?;
    check(manifest.schema_version == 1 && manifest.product == "mystia-steward-companion" && manifest.business_protocol_version == 1 && manifest.client_protocol_version == 2, "unsupported package protocol")?;
    let core = manifest.version.split('-').next().unwrap_or("");
    check(!manifest.version.ends_with('-') || manifest.version.split_once('-').is_some_and(|(_, suffix)| !suffix.is_empty()), "empty version suffix")?;
    check(core.split('.').count() == 3 && core.split('.').all(|p| !p.is_empty() && p.bytes().all(|b|b.is_ascii_digit()))
        && manifest.version.bytes().all(|b|b.is_ascii_alphanumeric() || b".-".contains(&b)), "invalid product version")?;
    check(hex(&manifest.source_commit,40) && hex(&manifest.source_digest,64) && hex(&manifest.toolchain_sha256,64), "invalid package provenance")?;
    check(manifest.files.len() >= REQUIRED.len() && manifest.files.len() <= 8192, "manifest file count")?;
    let mut seen = HashSet::new(); let mut names = HashSet::new(); let mut total = 0u64;
    for entry in &manifest.files {
        relative(&entry.path)?;
        check((REQUIRED.contains(&entry.path.as_str()) || entry.path.starts_with("companion/")) && seen.insert(entry.path.to_lowercase()), "unknown/duplicate managed path")?;
        names.insert(entry.path.as_str()); total += entry.size;
        check(entry.size <= 536870912 && total <= 2147483648 && hex(&entry.sha256,64), "invalid payload metadata")?;
        let lower = entry.path.to_lowercase();
        // 必需 AOT/ICU/索引/许可均非空，不能用另一个普通 data 文件掩盖关键项缺失。
        check(entry.size > 0 || !(REQUIRED.contains(&entry.path.as_str()) || lower.ends_with(".exe") || lower.ends_with(".dll")), "empty required/executable payload")?;
        let source = files.get(&entry.path).ok_or_else(||format!("missing payload: {}",entry.path))?;
        let mut stream = fs::File::open(source).map_err(|e|e.to_string())?;
        let mut hash = Sha256::new(); io::copy(&mut stream,&mut hash).map_err(|e|e.to_string())?;
        check(fs::metadata(source).map_err(|e|e.to_string())?.len() == entry.size && format!("{:x}",hash.finalize()) == entry.sha256, "payload size/hash mismatch")?;
    }
    for name in REQUIRED { check(names.contains(name), "missing required payload")?; }
    for name in files.keys() { check(name == NAME || names.contains(name.as_str()) || (allow_preserved && preserved(name)), "unlisted package file")?; }
    Ok(manifest)
}
/// 仅保留白名单附加文件；新包同名路径、旧非白名单文件或旧包损坏均拒绝，不猜测用户意图。
pub fn preserve(old: &Path, staged: &Path) -> Result<(), String> {
    validate(old,true)?; validate(staged,false)?;
    for (name, source) in tree(old)? {
        if !preserved(&name) { continue; }
        let destination = staged.join(&name); check(!destination.exists(), "preserved file conflict")?;
        fs::create_dir_all(destination.parent().ok_or("missing preserved parent")?).map_err(|e|e.to_string())?;
        fs::copy(&source,&destination).map_err(|e|e.to_string())?;
        check(fs::read(&source).map_err(|e|e.to_string())? == fs::read(destination).map_err(|e|e.to_string())?, "preserved file copy mismatch")?;
    }
    validate(staged,true)?; Ok(())
}
#[cfg(test)]
pub fn fixture(root: &Path) {
    fs::create_dir_all(root.join("companion/data")).unwrap();
    let mut entries = Vec::new();
    for name in REQUIRED.into_iter().chain(["companion/data/empty.asset"]) {
        let bytes = if name.ends_with("empty.asset") { Vec::new() } else { format!("受控组件：{name}").into_bytes() };
        // 必需资产含 flutter_assets 子目录；夹具按每项真实相对路径建父目录，不能依赖旧的扁平 data 布局。
        fs::create_dir_all(root.join(name).parent().unwrap()).unwrap();
        fs::write(root.join(name), &bytes).unwrap();
        entries.push(Entry {path:name.into(),size:bytes.len() as u64,sha256:format!("{:x}",Sha256::digest(&bytes))});
    }
    let manifest = Manifest {schema_version:1,product:"mystia-steward-companion".into(),version:"1.3.1".into(),source_commit:"a".repeat(40),source_digest:"b".repeat(64),toolchain_sha256:"c".repeat(64),business_protocol_version:1,client_protocol_version:2,files:entries};
    fs::write(root.join(NAME),serde_json::to_vec_pretty(&manifest).unwrap()).unwrap();
}
