use std::io;
use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_INVALID_PARAMETER, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::System::Threading::{
    OpenProcess, WaitForSingleObject, PROCESS_SYNCHRONIZE,
};

struct ProcessHandle(isize);

impl ProcessHandle {
    fn raw(&self) -> HANDLE {
        self.0 as HANDLE
    }
}

impl Drop for ProcessHandle {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.raw());
        }
    }
}

/// Observes the process object opened at startup, without any permission to
/// close or terminate it. The PID-only launch protocol cannot identify an
/// original process that exited before this open; PID reuse may conservatively
/// prolong waiting, but cannot cause an action against an unrelated process.
pub struct GameProcess {
    handle: Option<ProcessHandle>,
}

impl GameProcess {
    pub fn open(pid: u32) -> Result<Self, String> {
        if pid == 0 {
            return Err("游戏进程编号无效。".to_string());
        }
        let handle = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, 0, pid) };
        if handle.is_null() {
            let code = unsafe { GetLastError() };
            if code == ERROR_INVALID_PARAMETER {
                return Ok(Self { handle: None });
            }
            return Err(format!(
                "无法确认游戏进程状态：{}",
                io::Error::from_raw_os_error(code as i32)
            ));
        }
        Ok(Self {
            handle: Some(ProcessHandle(handle as isize)),
        })
    }

    pub fn is_running(&self) -> Result<bool, String> {
        let Some(handle) = &self.handle else {
            return Ok(false);
        };
        match unsafe { WaitForSingleObject(handle.raw(), 0) } {
            WAIT_OBJECT_0 => Ok(false),
            WAIT_TIMEOUT => Ok(true),
            _ => Err(format!(
                "无法确认游戏是否已退出：{}",
                io::Error::last_os_error()
            )),
        }
    }
}
