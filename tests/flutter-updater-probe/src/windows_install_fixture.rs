//! Explicit second executable for an owned fixture, never a production install
//! entrypoint. The original read-only run() above has no path into this module.
use super::*;
use core::bundle::{verify_file, BundleFile};
use core::install_control::{CancelDecision, InstallControl};
use core::install_wire::{InstallCommand, InstallSession};
use core::transaction::{
    fixture_root, read_fixture_metadata, run_install, status, InstallOutcome, ProcessObservation,
};
use mystia_steward_companion_flutter_updater_probe as core;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::os::windows::io::{FromRawHandle, OwnedHandle};
use std::sync::{Arc, Mutex};
use std::thread;
use windows_sys::Win32::Foundation::FILETIME;
use windows_sys::Win32::System::Threading::{
    GetExitCodeProcess, GetProcessId, GetProcessTimes, OpenProcess, QueryFullProcessImageNameW,
    PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
};

const GIT_SHA: &str = env!("MYSTIA_UPDATER_PROBE_EMBEDDED_GIT_SHA");

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Fixture {
    schema_version: u32,
    kind: String,
    id: String,
    git_sha: String,
    game_pid: u32,
    game_creation_hex: String,
    old_manifest_sha256: String,
    new_manifest_sha256: String,
    package_zip_sha256: String,
    package_zip_bytes: u64,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Observation {
    sequence: u64,
    status: LegacyInstallStatus,
    terminal: bool,
    can_cancel: bool,
}
struct Game {
    handle: OwnedHandle,
    pid: u32,
    creation: u64,
}
impl ProcessObservation for Game {
    fn exited_zero(&self) -> core::Result<bool> {
        let handle = self.handle.as_raw_handle() as HANDLE;
        if unsafe { GetProcessId(handle) } != self.pid
            || created(handle).map_err(core_error)? != self.creation
        {
            return Err(core_error(
                "Retained fixture process identity changed.".to_owned(),
            ));
        }
        match unsafe { WaitForSingleObject(handle, 0) } {
            WAIT_TIMEOUT => Ok(false),
            WAIT_OBJECT_0 => {
                let mut code = 0;
                if unsafe { GetExitCodeProcess(handle, &mut code) } == 0 || code != 0 {
                    return Err(core_error(
                        "Retained fixture process did not exit zero.".to_owned(),
                    ));
                }
                Ok(true)
            }
            _ => Err(core_error(last_error("observe retained fixture process"))),
        }
    }
}
fn core_error(message: String) -> core::ProbeError {
    core::ProbeError {
        kind: core::ErrorKind::Io,
        message,
    }
}
fn created(handle: HANDLE) -> Outcome<u64> {
    let mut times = [FILETIME {
        dwLowDateTime: 0,
        dwHighDateTime: 0,
    }; 4];
    if unsafe {
        GetProcessTimes(
            handle,
            &mut times[0],
            &mut times[1],
            &mut times[2],
            &mut times[3],
        )
    } == 0
    {
        return Err(last_error("read process creation"));
    }
    Ok((u64::from(times[0].dwHighDateTime) << 32) | u64::from(times[0].dwLowDateTime))
}
fn read_manifest(path: &Path, expected: &str) -> Outcome<core::bundle::BundleManifest> {
    let bytes = read_fixture_metadata(path, 1024 * 1024).map_err(|e| e.to_string())?;
    if format!("{:x}", Sha256::digest(&bytes)) != expected {
        return Err("Fixture manifest hash differs.".to_owned());
    }
    parse_manifest(&bytes, PRODUCT_VERSION, core::UPDATER_FILE_NAME).map_err(|e| e.to_string())
}
fn write_new(path: &Path, value: &impl Serialize) -> Outcome<()> {
    let bytes = serde_json::to_vec_pretty(value).map_err(|e| e.to_string())?;
    if path.try_exists().map_err(|e| e.to_string())? {
        return Err("Fixture evidence already exists.".to_owned());
    }
    let temporary = path.with_extension("json.writing");
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&temporary)
        .map_err(|e| e.to_string())?;
    file.write_all(&bytes)
        .and_then(|()| file.sync_all())
        .map_err(|e| e.to_string())?;
    drop(file);
    fs::rename(&temporary, path).map_err(|e| e.to_string())
}
fn fixed_root(root: &Path) -> Outcome<String> {
    let id = root
        .file_name()
        .and_then(|s| s.to_str())
        .and_then(|s| s.strip_prefix("mystia-steward-companion-install-p0-"))
        .filter(|s| {
            s.len() == 32
                && s.bytes()
                    .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
        })
        .ok_or("Invalid private fixture root identity.")?;
    Ok(id.to_owned())
}
fn waiter(root: &Path) -> Outcome<()> {
    let id = fixed_root(root)?;
    let runner = std::env::current_exe().map_err(|e| e.to_string())?;
    if runner != root.join("runner").join(core::UPDATER_FILE_NAME) {
        return Err("Waiter is outside its fixed owned runner.".to_owned());
    }
    if read_fixture_metadata(&root.join("waiter-token.txt"), 32).map_err(|e| e.to_string())?
        != id.as_bytes()
    {
        return Err("Waiter capability mismatch.".to_owned());
    }
    write_new(
        &root.join("waiter-ready.json"),
        &serde_json::json!({"pid":std::process::id(),"creationHex":format!("{:x}",created(unsafe{GetCurrentProcess()})?)}),
    )?;
    let deadline = Instant::now() + Duration::from_secs(300);
    loop {
        if root
            .join("release-waiter.txt")
            .try_exists()
            .map_err(|e| e.to_string())?
        {
            let value = read_fixture_metadata(&root.join("release-waiter.txt"), 32)
                .map_err(|e| e.to_string())?;
            if value == id.as_bytes() {
                return Ok(());
            }
            return Err("Waiter release capability mismatch.".to_owned());
        }
        if Instant::now() >= deadline {
            return Err("Fixture waiter release deadline elapsed.".to_owned());
        }
        thread::sleep(Duration::from_millis(25));
    }
}
pub fn run() -> Outcome<()> {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    if args.len() == 2 && args[0] == "--fixture-waiter" {
        return waiter(Path::new(&args[1]));
    }
    let arguments = parse_legacy_arguments(args).map_err(|e| e.to_string())?;
    let runner = std::env::current_exe().map_err(|e| e.to_string())?;
    let root = fixture_root(&arguments, &runner).map_err(|e| e.to_string())?;
    let result = execute(arguments.clone(), &runner, &root);
    if let Err(error) = &result {
        // The complete validated fixture layout is required before any write.
        let publish = write_status(
            &arguments.status_file,
            &status(
                LegacyInstallState::Failed,
                &format!("P0 fixture: {error}"),
                0,
            ),
        );
        if let Err(publish) = publish {
            return Err(format!("{error}; failed status publication: {publish}"));
        }
    }
    result
}
fn execute(arguments: InstallArguments, runner: &Path, root: &Path) -> Outcome<()> {
    let started = Instant::now();
    if GIT_SHA.len() != 40 || EMBEDDED_ZIP.is_empty() {
        return Err("Unconfigured fixture build.".to_owned());
    }
    verify_runner_binding(runner, &arguments.staged_dir).map_err(|e| e.to_string())?;
    let fixture_bytes =
        read_fixture_metadata(&root.join("fixture.json"), 65536).map_err(|e| e.to_string())?;
    let fixture: Fixture = serde_json::from_slice(&fixture_bytes).map_err(|e| e.to_string())?;
    if fixture.schema_version != 1
        || fixture.kind != "mystia-updater-install-fixture"
        || fixture.id != fixed_root(root)?
        || fixture.git_sha != GIT_SHA
        || fixture.game_pid != arguments.game_pid.get()
    {
        return Err("Fixture descriptor identity differs.".to_owned());
    }
    let old = read_manifest(
        &root.join("old-manifest.json"),
        &fixture.old_manifest_sha256,
    )?;
    let new = read_manifest(
        &root.join("new-manifest.json"),
        &fixture.new_manifest_sha256,
    )?;
    verify_bundle(&arguments.plugin_dir, &old).map_err(|e| e.to_string())?;
    verify_bundle(&arguments.staged_dir, &new).map_err(|e| e.to_string())?;
    verify_file(
        &root.join("package.zip"),
        &BundleFile {
            path: "package.zip".to_owned(),
            size: fixture.package_zip_bytes,
            sha256: fixture.package_zip_sha256.clone(),
        },
    )
    .map_err(|e| e.to_string())?;
    let raw = unsafe {
        OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
            0,
            fixture.game_pid,
        )
    };
    if raw.is_null() {
        return Err(last_error("open exact fixture waiter"));
    }
    let owned = unsafe { OwnedHandle::from_raw_handle(raw) };
    let creation = created(raw)?;
    if format!("{creation:x}") != fixture.game_creation_hex
        || unsafe { WaitForSingleObject(raw, 0) } != WAIT_TIMEOUT
    {
        return Err("Fixture waiter creation or liveness differs.".to_owned());
    }
    let mut image = vec![0_u16; 32768];
    let mut length = image.len() as u32;
    if unsafe { QueryFullProcessImageNameW(raw, 0, image.as_mut_ptr(), &mut length) } == 0
        || !String::from_utf16_lossy(&image[..length as usize])
            .eq_ignore_ascii_case(&runner.to_string_lossy())
    {
        return Err("Fixture waiter executable differs from the retained runner.".to_owned());
    }
    let game = Game {
        handle: owned,
        pid: fixture.game_pid,
        creation,
    };
    let ui_manifest = parse_manifest(EMBEDDED_MANIFEST, PRODUCT_VERSION, UI_ENTRYPOINT)
        .map_err(|e| e.to_string())?;
    let nonce = random_nonce()?;
    let parent = runner.parent().ok_or("runner parent missing")?;
    let partial = parent.join(format!("p0-bundle-{nonce}.partial"));
    let bundle = parent.join(format!("p0-bundle-{nonce}"));
    let expand_started = Instant::now();
    extract_verified_zip(EMBEDDED_ZIP, &ui_manifest, &partial).map_err(|e| e.to_string())?;
    if bundle.try_exists().map_err(|e| e.to_string())? {
        return Err("UI bundle already exists.".to_owned());
    }
    fs::rename(&partial, &bundle).map_err(|e| e.to_string())?;
    let ui = verify_bundle(&bundle, &ui_manifest).map_err(|e| e.to_string())?;
    let expand_millis = expand_started.elapsed().as_millis();
    let pipe_name = format!(
        r"\\.\pipe\mystia-steward-companion-p0-{}-{nonce}",
        std::process::id()
    );
    let pipe = create_pipe(&pipe_name)?;
    let child = Command::new(ui)
        .current_dir(&bundle)
        .arg(format!("--updater-probe-pipe={pipe_name}"))
        .arg(format!("--updater-probe-session={nonce}"))
        .arg(format!("--updater-probe-parent-pid={}", std::process::id()))
        .arg("--updater-probe-mode=install-fixture")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| format!("start verified fixture UI: {e}"))?;
    let mut child = ProbeChild(child);
    let child_handle = child.0.as_raw_handle() as HANDLE;
    connect(
        &pipe,
        child_handle,
        Instant::now() + Duration::from_secs(30),
    )?;
    let mut peer = 0;
    if unsafe { GetNamedPipeClientProcessId(pipe.0, &mut peer) } == 0 || peer != child.0.id() {
        return Err("Fixture UI pipe peer differs.".to_owned());
    }
    let control = Arc::new(InstallControl::default());
    let observation = Arc::new(Mutex::new(Observation {
        sequence: 1,
        status: status(
            LegacyInstallState::Waiting,
            "隔离 fixture 就绪；确认后安装完整 bundle。",
            0,
        ),
        terminal: false,
        can_cancel: true,
    }));
    let journal = Arc::new(Mutex::new(Vec::<Observation>::new()));
    let mut worker = None;
    let mut retained_game = Some(game);
    let mut session = InstallSession::new(nonce).map_err(|e| e.to_string())?;
    let deadline = Instant::now() + arguments.wait_timeout.min(Duration::from_secs(300));
    let exchange_result = (|| -> Outcome<()> {
        loop {
            let frame = read_frame(&pipe, child_handle, deadline)?;
            let terminal = observation
                .lock()
                .map_err(|_| "fixture state unavailable")?
                .terminal;
            let (request, command) = session
                .accept(&frame, terminal)
                .map_err(|e| e.to_string())?;
            if command == InstallCommand::Start {
                let game = retained_game
                    .take()
                    .ok_or("fixture installation cannot restart")?;
                let arguments = arguments.clone();
                let old = old.clone();
                let new = new.clone();
                let control = control.clone();
                let state = observation.clone();
                let journal = journal.clone();
                worker = Some(thread::spawn(move || {
                    let result = run_install(&arguments, &old, &new, &game, &control, |value| {
                        write_status(&arguments.status_file, &value).map_err(core_error)?;
                        let mut state = state
                            .lock()
                            .map_err(|_| core_error("fixture state unavailable".to_owned()))?;
                        state.sequence += 1;
                        state.can_cancel = matches!(
                            value.state,
                            LegacyInstallState::Preparing
                                | LegacyInstallState::WaitingGame
                                | LegacyInstallState::GameClosed
                        );
                        state.terminal = matches!(
                            value.state,
                            LegacyInstallState::Succeeded
                                | LegacyInstallState::Cancelled
                                | LegacyInstallState::Failed
                        );
                        state.status = value;
                        journal
                            .lock()
                            .map_err(|_| core_error("fixture journal unavailable".to_owned()))?
                            .push(state.clone());
                        Ok(())
                    });
                    if let Err(error) = &result {
                        if let Ok(mut state) = state.lock() {
                            state.sequence += 1;
                            state.terminal = true;
                            state.can_cancel = false;
                            state.status =
                                status(LegacyInstallState::Failed, &error.to_string(), 0);
                        }
                    }
                    result.map_err(|e| e.to_string())
                }));
            } else if command == InstallCommand::Cancel {
                let decision = control.cancel()?;
                if worker.is_none() && decision == CancelDecision::Accepted {
                    let value = status(
                        LegacyInstallState::Cancelled,
                        "隔离 fixture 已取消，尚未替换文件。",
                        0,
                    );
                    write_status(&arguments.status_file, &value)?;
                    let mut state = observation
                        .lock()
                        .map_err(|_| "fixture state unavailable")?;
                    state.sequence += 1;
                    state.status = value;
                    state.terminal = true;
                    state.can_cancel = false;
                }
            }
            let state = observation
                .lock()
                .map_err(|_| "fixture state unavailable")?
                .clone();
            let state_name = if request == 1 {
                "ready".to_owned()
            } else {
                serde_json::to_value(state.status.state)
                    .map_err(|e| e.to_string())?
                    .as_str()
                    .ok_or("invalid state")?
                    .to_owned()
            };
            write_frame(
                &pipe,
                child_handle,
                &session
                    .reply(
                        request,
                        state.sequence,
                        &state_name,
                        &state.status,
                        state.terminal,
                        state.can_cancel,
                    )
                    .map_err(|e| e.to_string())?,
            )?;
            if command == InstallCommand::Finish {
                break;
            }
        }
        if unsafe { WaitForSingleObject(child_handle, 10000) } != WAIT_OBJECT_0 {
            return Err("Fixture UI did not exit after finish.".to_owned());
        }
        if !child.0.wait().map_err(|e| e.to_string())?.success() {
            return Err("Fixture UI returned nonzero.".to_owned());
        }
        Ok(())
    })();
    // UI loss cancels only before the shared replacement admission. A started
    // transaction owns its lifetime and is joined even if the UI has crashed.
    if exchange_result.is_err() {
        let _ = control.cancel();
    }
    let install_result = match worker {
        Some(worker) => worker
            .join()
            .map_err(|_| "Fixture installation worker panicked.".to_owned())?,
        None => Ok(InstallOutcome::Cancelled),
    };
    let final_state = observation
        .lock()
        .map_err(|_| "fixture state unavailable")?
        .clone();
    let old_bytes = old.total_bytes();
    let new_bytes = new.total_bytes();
    let runner_bytes = fs::metadata(runner).map_err(|e| e.to_string())?.len();
    write_new(
        &root.join("install-evidence.json"),
        &serde_json::json!({
            "schemaVersion":1,"kind":"isolated-updater-install-evidence","gitSha":GIT_SHA,"fixtureId":fixture.id,
            "bootstrapPid":std::process::id(),"uiPid":child.0.id(),"waiterPid":fixture.game_pid,"waiterCreationHex":fixture.game_creation_hex,
            "state":final_state,"journal":*journal.lock().map_err(|_|"fixture journal unavailable")?,
            "packageZipBytes":fixture.package_zip_bytes,"newInstalledBytes":new_bytes,"oldInstalledBytes":old_bytes,
            "embeddedUpdaterZipBytes":EMBEDDED_ZIP.len(),"updaterExpandedBytes":ui_manifest.total_bytes(),"runnerBootstrapBytes":runner_bytes,
            "logicalCoexistencePeakBytes":fixture.package_zip_bytes+old_bytes+new_bytes+runner_bytes+ui_manifest.total_bytes(),
            "runnerExpansionMillis":expand_millis,"totalMillis":started.elapsed().as_millis(),
            "installedManifestSha256":fixture.new_manifest_sha256,"backupManifestSha256":fixture.old_manifest_sha256,
            "exchangeError":exchange_result.as_ref().err(),"installError":install_result.as_ref().err(),
            "environmentScope":"controlled fixture; clean-machine claim requires separate OS/runtime evidence"
        }),
    )?;
    exchange_result?;
    install_result?;
    Ok(())
}
