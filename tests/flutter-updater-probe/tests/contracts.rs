use std::ffi::OsString;
use std::fs;
use std::io::{Cursor, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use mystia_steward_companion_flutter_updater_probe::archive::extract_verified_zip;
use mystia_steward_companion_flutter_updater_probe::bundle::{
    parse_manifest, validate_relative_path, verify_bundle, MAX_MANIFEST_BYTES,
};
use mystia_steward_companion_flutter_updater_probe::launch::{
    parse_legacy_arguments, verify_runner_binding, DEFAULT_CONTROL_PORT,
    DEFAULT_WAIT_TIMEOUT_SECONDS,
};
use mystia_steward_companion_flutter_updater_probe::status::{
    LegacyInstallState, LegacyInstallStatus,
};
use mystia_steward_companion_flutter_updater_probe::wire::{ProbeSession, MAX_FRAME_BYTES};
use mystia_steward_companion_flutter_updater_probe::{ErrorKind, UPDATER_FILE_NAME};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use zip::write::SimpleFileOptions;

const VERSION: &str = "1.3.1";
const ENTRYPOINT: &str = "mystia-steward-companion-updater-ui.exe";
const SESSION: &str = "0123456789abcdef0123456789abcdef";

struct TestDirectory(PathBuf);
impl TestDirectory {
    fn new() -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        loop {
            let nonce = NEXT.fetch_add(1, Ordering::Relaxed);
            let epoch = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let path = std::env::temp_dir().join(format!(
                "mystia-updater-p0-test-{}-{epoch}-{nonce}",
                std::process::id()
            ));
            match fs::create_dir(&path) {
                Ok(()) => return Self(path),
                Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
                Err(error) => panic!("create isolated test directory: {error}"),
            }
        }
    }
    fn path(&self, relative: &str) -> PathBuf {
        self.0.join(relative)
    }
    fn write(&self, relative: &str, bytes: &[u8]) -> PathBuf {
        let path = self.path(relative);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, bytes).unwrap();
        path
    }
}
impl Drop for TestDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn legacy_args(root: &TestDirectory) -> Vec<OsString> {
    [
        ("game-pid", "4242".to_owned()),
        (
            "plugin-dir",
            root.path("mystia-steward-companion").display().to_string(),
        ),
        (
            "staged-dir",
            root.path("updates/staged/v1.3.1").display().to_string(),
        ),
        (
            "backup-dir",
            root.path("updates/backups/previous").display().to_string(),
        ),
        (
            "status-file",
            root.path("updates/install-status.json")
                .display()
                .to_string(),
        ),
    ]
    .into_iter()
    .flat_map(|(key, value)| [format!("--{key}").into(), value.into()])
    .collect()
}

fn replace_value(args: &mut [OsString], key: &str, value: impl Into<OsString>) {
    let index = args.iter().position(|arg| arg == key).unwrap();
    args[index + 1] = value.into();
}

fn manifest_value() -> Value {
    json!({
        "schemaVersion": 1,
        "product": "mystia-steward-companion",
        "version": VERSION,
        "entrypoint": ENTRYPOINT,
        "files": [
            file(ENTRYPOINT, b"probe-ui-fixture"),
            file("flutter_windows.dll", b"engine-fixture"),
            file("data/flutter_assets/empty-resource", b""),
            file("data/flutter_assets/help.json", b"abc"),
        ],
    })
}

fn file(path: &str, bytes: &[u8]) -> Value {
    json!({"path": path, "size": bytes.len(), "sha256": format!("{:x}", Sha256::digest(bytes))})
}

fn populate(root: &TestDirectory) -> PathBuf {
    root.write(&format!("bundle/{ENTRYPOINT}"), b"probe-ui-fixture");
    root.write("bundle/flutter_windows.dll", b"engine-fixture");
    root.write("bundle/data/flutter_assets/empty-resource", b"");
    root.write("bundle/data/flutter_assets/help.json", b"abc");
    root.path("bundle")
}

fn parse(
    value: &Value,
) -> mystia_steward_companion_flutter_updater_probe::Result<
    mystia_steward_companion_flutter_updater_probe::bundle::BundleManifest,
> {
    parse_manifest(&serde_json::to_vec(value).unwrap(), VERSION, ENTRYPOINT)
}

