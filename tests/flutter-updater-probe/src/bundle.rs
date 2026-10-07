use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};

use serde::Deserialize;
use sha2::{Digest, Sha256};

use crate::{paths, ErrorKind, ProbeError, Result, PRODUCT_NAME};

pub const MAX_MANIFEST_BYTES: usize = 1024 * 1024;
pub const MAX_FILE_COUNT: usize = 8192;
pub const MAX_FILE_BYTES: u64 = 512 * 1024 * 1024;
pub const MAX_BUNDLE_BYTES: u64 = 1024 * 1024 * 1024;
pub const MAX_PATH_BYTES: usize = 240;
pub const MAX_PATH_DEPTH: usize = 16;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RawManifest {
    schema_version: u32,
    product: String,
    version: String,
    entrypoint: String,
    files: Vec<BundleFile>,
}

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct BundleFile {
    pub path: String,
    pub size: u64,
    pub sha256: String,
}

#[derive(Debug, Clone)]
pub struct BundleManifest {
    version: String,
    entrypoint: String,
    files: BTreeMap<String, BundleFile>,
    directories: BTreeSet<String>,
    total_bytes: u64,
}

impl BundleManifest {
    pub fn version(&self) -> &str {
        &self.version
    }
    pub fn entrypoint(&self) -> &str {
        &self.entrypoint
    }
    pub fn files(&self) -> &BTreeMap<String, BundleFile> {
        &self.files
    }
    pub fn total_bytes(&self) -> u64 {
        self.total_bytes
    }
}

/// The expected identity belongs to the compiled bootstrap, not to an external
/// caller's command line. A manifest alone is not proof of release provenance.
pub fn parse_manifest(
    bytes: &[u8],
    expected_version: &str,
    expected_entrypoint: &str,
) -> Result<BundleManifest> {
    if bytes.len() > MAX_MANIFEST_BYTES {
        return Err(invalid("manifest exceeds 1 MiB"));
    }
    let raw: RawManifest =
        serde_json::from_slice(bytes).map_err(|error| invalid(error.to_string()))?;
    if raw.schema_version != 1 || raw.product != PRODUCT_NAME {
        return Err(invalid("unsupported manifest schema or product"));
    }
    if raw.version != expected_version || !is_product_version(&raw.version) {
        return Err(invalid(
            "manifest version is not the exact canonical compiled product version",
        ));
    }
    validate_relative_path(&raw.entrypoint)?;
    if raw.entrypoint != expected_entrypoint
        || raw.entrypoint.contains('/')
        || !raw.entrypoint.ends_with(".exe")
    {
        return Err(invalid(
            "entrypoint differs from the compiled root executable name",
        ));
    }
    if raw.files.is_empty() || raw.files.len() > MAX_FILE_COUNT {
        return Err(invalid("manifest file count is outside the probe limit"));
    }
    let mut files = BTreeMap::new();
    let mut spellings = BTreeMap::new();
    let mut directories = BTreeSet::new();
    let mut total_bytes = 0_u64;
    for file in raw.files {
        validate_relative_path(&file.path)?;
        if file.size > MAX_FILE_BYTES || (file.path == raw.entrypoint && file.size == 0) {
            return Err(invalid(format!(
                "empty entrypoint or oversized bundle file: {}",
                file.path
            )));
        }
        if file.sha256.len() != 64
            || !file
                .sha256
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        {
            return Err(invalid(format!("noncanonical SHA-256: {}", file.path)));
        }
        total_bytes = total_bytes
            .checked_add(file.size)
            .ok_or_else(|| invalid("bundle length overflow"))?;
        if total_bytes > MAX_BUNDLE_BYTES {
            return Err(invalid("bundle exceeds 1 GiB"));
        }
        let mut prefix = String::new();
        let segments: Vec<_> = file.path.split('/').collect();
        for (index, segment) in segments.iter().enumerate() {
            if index > 0 {
                prefix.push('/');
            }
            prefix.push_str(segment);
            let key = prefix.to_ascii_lowercase();
            if spellings
                .get(&key)
                .is_some_and(|spelling| spelling != &prefix)
            {
                return Err(invalid(format!("Windows case alias: {prefix}")));
            }
            spellings.insert(key, prefix.clone());
            if index + 1 < segments.len() {
                directories.insert(prefix.clone());
            }
        }
        if files.insert(file.path.clone(), file).is_some() {
            return Err(invalid("duplicate manifest path"));
        }
    }
    if !files.contains_key(&raw.entrypoint) {
        return Err(invalid("entrypoint is absent from the file manifest"));
    }
    if directories
        .iter()
        .any(|directory| files.contains_key(directory))
    {
        return Err(invalid("a path is both a file and a directory"));
    }
    Ok(BundleManifest {
        version: raw.version,
        entrypoint: raw.entrypoint,
        files,
        directories,
        total_bytes,
    })
}

