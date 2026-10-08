//! Windows P0 process harness only. No Mod/game API, game handle, control-port
//! write, install operation, or success status exists in this executable.

use std::ffi::c_void;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::mem::{offset_of, size_of};
use std::os::windows::io::AsRawHandle;
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::ptr::{null, null_mut};
use std::time::{Duration, Instant};

use mystia_steward_companion_flutter_updater_probe::archive::extract_verified_zip;
use mystia_steward_companion_flutter_updater_probe::bundle::{parse_manifest, verify_bundle};
use mystia_steward_companion_flutter_updater_probe::launch::{
    parse_legacy_arguments, verify_runner_binding, InstallArguments,
};
use mystia_steward_companion_flutter_updater_probe::status::{
    LegacyInstallState, LegacyInstallStatus,
};
use mystia_steward_companion_flutter_updater_probe::wire::{ProbeSession, MAX_FRAME_BYTES};
use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, LocalFree, ERROR_IO_PENDING, ERROR_PIPE_CONNECTED, HANDLE,
    INVALID_HANDLE_VALUE, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows_sys::Win32::Security::Cryptography::{
    BCryptGenRandom, BCRYPT_USE_SYSTEM_PREFERRED_RNG,
};
use windows_sys::Win32::Security::{
    GetTokenInformation, TokenGroups, SECURITY_ATTRIBUTES, SID_AND_ATTRIBUTES, TOKEN_GROUPS,
    TOKEN_QUERY,
};
use windows_sys::Win32::Storage::FileSystem::{
    ReadFile, WriteFile, FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX,
};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, GetNamedPipeClientProcessId, PIPE_READMODE_BYTE,
    PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_WAIT,
};
use windows_sys::Win32::System::Threading::{
    CreateEventW, GetCurrentProcess, OpenProcessToken, WaitForMultipleObjects, WaitForSingleObject,
};
use windows_sys::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};

const EMBEDDED_ZIP: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/bundle.zip"));
const EMBEDDED_MANIFEST: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/bundle-manifest.json"));
const PRODUCT_VERSION: &str = env!("MYSTIA_UPDATER_PROBE_EMBEDDED_VERSION");
const UI_ENTRYPOINT: &str = "mystia-steward-companion-updater-ui.exe";
type Outcome<T> = std::result::Result<T, String>;

#[cfg(feature = "install-fixture")]
#[allow(dead_code)]
#[path = "windows_install_fixture.rs"]
pub mod install_fixture;

pub fn run() -> Outcome<()> {
    // Malformed CLI has no trusted output path. Never recover a status path by
    // partially parsing rejected arguments and then writing to it.
    let arguments =
        parse_legacy_arguments(std::env::args_os().skip(1)).map_err(|error| error.to_string())?;
    let result = run_probe(&arguments);
    let (state, message) = match &result {
        Ok(()) => (
            LegacyInstallState::Cancelled,
            "P0 只读探针完成 hello/cancel；未安装、未关闭游戏、未修改插件目录。".to_owned(),
        ),
        Err(error) => (
            LegacyInstallState::Failed,
            format!("P0 只读探针失败；未安装、未关闭游戏、未修改插件目录。{error}"),
        ),
    };
    let status = LegacyInstallStatus {
        state,
        message,
        progress: 0,
    };
    let write_result = write_status(&arguments.status_file, &status);
    match (result, write_result) {
        (Err(error), Err(write_error)) => {
            Err(format!("{error}; status publication failed: {write_error}"))
        }
        (Err(error), _) => Err(error),
        (_, Err(error)) => Err(error),
        (Ok(()), Ok(())) => Ok(()),
    }
}

