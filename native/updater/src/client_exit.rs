//! 新控制协议的退出确认：验证进程身份，等待真实进程结束，再独占打开程序载荷。
//! 连接拒绝只表示没有控制服务，仍须检查全部 EXE/DLL 未被占用；绝不以固定 sleep 判定成功。
use std::{io::{Read, Write}, net::{SocketAddr, TcpStream}, path::Path, time::{Duration, Instant}};
use crate::package_manifest;

pub fn parse_reply(bytes: &[u8]) -> Result<(u32,u64),String> {
    let reply = std::str::from_utf8(bytes).map_err(|_|"exit ACK is not UTF-8")?;
    let body = reply.strip_prefix("mystia-companion-v2:ok:").and_then(|s|s.strip_suffix('\n')).ok_or("invalid exit ACK protocol")?;
    let values: Vec<_> = body.split(':').collect();
    if values.len()!=2 || values.iter().any(|v|v.is_empty() || !v.bytes().all(|b|b.is_ascii_digit())) {return Err("invalid exit ACK identity".into());}
    let pid = values[0].parse::<u32>().map_err(|_|"invalid exit PID")?;
    let created = values[1].parse::<u64>().map_err(|_|"invalid exit creation time")?;
    if pid == 0 || created == 0 {return Err("empty exit identity".into());}
    Ok((pid,created))
}
pub fn request_and_wait(port: u16, root: &Path, timeout: Duration) -> Result<(),String> {
    let deadline=Instant::now()+timeout;
    let address=SocketAddr::from(([127,0,0,1],port));
    let remaining=|| deadline.checked_duration_since(Instant::now()).filter(|value|!value.is_zero()).ok_or_else(||"client exit acknowledgement deadline exceeded".to_string());
    match TcpStream::connect_timeout(&address,remaining()?.min(Duration::from_secs(2))) {
        Ok(mut stream) => {
            stream.set_read_timeout(Some(Duration::from_secs(2))).map_err(|e|e.to_string())?;
            stream.set_write_timeout(Some(Duration::from_secs(2))).map_err(|e|e.to_string())?;
            stream.write_all(b"mystia-companion-v2:exit\n").map_err(|e|e.to_string())?;
            let mut reply=Vec::new();
            while reply.len()<128 {
                // 每次读取都重新收紧全局期限，防止逐字节慢速回应把退出等待延长数分钟。
                stream.set_read_timeout(Some(remaining()?.min(Duration::from_secs(2)))).map_err(|e|e.to_string())?;
                let mut byte=[0u8]; if stream.read(&mut byte).map_err(|e|e.to_string())? == 0 {break;}
                reply.push(byte[0]); if byte[0]==b'\n' {break;}
            }
            let (pid,created)=parse_reply(&reply)?;
            wait_identity(pid,created,deadline.saturating_duration_since(Instant::now()))?;
        }
        Err(error) if error.kind()==std::io::ErrorKind::ConnectionRefused => {}
        Err(error) => return Err(format!("client control connection failed: {error}")),
    }
    assert_unlocked(root)
}
#[cfg(windows)]
fn wait_identity(pid:u32,created:u64,timeout:Duration)->Result<(),String>{
    use windows_sys::Win32::{Foundation::{CloseHandle,GetLastError,FILETIME,WAIT_OBJECT_0,ERROR_INVALID_PARAMETER},System::Threading::{OpenProcess,GetProcessTimes,WaitForSingleObject,PROCESS_QUERY_LIMITED_INFORMATION,PROCESS_SYNCHRONIZE}};
    // 保留真实进程 HANDLE，创建时间必须完全匹配 ACK，防止 PID 已被另一进程重用。
    unsafe {
        let handle=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|PROCESS_SYNCHRONIZE,0,pid);
        if handle.is_null() {
            return if GetLastError()==ERROR_INVALID_PARAMETER {Ok(())} else {Err("cannot open acknowledged client identity".into())};
        }
        let mut creation:FILETIME=std::mem::zeroed();let mut exit:FILETIME=std::mem::zeroed();let mut kernel:FILETIME=std::mem::zeroed();let mut user:FILETIME=std::mem::zeroed();
        let result=if GetProcessTimes(handle,&mut creation,&mut exit,&mut kernel,&mut user)==0 {Err("cannot query client creation time".into())}
        else if ((creation.dwHighDateTime as u64)<<32 | creation.dwLowDateTime as u64)!=created {Err("client identity changed before exit wait".into())}
        else if WaitForSingleObject(handle,timeout.as_millis().min(u32::MAX as u128) as u32)!=WAIT_OBJECT_0 {Err("client did not exit before bounded deadline".into())} else {Ok(())};
        CloseHandle(handle); result
    }
}
#[cfg(not(windows))]
fn wait_identity(_pid:u32,_created:u64,_timeout:Duration)->Result<(),String>{Err("client identity exit wait requires Windows".into())}