/// Portable ASCII paths keep Windows ordinal case and reserved-name checks
/// deterministic even when this contract runs on Linux. Unexpected build output
/// fails explicitly; it is never renamed or silently omitted.
pub fn validate_relative_path(path: &str) -> Result<()> {
    let parts: Vec<_> = path.split('/').collect();
    if path.is_empty() || path.len() > MAX_PATH_BYTES || parts.len() > MAX_PATH_DEPTH {
        return Err(invalid("empty or oversized bundle relative path"));
    }
    for part in parts {
        if part.is_empty()
            || part == "."
            || part == ".."
            || part.ends_with('.')
            || !part
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || b"-_.+@".contains(&byte))
        {
            return Err(invalid(format!("nonportable bundle relative path: {path}")));
        }
        let stem = part.split('.').next().unwrap_or("").to_ascii_uppercase();
        if matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
            || (stem.len() == 4
                && (stem.starts_with("COM") || stem.starts_with("LPT"))
                && matches!(stem.as_bytes()[3], b'1'..=b'9'))
        {
            return Err(invalid(format!("Windows reserved filename: {path}")));
        }
    }
    Ok(())
}

pub fn verify_bundle(root: &Path, manifest: &BundleManifest) -> Result<PathBuf> {
    paths::directory(root)?;
    let mut actual_files = BTreeSet::new();
    let mut actual_directories = BTreeSet::new();
    inspect_tree(
        root,
        root,
        manifest,
        &mut actual_files,
        &mut actual_directories,
    )?;
    if actual_files.len() != manifest.files.len()
        || actual_files.iter().ne(manifest.files.keys())
        || actual_directories != manifest.directories
    {
        return Err(ProbeError::new(
            ErrorKind::IntegrityMismatch,
            "bundle file/directory set differs from manifest",
        ));
    }
    for file in manifest.files.values() {
        verify_file(&root.join(&file.path), file)?;
    }
    Ok(root.join(&manifest.entrypoint))
}

fn inspect_tree(
    root: &Path,
    current: &Path,
    manifest: &BundleManifest,
    files: &mut BTreeSet<String>,
    directories: &mut BTreeSet<String>,
) -> Result<()> {
    let entries = fs::read_dir(current).map_err(io_error)?;
    for entry in entries {
        let entry = entry.map_err(io_error)?;
        let path = entry.path();
        let relative = path
            .strip_prefix(root)
            .map_err(|_| invalid("bundle escaped its root"))?;
        let relative = relative
            .iter()
            .map(|part| {
                part.to_str()
                    .ok_or_else(|| invalid("non-Unicode bundle filename"))
            })
            .collect::<Result<Vec<_>>>()?
            .join("/");
        validate_relative_path(&relative)?;
        let metadata = paths::metadata_without_links(&path)?;
        if metadata.is_dir() {
            if !manifest.directories.contains(&relative) {
                return Err(ProbeError::new(
                    ErrorKind::IntegrityMismatch,
                    format!("unlisted directory: {relative}"),
                ));
            }
            directories.insert(relative);
            inspect_tree(root, &path, manifest, files, directories)?;
        } else if metadata.is_file() {
            if !manifest.files.contains_key(&relative) {
                return Err(ProbeError::new(
                    ErrorKind::IntegrityMismatch,
                    format!("unlisted file: {relative}"),
                ));
            }
            files.insert(relative);
        } else {
            return Err(invalid(format!("not a regular bundle entry: {relative}")));
        }
    }
    Ok(())
}

pub fn verify_file(path: &Path, expected: &BundleFile) -> Result<()> {
    if paths::regular_file(path)?.len() != expected.size {
        return Err(ProbeError::new(
            ErrorKind::IntegrityMismatch,
            format!("size mismatch: {}", expected.path),
        ));
    }
    let mut source = File::open(path).map_err(io_error)?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    let mut total = 0_u64;
    loop {
        let length = source.read(&mut buffer).map_err(io_error)?;
        if length == 0 {
            break;
        }
        total += length as u64;
        if total > expected.size {
            return Err(ProbeError::new(
                ErrorKind::IntegrityMismatch,
                "file grew while hashing",
            ));
        }
        digest.update(&buffer[..length]);
    }
    if total != expected.size || format!("{:x}", digest.finalize()) != expected.sha256 {
        return Err(ProbeError::new(
            ErrorKind::IntegrityMismatch,
            format!("SHA-256 mismatch: {}", expected.path),
        ));
    }
    Ok(())
}

fn is_product_version(version: &str) -> bool {
    let canonical = |value: &str, zero: bool| {
        !value.is_empty()
            && value.bytes().all(|byte| byte.is_ascii_digit())
            && (value == "0" && zero || !value.starts_with('0'))
            && value.parse::<u64>().is_ok()
    };
    let core = if let Some((core, preview)) = version.split_once("-preview.") {
        if !canonical(preview, false) {
            return false;
        }
        core
    } else {
        version
    };
    let parts: Vec<_> = core.split('.').collect();
    parts.len() == 3 && parts.iter().all(|part| canonical(part, true))
}

fn invalid(message: impl Into<String>) -> ProbeError {
    ProbeError::new(ErrorKind::InvalidManifest, message)
}
fn io_error(error: std::io::Error) -> ProbeError {
    ProbeError::new(ErrorKind::Io, error.to_string())
}