fn run_probe(arguments: &InstallArguments) -> Outcome<()> {
    if EMBEDDED_ZIP.is_empty() || EMBEDDED_MANIFEST.is_empty() {
        return Err("Bootstrap was built without the three explicit bundle inputs.".to_owned());
    }
    let runner = std::env::current_exe().map_err(|error| error.to_string())?;
    arguments
        .validate_runner_location(&runner)
        .map_err(|error| error.to_string())?;
    verify_runner_binding(&runner, &arguments.staged_dir).map_err(|error| error.to_string())?;
    let manifest = parse_manifest(EMBEDDED_MANIFEST, PRODUCT_VERSION, UI_ENTRYPOINT)
        .map_err(|error| error.to_string())?;
    let nonce = random_nonce()?;
    let parent = runner.parent().ok_or("runner has no parent")?;
    let stage = parent.join(format!("p0-bundle-{nonce}.partial"));
    let bundle = parent.join(format!("p0-bundle-{nonce}"));
    // verify_runner_binding validates all existing ancestors. The unpredictable
    // task directory is created exclusively; existing paths are never reused.
    extract_verified_zip(EMBEDDED_ZIP, &manifest, &stage).map_err(|error| error.to_string())?;
    if bundle.try_exists().map_err(|error| error.to_string())? {
        return Err("P0 bundle destination already exists".to_owned());
    }
    fs::rename(&stage, &bundle).map_err(|error| error.to_string())?;
    let executable = verify_bundle(&bundle, &manifest).map_err(|error| error.to_string())?;
    let pipe_name = format!(
        r"\\.\pipe\mystia-steward-companion-p0-{}-{nonce}",
        std::process::id()
    );
    let pipe = create_pipe(&pipe_name)?;
    let child = Command::new(executable)
        .current_dir(&bundle)
        .arg(format!("--updater-probe-pipe={pipe_name}"))
        .arg(format!("--updater-probe-session={nonce}"))
        .arg(format!("--updater-probe-parent-pid={}", std::process::id()))
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| format!("start verified Flutter probe UI: {error}"))?;
    let mut child = ProbeChild(child);
    let child_handle = child.0.as_raw_handle() as HANDLE;
    let hello_deadline = Instant::now() + Duration::from_secs(30);
    connect(&pipe, child_handle, hello_deadline)?;
    let mut actual_pid = 0;
    // SAFETY: pipe owns a connected kernel pipe and actual_pid is valid storage.
    if unsafe { GetNamedPipeClientProcessId(pipe.0, &mut actual_pid) } == 0
        || actual_pid != child.0.id()
    {
        return Err("Named Pipe peer is not the exact launched UI process".to_owned());
    }
    if unsafe { WaitForSingleObject(child_handle, 0) } != WAIT_TIMEOUT {
        return Err("UI exited before the authenticated handshake".to_owned());
    }
    let mut session = ProbeSession::new(nonce).map_err(|error| error.to_string())?;
    let frame = read_frame(&pipe, child_handle, hello_deadline)?;
    let response = session.accept(&frame).map_err(|error| error.to_string())?;
    write_frame(&pipe, child_handle, &response)?;
    let cancel_deadline = Instant::now() + arguments.wait_timeout.min(Duration::from_secs(300));
    let frame = read_frame(&pipe, child_handle, cancel_deadline)?;
    let response = session.accept(&frame).map_err(|error| error.to_string())?;
    write_frame(&pipe, child_handle, &response)?;
    // Only this Child handle is observed. The supplied game PID is never opened.
    if unsafe { WaitForSingleObject(child_handle, 10_000) } != WAIT_OBJECT_0 {
        return Err("UI did not exit within 10 seconds after cancelled response".to_owned());
    }
    let exit = child.0.wait().map_err(|error| error.to_string())?;
    if !exit.success() {
        return Err(format!("UI exited with {exit}"));
    }
    Ok(())
}

/// Any failure cleans up only the child created by this harness. It never looks
/// up a process by name or uses the game PID from the old CLI.
struct ProbeChild(Child);
impl Drop for ProbeChild {
    fn drop(&mut self) {
        if !matches!(self.0.try_wait(), Ok(Some(_))) {
            let _ = self.0.kill();
            let _ = self.0.wait();
        }
    }
}

struct Handle(HANDLE);
impl Handle {
    fn checked(handle: HANDLE, operation: &str) -> Outcome<Self> {
        if handle.is_null() || handle == INVALID_HANDLE_VALUE {
            Err(last_error(operation))
        } else {
            Ok(Self(handle))
        }
    }
}
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}

struct LocalAllocation(*mut c_void);
impl Drop for LocalAllocation {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                LocalFree(self.0);
            }
        }
    }
}

fn random_nonce() -> Outcome<String> {
    let mut bytes = [0_u8; 16];
    let status = unsafe {
        BCryptGenRandom(
            null_mut(),
            bytes.as_mut_ptr(),
            bytes.len() as u32,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        )
    };
    if status < 0 {
        return Err(format!("BCryptGenRandom failed: {status}"));
    }
    Ok(bytes.iter().map(|byte| format!("{byte:02x}")).collect())
}

