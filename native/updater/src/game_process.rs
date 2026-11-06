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

/// 在窗口初始化时打开一次游戏进程对象，只持有 SYNCHRONIZE 观察权限。
/// 后续轮询同一 HANDLE，避免进程退出后 PID 被重用导致等待对象改变。
/// 启动参数目前仅有 PID：若原进程在打开前已退出且 PID 被重用，最多会保守延长等待；
/// 本对象没有关闭窗口或终止进程的权限，不会对同号新进程执行任何写操作。
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

#[cfg(test)]
mod tests {
    use super::*;

    /// 使用测试程序自己的短生命周期子进程验证真实 HANDLE；不启动游戏或客户端。
    #[test]
    fn observes_one_process_object_without_termination_rights() {
        use std::os::windows::process::CommandExt;
        let mut child = std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "client_exit::tests::exit_child", "--ignored"])
            .creation_flags(0x08000000)
            .stdout(std::process::Stdio::null())
            .spawn()
            .unwrap();
        let game = GameProcess::open(child.id()).unwrap();
        assert!(game.is_running().unwrap());
        assert!(child.wait().unwrap().success());
        assert!(!game.is_running().unwrap());
        // 对象退出后的观察结果不会再次按 PID 查找，从而不会绑定后来的同号进程。
        assert!(!game.is_running().unwrap());
    }

    #[test]
    fn rejects_empty_process_identity() {
        assert!(GameProcess::open(0).is_err());
    }
}