fn archive(entries: &[(&str, &[u8])], directory_entries: bool) -> Vec<u8> {
    let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let options = SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    if directory_entries {
        writer.add_directory("data/", options).unwrap();
        writer
            .add_directory("data/flutter_assets/", options)
            .unwrap();
    }
    for (name, bytes) in entries {
        writer.start_file(*name, options).unwrap();
        writer.write_all(bytes).unwrap();
    }
    writer.finish().unwrap().into_inner()
}

fn valid_archive(directories: bool) -> Vec<u8> {
    archive(
        &[
            (ENTRYPOINT, b"probe-ui-fixture"),
            ("flutter_windows.dll", b"engine-fixture"),
            ("data/flutter_assets/empty-resource", b""),
            ("data/flutter_assets/help.json", b"abc"),
        ],
        directories,
    )
}

#[test]
fn accepts_exact_update_service_argument_shape_and_preserves_defaults() {
    let root = TestDirectory::new();
    let parsed = parse_legacy_arguments(legacy_args(&root)).unwrap();
    assert_eq!(parsed.game_pid.get(), 4242);
    assert_eq!(parsed.control_port.get(), DEFAULT_CONTROL_PORT);
    assert_eq!(parsed.wait_timeout.as_secs(), DEFAULT_WAIT_TIMEOUT_SECONDS);
    let mut args = legacy_args(&root);
    args.extend(["--control-port", "32146", "--wait-timeout-seconds", "300"].map(OsString::from));
    assert_eq!(
        parse_legacy_arguments(args).unwrap().wait_timeout.as_secs(),
        300
    );
}

#[test]
fn rejects_ambiguous_argument_shapes_and_noncanonical_numbers() {
    let root = TestDirectory::new();
    for suffix in [
        vec!["--game-pid", "2"],
        vec!["--unknown", "1"],
        vec!["--control-port"],
        vec!["--control-port=123"],
        vec!["positional"],
    ] {
        let mut args = legacy_args(&root);
        args.extend(suffix.iter().map(OsString::from));
        assert!(parse_legacy_arguments(args).is_err(), "{suffix:?}");
    }
    for value in ["0", "01", "+1", " 1", "-1", "4294967296", "--control-port"] {
        let mut args = legacy_args(&root);
        replace_value(&mut args, "--game-pid", value);
        assert!(parse_legacy_arguments(args).is_err(), "{value}");
    }
    for value in ["0", "65536", "01", "bad"] {
        let mut args = legacy_args(&root);
        args.extend([OsString::from("--control-port"), OsString::from(value)]);
        assert!(parse_legacy_arguments(args).is_err());
    }
    assert!(parse_legacy_arguments(Vec::new()).is_err());
}

#[test]
fn rejects_relative_overlapping_or_wrong_product_install_paths() {
    let root = TestDirectory::new();
    for (key, value) in [
        ("--plugin-dir", root.path("different-product")),
        ("--staged-dir", PathBuf::from("relative/path")),
        ("--staged-dir", root.path("mystia-steward-companion/stage")),
        ("--backup-dir", root.path("updates/staged")),
        (
            "--status-file",
            root.path("mystia-steward-companion/status.json"),
        ),
        (
            "--status-file",
            root.path("updates/staged/v1.3.1/status.json"),
        ),
        ("--backup-dir", root.path("updates/backups/../wrong")),
    ] {
        let mut args = legacy_args(&root);
        replace_value(&mut args, key, value.into_os_string());
        assert!(parse_legacy_arguments(args).is_err(), "{key}");
    }
}

#[test]
fn runner_binding_checks_every_byte_length_and_legacy_name() {
    let root = TestDirectory::new();
    let bytes = vec![37_u8; 131_075];
    let runner = root.write(&format!("runner/{UPDATER_FILE_NAME}"), &bytes);
    let packaged = root.write(&format!("staged/{UPDATER_FILE_NAME}"), &bytes);
    verify_runner_binding(&runner, &root.path("staged")).unwrap();
    let mut changed = bytes.clone();
    changed[70_000] ^= 1;
    fs::write(&packaged, &changed).unwrap();
    assert_eq!(
        verify_runner_binding(&runner, &root.path("staged"))
            .unwrap_err()
            .kind,
        ErrorKind::IntegrityMismatch
    );
    fs::write(&packaged, &bytes[..bytes.len() - 1]).unwrap();
    assert!(verify_runner_binding(&runner, &root.path("staged")).is_err());
    let renamed = root.write("runner/other.exe", &bytes);
    assert!(verify_runner_binding(&renamed, &root.path("staged")).is_err());
    assert!(verify_runner_binding(&packaged, &root.path("staged")).is_err());
}