fn create_pipe(name: &str) -> Outcome<Handle> {
    let sid = current_logon_sid()?;
    // Read/write individual rights, deliberately excluding FILE_APPEND_DATA
    // (the same bit as FILE_CREATE_PIPE_INSTANCE), for this logon session only.
    let descriptor = wide(&format!("D:P(A;;0x12019b;;;{sid})"));
    let mut descriptor_pointer = null_mut();
    if unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            descriptor.as_ptr(),
            SDDL_REVISION_1,
            &mut descriptor_pointer,
            null_mut(),
        )
    } == 0
    {
        return Err(last_error("build pipe security descriptor"));
    }
    let descriptor = LocalAllocation(descriptor_pointer);
    let attributes = SECURITY_ATTRIBUTES {
        nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: descriptor.0,
        bInheritHandle: 0,
    };
    let name = wide(name);
    Handle::checked(
        unsafe {
            CreateNamedPipeW(
                name.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1,
                MAX_FRAME_BYTES as u32 + 1,
                MAX_FRAME_BYTES as u32 + 1,
                0,
                &attributes,
            )
        },
        "create private pipe",
    )
}

fn current_logon_sid() -> Outcome<String> {
    let mut raw_token = null_mut();
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut raw_token) } == 0 {
        return Err(last_error("open current process token"));
    }
    let token = Handle::checked(raw_token, "open token")?;
    let mut length = 0;
    unsafe {
        GetTokenInformation(token.0, TokenGroups, null_mut(), 0, &mut length);
    }
    if length < size_of::<TOKEN_GROUPS>() as u32 || length > 64 * 1024 {
        return Err("invalid token group size".to_owned());
    }
    let mut storage = vec![0_usize; (length as usize).div_ceil(size_of::<usize>())];
    if unsafe {
        GetTokenInformation(
            token.0,
            TokenGroups,
            storage.as_mut_ptr().cast(),
            length,
            &mut length,
        )
    } == 0
    {
        return Err(last_error("read token groups"));
    }
    let group_count = unsafe { (*storage.as_ptr().cast::<TOKEN_GROUPS>()).GroupCount } as usize;
    let offset = offset_of!(TOKEN_GROUPS, Groups);
    if (length as usize) < offset
        || length as usize > storage.len() * size_of::<usize>()
        || group_count > (length as usize - offset) / size_of::<SID_AND_ATTRIBUTES>()
    {
        return Err("invalid token group count".to_owned());
    }
    let entries = unsafe {
        std::slice::from_raw_parts(
            storage
                .as_ptr()
                .cast::<u8>()
                .add(offset)
                .cast::<SID_AND_ATTRIBUTES>(),
            group_count,
        )
    };
    let logon = entries
        .iter()
        .filter(|entry| entry.Attributes & 0xc000_0000 == 0xc000_0000)
        .collect::<Vec<_>>();
    if logon.len() != 1 {
        return Err("current token has no unambiguous logon SID".to_owned());
    }
    let mut sid_string = null_mut();
    if unsafe { ConvertSidToStringSidW(logon[0].Sid, &mut sid_string) } == 0 {
        return Err(last_error("format logon SID"));
    }
    let allocation = LocalAllocation(sid_string.cast());
    let mut length = 0;
    while length < 512 && unsafe { *sid_string.add(length) } != 0 {
        length += 1;
    }
    if length == 512 {
        return Err("unexpected SID string length".to_owned());
    }
    let value = String::from_utf16(unsafe { std::slice::from_raw_parts(sid_string, length) })
        .map_err(|error| error.to_string())?;
    drop(allocation);
    Ok(value)
}

fn connect(pipe: &Handle, child: HANDLE, deadline: Instant) -> Outcome<()> {
    let event = new_event()?;
    let mut overlapped = OVERLAPPED {
        hEvent: event.0,
        ..Default::default()
    };
    if unsafe { ConnectNamedPipe(pipe.0, &mut overlapped) } != 0 {
        return Ok(());
    }
    match unsafe { GetLastError() } {
        ERROR_PIPE_CONNECTED => Ok(()),
        ERROR_IO_PENDING => finish_io(pipe, &mut overlapped, child, deadline).map(|_| ()),
        code => Err(format!("ConnectNamedPipe failed: {code}")),
    }
}

fn read_frame(pipe: &Handle, child: HANDLE, deadline: Instant) -> Outcome<Vec<u8>> {
    let mut result = Vec::with_capacity(256);
    loop {
        if Instant::now() >= deadline {
            return Err("P0 frame deadline exceeded".to_owned());
        }
        let mut byte = [0_u8];
        let event = new_event()?;
        let mut overlapped = OVERLAPPED {
            hEvent: event.0,
            ..Default::default()
        };
        let immediate =
            unsafe { ReadFile(pipe.0, byte.as_mut_ptr(), 1, null_mut(), &mut overlapped) };
        let count = complete_submission(pipe, &mut overlapped, immediate, child, deadline)?;
        if count != 1 {
            return Err("P0 pipe disconnected before a complete frame".to_owned());
        }
        if byte[0] == b'\n' {
            return Ok(result);
        }
        if result.len() == MAX_FRAME_BYTES {
            return Err("P0 frame exceeds 16 KiB".to_owned());
        }
        result.push(byte[0]);
    }
}

