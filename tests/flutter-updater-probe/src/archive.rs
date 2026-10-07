use std::collections::BTreeSet;
use std::fs::{self, OpenOptions};
use std::io::{Cursor, Read, Write};
use std::path::Path;

use crate::bundle::{validate_relative_path, verify_bundle, BundleManifest, MAX_FILE_COUNT};
use crate::{paths, ErrorKind, ProbeError, Result};

/// Extract only into a newly created directory owned by this probe. On failure
/// the partial directory is retained for diagnosis; existing trees are untouched.
/// The caller must keep this private runner directory outside the plugin tree.
pub fn extract_verified_zip(
    bytes: &[u8],
    manifest: &BundleManifest,
    destination: &Path,
) -> Result<()> {
    if bytes.is_empty() || bytes.len() > 512 * 1024 * 1024 {
        return Err(invalid("empty or oversized ZIP"));
    }
    let parent = destination
        .parent()
        .ok_or_else(|| invalid("missing extraction parent"))?;
    paths::directory(parent)?;
    if destination.file_name().is_none() || destination.try_exists().map_err(io_error)? {
        return Err(invalid("extraction destination must not exist"));
    }
    let mut archive =
        zip::ZipArchive::new(Cursor::new(bytes)).map_err(|error| invalid(error.to_string()))?;
    if archive.len() > MAX_FILE_COUNT * 2 {
        return Err(invalid("too many ZIP entries"));
    }
    // Validate all entries before making the first directory.
    let mut names = BTreeSet::new();
    let mut regular_names = BTreeSet::new();
    for index in 0..archive.len() {
        let entry = archive
            .by_index(index)
            .map_err(|error| invalid(error.to_string()))?;
        let name = entry.name();
        let relative = if entry.is_dir() {
            name.strip_suffix('/')
                .ok_or_else(|| invalid("noncanonical ZIP directory"))?
        } else {
            name
        };
        validate_relative_path(relative)?;
        if !names.insert(relative.to_ascii_lowercase()) {
            return Err(invalid("duplicate or case-aliased ZIP entry"));
        }
        let file_type = entry.unix_mode().unwrap_or(0) & 0o170000;
        if (entry.is_dir() && !matches!(file_type, 0 | 0o040000))
            || (!entry.is_dir() && !matches!(file_type, 0 | 0o100000))
        {
            return Err(invalid("ZIP links and special entries are forbidden"));
        }
        if entry.is_dir() {
            if !manifest
                .files()
                .keys()
                .any(|path| path.starts_with(&format!("{relative}/")))
                || entry.size() != 0
            {
                return Err(invalid("unlisted or nonempty ZIP directory entry"));
            }
        } else {
            let expected = manifest
                .files()
                .get(relative)
                .ok_or_else(|| invalid("unlisted ZIP file"))?;
            if entry.size() != expected.size {
                return Err(invalid("ZIP declared size differs from manifest"));
            }
            regular_names.insert(relative.to_owned());
        }
    }
    if regular_names.iter().ne(manifest.files().keys()) {
        return Err(invalid("ZIP does not contain the exact manifest file set"));
    }
    fs::create_dir(destination).map_err(io_error)?;
    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|error| invalid(error.to_string()))?;
        if entry.is_dir() {
            continue;
        }
        let expected = manifest
            .files()
            .get(entry.name())
            .ok_or_else(|| invalid("ZIP entry changed"))?;
        let target = destination.join(&expected.path);
        let parent = target
            .parent()
            .ok_or_else(|| invalid("invalid file parent"))?;
        fs::create_dir_all(parent).map_err(io_error)?;
        paths::directory(parent)?;
        let mut target_file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&target)
            .map_err(io_error)?;
        let mut remaining = expected.size;
        let mut buffer = [0_u8; 64 * 1024];
        while remaining > 0 {
            let length = remaining.min(buffer.len() as u64) as usize;
            entry.read_exact(&mut buffer[..length]).map_err(io_error)?;
            target_file.write_all(&buffer[..length]).map_err(io_error)?;
            remaining -= length as u64;
        }
        if entry.read(&mut buffer[..1]).map_err(io_error)? != 0 {
            return Err(invalid("ZIP data exceeds declared length"));
        }
        target_file.sync_all().map_err(io_error)?;
    }
    verify_bundle(destination, manifest)?;
    Ok(())
}

fn invalid(message: impl Into<String>) -> ProbeError {
    ProbeError::new(ErrorKind::InvalidManifest, message)
}
fn io_error(error: std::io::Error) -> ProbeError {
    ProbeError::new(ErrorKind::Io, error.to_string())
}