#[test]
fn runner_preflight_refuses_plugin_local_extraction_and_existing_backup() {
    let root = TestDirectory::new();
    let arguments = parse_legacy_arguments(legacy_args(&root)).unwrap();
    for directory in [
        arguments.plugin_dir.as_path(),
        arguments.staged_dir.as_path(),
        arguments.backup_dir.parent().unwrap(),
        arguments.status_file.parent().unwrap(),
        root.path("runner").as_path(),
    ] {
        fs::create_dir_all(directory).unwrap();
    }
    let runner = root.path(&format!("runner/{UPDATER_FILE_NAME}"));
    arguments.validate_runner_location(&runner).unwrap();
    assert!(arguments
        .validate_runner_location(&arguments.plugin_dir.join(UPDATER_FILE_NAME))
        .is_err());
    fs::create_dir(&arguments.backup_dir).unwrap();
    assert!(arguments.validate_runner_location(&runner).is_err());
}

#[test]
fn preserves_all_legacy_status_names_without_claiming_probe_installation() {
    for name in [
        "waiting",
        "preparing",
        "closing-companion",
        "waiting-game",
        "terminating-game",
        "game-closed",
        "backing-up",
        "installing",
        "verifying",
        "succeeded",
        "failed",
        "cancelled",
    ] {
        let value = json!({"state": name, "message": "wire fixture, not an install result", "progress": 25});
        let decoded = LegacyInstallStatus::from_json(&serde_json::to_vec(&value).unwrap()).unwrap();
        assert_eq!(
            serde_json::from_slice::<Value>(&decoded.to_json().unwrap()).unwrap(),
            value
        );
    }
    let mut value = json!({"state":"failed","message":"P0","progress":0});
    for progress in [json!(-1), json!(101), json!(1.5), json!("1")] {
        value["progress"] = progress;
        assert!(LegacyInstallStatus::from_json(&serde_json::to_vec(&value).unwrap()).is_err());
    }
    assert!(
        LegacyInstallStatus::from_json(br#"{"state":"ready","message":"P0","progress":0}"#)
            .is_err()
    );
    assert!(LegacyInstallStatus::from_json(
        br#"{"state":"failed","state":"cancelled","message":"P0","progress":0}"#
    )
    .is_err());
    assert!(LegacyInstallStatus {
        state: LegacyInstallState::Failed,
        message: "P0".into(),
        progress: 255
    }
    .to_json()
    .is_err());
}

#[test]
fn validates_real_bundle_hashes_file_set_and_zero_byte_resource() {
    assert_eq!(
        format!("{:x}", Sha256::digest(b"abc")),
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    );
    let root = TestDirectory::new();
    let bundle = populate(&root);
    let manifest = parse(&manifest_value()).unwrap();
    assert_eq!(
        verify_bundle(&bundle, &manifest).unwrap(),
        bundle.join(ENTRYPOINT)
    );
    root.write("bundle/data/flutter_assets/help.json", b"abd");
    assert_eq!(
        verify_bundle(&bundle, &manifest).unwrap_err().kind,
        ErrorKind::IntegrityMismatch
    );
    root.write("bundle/data/flutter_assets/help.json", b"abc");
    root.write("bundle/unlisted.dll", b"extra");
    assert!(verify_bundle(&bundle, &manifest).is_err());
    fs::remove_file(bundle.join("unlisted.dll")).unwrap();
    fs::create_dir(bundle.join("unlisted-directory")).unwrap();
    assert!(verify_bundle(&bundle, &manifest).is_err());
    fs::remove_dir(bundle.join("unlisted-directory")).unwrap();
    fs::remove_file(bundle.join("flutter_windows.dll")).unwrap();
    assert!(verify_bundle(&bundle, &manifest).is_err());
}

#[test]
fn rejects_manifest_identity_schema_duplicate_keys_and_overflow() {
    for (key, value) in [
        ("schemaVersion", json!(2)),
        ("product", json!("other")),
        ("version", json!("1.3.2")),
        ("entrypoint", json!("other.exe")),
        ("extra", json!(true)),
    ] {
        let mut manifest = manifest_value();
        manifest[key] = value;
        assert!(parse(&manifest).is_err(), "{key}");
    }
    for size in [json!(0), json!(-1), json!(9007199254740992_u64), json!(1.2)] {
        let mut manifest = manifest_value();
        manifest["files"][0]["size"] = size;
        assert!(parse(&manifest).is_err());
    }
    let mut manifest = manifest_value();
    manifest["files"][0]["sha256"] = json!("A".repeat(64));
    assert!(parse(&manifest).is_err());
    assert!(parse_manifest(&vec![b' '; MAX_MANIFEST_BYTES + 1], VERSION, ENTRYPOINT).is_err());
    let duplicated = serde_json::to_string(&manifest_value()).unwrap().replacen(
        "\"schemaVersion\":1",
        "\"schemaVersion\":1,\"schemaVersion\":1",
        1,
    );
    assert!(parse_manifest(duplicated.as_bytes(), VERSION, ENTRYPOINT).is_err());
}

#[test]
fn rejects_windows_path_ambiguity_even_on_linux() {
    for path in [
        "",
        "/root.exe",
        "../bad.exe",
        "data/../bad.exe",
        "data//a",
        "data\\a",
        "C:/a",
        "C:a",
        "a:stream",
        "a.",
        "a ",
        "NUL",
        "nul.txt",
        "CON.exe",
        "COM1.dll",
        "LPT9",
        "data/./a",
        "data/a\n",
        "a?",
        "\\\\server\\share",
        "data/中.txt",
    ] {
        assert!(validate_relative_path(path).is_err(), "{path:?}");
    }
    for (first, second) in [
        ("data/a", "DATA/b"),
        ("data/a", "data/A"),
        ("data", "data/a"),
        ("data/a", "data/a"),
    ] {
        let mut value = manifest_value();
        value["files"]
            .as_array_mut()
            .unwrap()
            .extend([file(first, b"x"), file(second, b"x")]);
        assert!(parse(&value).is_err(), "{first} / {second}");
    }
}

#[test]
fn extracts_complete_zip_with_or_without_directory_entries_and_keeps_old_tree() {
    for directories in [true, false] {
        let root = TestDirectory::new();
        let sentinel = root.write("mystia-steward-companion/user-owned.txt", b"unchanged");
        let manifest = parse(&manifest_value()).unwrap();
        let destination = root.path("fresh-bundle");
        extract_verified_zip(&valid_archive(directories), &manifest, &destination).unwrap();
        verify_bundle(&destination, &manifest).unwrap();
        assert_eq!(fs::read(&sentinel).unwrap(), b"unchanged");
        assert!(extract_verified_zip(&valid_archive(false), &manifest, &destination).is_err());
        assert_eq!(fs::read(&sentinel).unwrap(), b"unchanged");
    }
}

#[test]
fn rejects_bad_zip_before_extraction_and_detects_tampered_bytes() {
    let root = TestDirectory::new();
    let manifest = parse(&manifest_value()).unwrap();
    for entries in [
        vec![("../escape", &b"x"[..])],
        vec![("C:/escape", &b"x"[..])],
        vec![("missing.exe", &b"x"[..])],
    ] {
        let destination = root.path("bad-bundle");
        assert!(extract_verified_zip(&archive(&entries, false), &manifest, &destination).is_err());
        assert!(!destination.exists());
    }
    let tampered = archive(
        &[
            (ENTRYPOINT, b"probe-ui-fixture"),
            ("flutter_windows.dll", b"engine-fixture"),
            ("data/flutter_assets/empty-resource", b""),
            ("data/flutter_assets/help.json", b"abd"),
        ],
        false,
    );
    let destination = root.path("tampered");
    assert!(extract_verified_zip(&tampered, &manifest, &destination).is_err());
    assert!(
        destination.exists(),
        "retain this task's failed staging files for diagnosis"
    );
}

#[test]
fn rejects_zip_symlink_even_when_its_path_is_in_the_manifest() {
    let root = TestDirectory::new();
    let manifest = parse(&manifest_value()).unwrap();
    let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
    writer
        .add_symlink(ENTRYPOINT, "outside-target", SimpleFileOptions::default())
        .unwrap();
    let archive = writer.finish().unwrap().into_inner();
    let destination = root.path("link-bundle");
    assert!(extract_verified_zip(&archive, &manifest, &destination).is_err());
    assert!(!destination.exists());
}

#[cfg(unix)]
#[test]
fn refuses_symlinks_in_files_and_all_ancestor_directories() {
    use std::os::unix::fs::symlink;
    let root = TestDirectory::new();
    let bundle = populate(&root);
    let manifest = parse(&manifest_value()).unwrap();
    let outside = root.write("outside/help.json", b"abc");
    fs::remove_file(bundle.join("data/flutter_assets/help.json")).unwrap();
    symlink(&outside, bundle.join("data/flutter_assets/help.json")).unwrap();
    assert_eq!(
        verify_bundle(&bundle, &manifest).unwrap_err().kind,
        ErrorKind::InvalidPath
    );
    let aliased = root.path("alias");
    symlink(&bundle, &aliased).unwrap();
    assert_eq!(
        verify_bundle(&aliased, &manifest).unwrap_err().kind,
        ErrorKind::InvalidPath
    );
    let runner = root.write(&format!("runner/{UPDATER_FILE_NAME}"), b"same");
    let packaged = root.write(&format!("staged/{UPDATER_FILE_NAME}"), b"same");
    fs::remove_file(&runner).unwrap();
    symlink(&packaged, &runner).unwrap();
    assert_eq!(
        verify_runner_binding(&runner, &root.path("staged"))
            .unwrap_err()
            .kind,
        ErrorKind::InvalidPath
    );
}

fn request(id: u32, command: &str) -> Vec<u8> {
    serde_json::to_vec(
        &json!({"protocolVersion":1,"session":SESSION,"requestId":id,"command":command}),
    )
    .unwrap()
}

#[test]
fn wire_accepts_only_hello_then_cancel_and_never_install() {
    let mut session = ProbeSession::new(SESSION.to_owned()).unwrap();
    let first = session.accept(&request(1, "hello")).unwrap();
    assert_eq!(*first.last().unwrap(), b'\n');
    let first: Value = serde_json::from_slice(&first).unwrap();
    assert_eq!(first["state"], "ready");
    assert_eq!(first["stateSequence"], 1);
    let second: Value =
        serde_json::from_slice(&session.accept(&request(2, "cancel")).unwrap()).unwrap();
    assert_eq!(second["state"], "cancelled");
    assert_eq!(second["stateSequence"], 2);
    assert!(session.accept(&request(3, "cancel")).is_err());
    for command in ["start", "install", "execute", "cancel"] {
        assert!(ProbeSession::new(SESSION.to_owned())
            .unwrap()
            .accept(&request(1, command))
            .is_err());
    }
}

#[test]
fn wire_rejects_unknown_fields_stale_session_duplicates_and_oversize() {
    for (key, value) in [
        ("protocolVersion", json!(2)),
        ("session", json!("f".repeat(32))),
        ("requestId", json!(2)),
        ("path", json!("C:/anything")),
    ] {
        let mut frame: Value = serde_json::from_slice(&request(1, "hello")).unwrap();
        frame[key] = value;
        assert!(ProbeSession::new(SESSION.to_owned())
            .unwrap()
            .accept(&serde_json::to_vec(&frame).unwrap())
            .is_err());
    }
    let duplicate = format!(
        r#"{{"protocolVersion":1,"session":"{SESSION}","requestId":1,"requestId":1,"command":"hello"}}"#
    );
    assert!(ProbeSession::new(SESSION.to_owned())
        .unwrap()
        .accept(duplicate.as_bytes())
        .is_err());
    assert!(ProbeSession::new(SESSION.to_owned())
        .unwrap()
        .accept(&vec![b' '; MAX_FRAME_BYTES + 1])
        .is_err());
    assert!(ProbeSession::new("not-a-random-session".to_owned()).is_err());
    let mut session = ProbeSession::new(SESSION.to_owned()).unwrap();
    session.accept(&request(1, "hello")).unwrap();
    assert!(session.accept(&request(1, "hello")).is_err());
}