fn write_frame(pipe: &Handle, child: HANDLE, bytes: &[u8]) -> Outcome<()> {
    let event = new_event()?;
    let mut overlapped = OVERLAPPED {
        hEvent: event.0,
        ..Default::default()
    };
    let immediate = unsafe {
        WriteFile(
            pipe.0,
            bytes.as_ptr(),
            bytes.len() as u32,
            null_mut(),
            &mut overlapped,
        )
    };
    let count = complete_submission(
        pipe,
        &mut overlapped,
        immediate,
        child,
        Instant::now() + Duration::from_secs(3),
    )?;
    if count as usize != bytes.len() {
        return Err("partial P0 response write".to_owned());
    }
    Ok(())
}

fn complete_submission(
    pipe: &Handle,
    overlapped: &mut OVERLAPPED,
    immediate: i32,
    child: HANDLE,
    deadline: Instant,
) -> Outcome<u32> {
    if immediate == 0 {
        let code = unsafe { GetLastError() };
        if code != ERROR_IO_PENDING {
            return Err(format!("P0 pipe I/O failed: {code}"));
        }
        return finish_io(pipe, overlapped, child, deadline);
    }
    let mut count = 0;
    if unsafe { GetOverlappedResult(pipe.0, overlapped, &mut count, 0) } == 0 {
        return Err(last_error("complete immediate pipe I/O"));
    }
    Ok(count)
}

fn finish_io(
    pipe: &Handle,
    overlapped: &mut OVERLAPPED,
    child: HANDLE,
    deadline: Instant,
) -> Outcome<u32> {
    let handles = [overlapped.hEvent, child];
    let timeout = deadline
        .saturating_duration_since(Instant::now())
        .as_millis()
        .min(u32::MAX as u128 - 1) as u32;
    let wait =
        unsafe { WaitForMultipleObjects(handles.len() as u32, handles.as_ptr(), 0, timeout) };
    let mut count = 0;
    if wait != WAIT_OBJECT_0 {
        // Drain cancellation before stack buffers/OVERLAPPED go out of scope.
        unsafe {
            CancelIoEx(pipe.0, overlapped);
            GetOverlappedResult(pipe.0, overlapped, &mut count, 1);
        }
        return Err(format!(
            "P0 pipe operation stopped (timeout/child exit/wait failure: {wait})"
        ));
    }
    if unsafe { GetOverlappedResult(pipe.0, overlapped, &mut count, 0) } == 0 {
        return Err(last_error("finish pipe I/O"));
    }
    Ok(count)
}

fn new_event() -> Outcome<Handle> {
    Handle::checked(
        unsafe { CreateEventW(null(), 1, 0, null()) },
        "create I/O event",
    )
}
fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}
fn last_error(operation: &str) -> String {
    format!("{operation}: {}", std::io::Error::last_os_error())
}

fn write_status(path: &Path, status: &LegacyInstallStatus) -> Outcome<()> {
    let parent = path.parent().ok_or("missing status parent")?;
    let mut ancestor = Some(parent);
    while let Some(current) = ancestor {
        use std::os::windows::fs::MetadataExt;
        let metadata = fs::symlink_metadata(current).map_err(|error| error.to_string())?;
        if !metadata.is_dir() || metadata.file_attributes() & 0x400 != 0 {
            return Err(
                "status parent must be a real directory, without reparse points".to_owned(),
            );
        }
        ancestor = current.parent();
    }
    if let Ok(metadata) = fs::symlink_metadata(path) {
        use std::os::windows::fs::MetadataExt;
        if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
            return Err("status destination is not a regular file".to_owned());
        }
    }
    let temporary = parent.join(format!(
        "p0-status-{}-{}.tmp",
        std::process::id(),
        random_nonce()?
    ));
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)
        .map_err(|error| error.to_string())?;
    file.write_all(&status.to_json().map_err(|error| error.to_string())?)
        .and_then(|()| file.sync_all())
        .map_err(|error| format!("write {}: {error}", temporary.display()))?;
    drop(file);
    fs::rename(&temporary, path)
        .map_err(|error| format!("publish status; retained {}: {error}", temporary.display()))
}