pub fn assert_unlocked(root:&Path)->Result<(),String>{
    for (name,path) in package_manifest::tree(root)? {
        if !(name.to_ascii_lowercase().ends_with(".exe") || name.to_ascii_lowercase().ends_with(".dll")) {continue;}
        #[cfg(windows)] {
            use std::os::windows::ffi::OsStrExt;
            use windows_sys::Win32::{Foundation::{CloseHandle,INVALID_HANDLE_VALUE,GENERIC_READ},Storage::FileSystem::{CreateFileW,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL}};
            let wide:Vec<u16>=path.as_os_str().encode_wide().chain(Some(0)).collect();
            // shareMode=0，验证没有其它进程持有冲突读/写/删除句柄；失败保留旧安装，不重试覆盖。
            unsafe { let handle=CreateFileW(wide.as_ptr(),GENERIC_READ,0,std::ptr::null(),OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,std::ptr::null_mut());
                if handle==INVALID_HANDLE_VALUE{return Err(format!("client payload still locked: {name}"));}CloseHandle(handle); }
        }
        #[cfg(not(windows))] { let _=path; }
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test] fn ack_rejects_wrong_protocol_missing_identity_and_overflow(){
        for value in ["ok\n","mystia-companion-v2:ok:0:1\n","mystia-companion-v2:ok:1:0\n","mystia-companion-v2:ok:1:2:3\n","mystia-companion-v2:ok:1:18446744073709551616\n"] {assert!(parse_reply(value.as_bytes()).is_err());}
        assert_eq!(parse_reply(b"mystia-companion-v2:ok:123:456\n").unwrap(),(123,456));
    }
    /// 单独的受控子进程只短暂等待，供父测试证明真实 HANDLE 等待与超时，不启动产品/游戏。
    #[test] #[ignore] fn exit_child(){ std::thread::sleep(Duration::from_millis(800)); }
    #[cfg(windows)]
    #[test] fn process_identity_rejects_reuse_timeout_then_observes_exit(){
        use std::os::windows::process::CommandExt;
        use windows_sys::Win32::{Foundation::{CloseHandle,FILETIME},System::Threading::{OpenProcess,GetProcessTimes,PROCESS_QUERY_LIMITED_INFORMATION}};
        let mut child=std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact","client_exit::tests::exit_child","--ignored"])
            .creation_flags(0x08000000).stdout(std::process::Stdio::null()).spawn().unwrap();
        let created=unsafe {
            let handle=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,0,child.id());assert!(!handle.is_null());
            let mut a:FILETIME=std::mem::zeroed();let mut b:FILETIME=std::mem::zeroed();let mut c:FILETIME=std::mem::zeroed();let mut d:FILETIME=std::mem::zeroed();
            assert_ne!(GetProcessTimes(handle,&mut a,&mut b,&mut c,&mut d),0);CloseHandle(handle);
            (a.dwHighDateTime as u64)<<32|a.dwLowDateTime as u64
        };
        assert!(wait_identity(child.id(),created+1,Duration::from_millis(1)).unwrap_err().contains("identity changed"));
        assert!(wait_identity(child.id(),created,Duration::from_millis(1)).unwrap_err().contains("deadline"));
        assert!(wait_identity(child.id(),created,Duration::from_secs(5)).is_ok());
        assert!(child.wait().unwrap().success());
    }
    #[cfg(windows)]
    #[test] fn loaded_payload_handle_prevents_replacement(){
        use std::os::windows::fs::OpenOptionsExt;
        let root=std::env::temp_dir().join(format!("mystia-updater-handle-{}",std::process::id()));
        std::fs::create_dir(&root).unwrap();
        let file=root.join("controlled.dll");std::fs::write(&file,b"artificial-payload").unwrap();
        let held=std::fs::OpenOptions::new().read(true).share_mode(0).open(file).unwrap();
        assert!(assert_unlocked(&root).unwrap_err().contains("still locked"));drop(held);
        assert_unlocked(&root).unwrap();std::fs::remove_dir_all(root).unwrap();
    }
    #[test] fn malformed_control_ack_never_becomes_success(){
        let listener=std::net::TcpListener::bind(("127.0.0.1",0)).unwrap();let port=listener.local_addr().unwrap().port();
        let server=std::thread::spawn(move||{let(mut connection,_)=listener.accept().unwrap();let mut request=[0u8;25];
            let count=connection.read(&mut request).unwrap();assert_eq!(&request[..count],b"mystia-companion-v2:exit\n");connection.write_all(b"ok\n").unwrap();});
        let error=request_and_wait(port,Path::new("unused-no-filesystem-access"),Duration::from_secs(2)).unwrap_err();
        assert!(error.contains("protocol"), "unexpected control failure: {error}");server.join().unwrap();
    }
}
