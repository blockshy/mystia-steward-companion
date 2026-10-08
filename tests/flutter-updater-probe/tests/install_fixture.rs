//! Portable transaction tests use explicit fake process observations. They do
//! not claim Windows process, UI, filesystem durability or clean-OS evidence.
use mystia_steward_companion_flutter_updater_probe as probe;
use probe::bundle::{parse_manifest, verify_bundle, BundleManifest};
use probe::install_control::InstallControl;
use probe::install_wire::{InstallCommand, InstallSession};
use probe::launch::InstallArguments;
use probe::status::LegacyInstallState as State;
use probe::transaction::{run_install, InstallOutcome, ProcessObservation};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::fs;
use std::num::{NonZeroU16, NonZeroU32};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

struct Fixture {
    root: PathBuf,
    arguments: InstallArguments,
    old: BundleManifest,
    new: BundleManifest,
}
impl Fixture {
    fn new() -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let nonce = NEXT.fetch_add(1, Ordering::Relaxed);
        let epoch = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = std::env::temp_dir().join(format!(
            "mystia-install-unit-{}-{epoch}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&root).unwrap();
        let arguments = InstallArguments {
            game_pid: NonZeroU32::new(4242).unwrap(),
            plugin_dir: root.join(probe::PRODUCT_NAME),
            staged_dir: root.join("staging"),
            backup_dir: root.join("backups/previous"),
            status_file: root.join("state/install-status.json"),
            control_port: NonZeroU16::new(32756).unwrap(),
            wait_timeout: Duration::from_secs(1),
        };
        fs::create_dir(root.join("backups")).unwrap();
        let old = populate(&arguments.plugin_dir, false);
        let new = populate(&arguments.staged_dir, true);
        Self {
            root,
            arguments,
            old,
            new,
        }
    }
    fn old_intact(&self) {
        verify_bundle(&self.arguments.plugin_dir, &self.old).unwrap();
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}
fn populate(root: &std::path::Path, new: bool) -> BundleManifest {
    let mut files = Vec::new();
    for path in [
        probe::UPDATER_FILE_NAME,
        "MystiaStewardCompanion.BepInEx.dll",
        "companion/mystia-steward-companion.exe",
        "companion/flutter_windows.dll",
        "companion/data/flutter_assets/runtime.json",
    ] {
        let bytes = format!("{}:{path}", if new { "new" } else { "old" }).into_bytes();
        let destination = root.join(path);
        fs::create_dir_all(destination.parent().unwrap()).unwrap();
        fs::write(destination, &bytes).unwrap();
        files.push(
            json!({"path":path,"size":bytes.len(),"sha256":format!("{:x}",Sha256::digest(&bytes))}),
        );
    }
    parse_manifest(&serde_json::to_vec(&json!({"schemaVersion":1,"product":probe::PRODUCT_NAME,"version":"1.3.1","entrypoint":probe::UPDATER_FILE_NAME,"files":files})).unwrap(),"1.3.1",probe::UPDATER_FILE_NAME).unwrap()
}
struct Exited;
impl ProcessObservation for Exited {
    fn exited_zero(&self) -> probe::Result<bool> {
        Ok(true)
    }
}

#[test]
fn installs_every_file_and_preserves_verified_backup() {
    let fixture = Fixture::new();
    let control = InstallControl::default();
    let mut states = Vec::new();
    assert_eq!(
        run_install(
            &fixture.arguments,
            &fixture.old,
            &fixture.new,
            &Exited,
            &control,
            |status| {
                states.push(status.state);
                Ok(())
            }
        )
        .unwrap(),
        InstallOutcome::Succeeded
    );
    assert_eq!(
        states,
        vec![
            State::Preparing,
            State::WaitingGame,
            State::GameClosed,
            State::BackingUp,
            State::Installing,
            State::Verifying,
            State::Succeeded
        ]
    );
    verify_bundle(&fixture.arguments.plugin_dir, &fixture.new).unwrap();
    verify_bundle(&fixture.arguments.backup_dir, &fixture.old).unwrap();
    assert!(!fixture.arguments.staged_dir.exists());
}
#[test]
fn cancellation_at_game_exit_publication_wins_before_replacement() {
    let fixture = Fixture::new();
    let control = InstallControl::default();
    let result = run_install(
        &fixture.arguments,
        &fixture.old,
        &fixture.new,
        &Exited,
        &control,
        |status| {
            if status.state == State::GameClosed {
                control.cancel().unwrap();
            }
            Ok(())
        },
    )
    .unwrap();
    assert_eq!(result, InstallOutcome::Cancelled);
    fixture.old_intact();
    verify_bundle(&fixture.arguments.staged_dir, &fixture.new).unwrap();
    assert!(!fixture.arguments.backup_dir.exists());
}
#[test]
fn process_uncertainty_does_not_replace_files() {
    struct Unknown;
    impl ProcessObservation for Unknown {
        fn exited_zero(&self) -> probe::Result<bool> {
            Err(probe::ProbeError {
                kind: probe::ErrorKind::Io,
                message: "identity unavailable".into(),
            })
        }
    }
    let fixture = Fixture::new();
    assert!(run_install(
        &fixture.arguments,
        &fixture.old,
        &fixture.new,
        &Unknown,
        &InstallControl::default(),
        |_| Ok(())
    )
    .is_err());
    fixture.old_intact();
    assert!(!fixture.arguments.backup_dir.exists());
}
#[test]
fn waiting_cancellation_never_admits_replacement() {
    struct Waiting<'a>(&'a InstallControl);
    impl ProcessObservation for Waiting<'_> {
        fn exited_zero(&self) -> probe::Result<bool> {
            self.0.cancel().unwrap();
            Ok(false)
        }
    }
    let fixture = Fixture::new();
    let control = InstallControl::default();
    assert_eq!(
        run_install(
            &fixture.arguments,
            &fixture.old,
            &fixture.new,
            &Waiting(&control),
            &control,
            |_| Ok(())
        )
        .unwrap(),
        InstallOutcome::Cancelled
    );
    fixture.old_intact();
    assert!(!fixture.arguments.backup_dir.exists());
}
#[test]
fn existing_backup_and_extra_staged_file_are_rejected_without_writes() {
    for backup in [true, false] {
        let fixture = Fixture::new();
        if backup {
            fs::create_dir(&fixture.arguments.backup_dir).unwrap();
            fs::write(fixture.arguments.backup_dir.join("owner"), b"preserve").unwrap();
        } else {
            fs::write(
                fixture.arguments.staged_dir.join("unlisted.dll"),
                b"unexpected",
            )
            .unwrap();
        }
        assert!(run_install(
            &fixture.arguments,
            &fixture.old,
            &fixture.new,
            &Exited,
            &InstallControl::default(),
            |_| Ok(())
        )
        .is_err());
        fixture.old_intact();
        if backup {
            assert_eq!(
                fs::read(fixture.arguments.backup_dir.join("owner")).unwrap(),
                b"preserve"
            );
        }
    }
}
#[test]
fn damaged_post_install_asset_restores_old_tree_and_preserves_failed_tree() {
    let fixture = Fixture::new();
    let result = run_install(
        &fixture.arguments,
        &fixture.old,
        &fixture.new,
        &Exited,
        &InstallControl::default(),
        |status| {
            if status.state == State::Verifying {
                fs::write(
                    fixture
                        .arguments
                        .plugin_dir
                        .join("companion/data/flutter_assets/runtime.json"),
                    b"damaged",
                )
                .unwrap();
            }
            Ok(())
        },
    );
    assert!(result
        .unwrap_err()
        .message
        .contains("verified old fixture restored"));
    fixture.old_intact();
    assert!(!fixture.arguments.backup_dir.exists());
    assert!(fixture
        .root
        .join("failed-installed/companion/flutter_windows.dll")
        .exists());
}
#[test]
fn failed_status_publication_after_backup_restores_old_tree() {
    let fixture = Fixture::new();
    assert!(run_install(
        &fixture.arguments,
        &fixture.old,
        &fixture.new,
        &Exited,
        &InstallControl::default(),
        |status| {
            if status.state == State::Installing {
                Err(probe::ProbeError {
                    kind: probe::ErrorKind::Io,
                    message: "status write failed".into(),
                })
            } else {
                Ok(())
            }
        }
    )
    .is_err());
    fixture.old_intact();
    verify_bundle(&fixture.arguments.staged_dir, &fixture.new).unwrap();
    assert!(!fixture.arguments.backup_dir.exists());
}
#[test]
fn cancellation_after_admission_cannot_claim_cancelled_success() {
    let fixture = Fixture::new();
    let control = InstallControl::default();
    assert_eq!(
        run_install(
            &fixture.arguments,
            &fixture.old,
            &fixture.new,
            &Exited,
            &control,
            |status| {
                if status.state == State::BackingUp {
                    assert_eq!(
                        control.cancel().unwrap(),
                        probe::install_control::CancelDecision::TooLate
                    );
                }
                Ok(())
            }
        )
        .unwrap(),
        InstallOutcome::Succeeded
    );
}
const NONCE: &str = "0123456789abcdef0123456789abcdef";
fn request(id: u32, command: &str) -> Vec<u8> {
    serde_json::to_vec(
        &json!({"protocolVersion":2,"session":NONCE,"requestId":id,"command":command}),
    )
    .unwrap()
}
#[test]
fn install_wire_requires_explicit_once_only_start_and_terminal_finish() {
    let mut session = InstallSession::new(NONCE.into()).unwrap();
    assert!(session.accept(&request(1, "start"), false).is_err());
    assert_eq!(
        session.accept(&request(1, "hello"), false).unwrap(),
        (1, InstallCommand::Hello)
    );
    assert!(session.accept(&request(2, "finish"), false).is_err());
    session.accept(&request(2, "start"), false).unwrap();
    assert!(session.accept(&request(3, "start"), false).is_err());
    assert!(session.accept(&request(2, "status"), false).is_err());
    session.accept(&request(3, "status"), false).unwrap();
    session.accept(&request(4, "finish"), true).unwrap();
    assert!(session.accept(&request(5, "status"), true).is_err());
}
#[test]
fn install_wire_rejects_old_version_wrong_session_extra_field_and_frame_endings() {
    for field in ["protocolVersion", "session", "extra"] {
        let mut value: serde_json::Value = serde_json::from_slice(&request(1, "hello")).unwrap();
        value[field] = match field {
            "protocolVersion" => json!(1),
            "session" => json!("different"),
            _ => json!(true),
        };
        assert!(InstallSession::new(NONCE.into())
            .unwrap()
            .accept(&serde_json::to_vec(&value).unwrap(), false)
            .is_err());
    }
    let mut frame = request(1, "hello");
    frame.push(b'\n');
    assert!(InstallSession::new(NONCE.into())
        .unwrap()
        .accept(&frame, false)
        .is_err());
}
