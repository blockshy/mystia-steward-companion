//! 限定旧 Windows 客户端资料的候选位置，不负责枚举、迁移或删除。
//!
//! 公开 v1.3.1 的 Tauri 标识与 Flutter 私有存储根不同。这里仅按当前用户的
//! Known Folders 解析该固定标识；调用方仍须验证普通文件/目录、进程退出和清理计划。

use std::path::PathBuf;

#[cfg(any(windows, test))]
use std::path::Path;

/// 返回当前 Windows 用户的两个旧客户端候选，顺序固定为 WebView 目录、窗口状态文件。
///
/// 第一项包含旧 localStorage 中的偏好、Token 和设备身份，不能作为纯缓存自动删除。
/// 第二项只指向 RoamingAppData 下的 `.window-state.json`，不授权删除未知兄弟文件。
/// 本接口不接受任意用户目录或测试根替换，也不读取候选内容；缺失目录由上层预览处理。
/// 非 Windows 明确拒绝，避免把 Android 同包名私有数据误当作可清理目录。
pub fn legacy_roots() -> Result<Vec<PathBuf>, String> {
    #[cfg(windows)]
    {
        use windows_sys::Win32::UI::Shell::{FOLDERID_LocalAppData, FOLDERID_RoamingAppData};

        let local = known_folder(&FOLDERID_LocalAppData)?;
        let roaming = known_folder(&FOLDERID_RoamingAppData)?;
        map_known_folders(&local, &roaming)
    }
    #[cfg(not(windows))]
    {
        Err("旧客户端资料清理仅支持 Windows 当前用户。".into())
    }
}

/// 纯路径映射独立于系统解析，便于用合成路径验证命名边界；不会作为 CLI 注入入口。
#[cfg(any(windows, test))]
fn map_known_folders(local: &Path, roaming: &Path) -> Result<Vec<PathBuf>, String> {
    if !local.is_absolute() || !roaming.is_absolute() {
        return Err("系统应用数据目录不是绝对路径。".into());
    }
    const LEGACY_IDENTIFIER: &str = "com.tyukki.mystia-steward-companion";
    Ok(vec![
        local.join(LEGACY_IDENTIFIER),
        roaming.join(LEGACY_IDENTIFIER).join(".window-state.json"),
    ])
}

/// 由系统解析当前用户的实际位置，保留重定向后的路径，但不创建或枚举资料目录。
#[cfg(windows)]
fn known_folder(id: &windows_sys::core::GUID) -> Result<PathBuf, String> {
    use std::{ffi::OsString, os::windows::ffi::OsStringExt, ptr};
    use windows_sys::Win32::{
        System::Com::CoTaskMemFree,
        UI::Shell::{SHGetKnownFolderPath, KF_FLAG_DONT_VERIFY},
    };

    // 系统返回的 UTF-16 字符串归调用方释放；用守卫覆盖 HRESULT 失败等提前返回分支。
    struct FolderBuffer(*mut u16);
    impl Drop for FolderBuffer {
        fn drop(&mut self) {
            if !self.0.is_null() {
                unsafe { CoTaskMemFree(self.0.cast()) };
            }
        }
    }

    let mut buffer = FolderBuffer(ptr::null_mut());
    // 不传替代用户令牌，也不使用 CREATE 标志。DONT_VERIFY 允许只读预览尚未存在的目录。
    let result = unsafe {
        SHGetKnownFolderPath(
            id,
            KF_FLAG_DONT_VERIFY as u32,
            ptr::null_mut(),
            &mut buffer.0,
        )
    };
    if result < 0 || buffer.0.is_null() {
        return Err(format!(
            "无法解析当前用户的系统应用数据目录（HRESULT 0x{:08X}）。",
            result as u32
        ));
    }
    // 成功的 Known Folders 契约返回以 NUL 结束的字符串；保留 Windows 原生 UTF-16，
    // 不通过有损 UTF-8 转换修改候选路径，也不把真实用户目录写入错误消息。
    let mut length = 0;
    unsafe {
        while *buffer.0.add(length) != 0 {
            length += 1;
        }
        Ok(PathBuf::from(OsString::from_wide(
            std::slice::from_raw_parts(buffer.0, length),
        )))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 固定合成根不查询 Known Folders、不读环境变量，也不接触真实客户端资料。
    fn synthetic_roots() -> (PathBuf, PathBuf) {
        #[cfg(windows)]
        let base = PathBuf::from(r"C:\synthetic-client-root");
        #[cfg(not(windows))]
        let base = PathBuf::from("/synthetic-client-root");
        (base.join("local"), base.join("roaming"))
    }

    #[test]
    fn candidates_are_exact_legacy_directory_and_window_state() {
        let (local, roaming) = synthetic_roots();
        assert_eq!(
            map_known_folders(&local, &roaming).unwrap(),
            vec![
                local.join("com.tyukki.mystia-steward-companion"),
                roaming
                    .join("com.tyukki.mystia-steward-companion")
                    .join(".window-state.json"),
            ]
        );
    }

    #[test]
    fn candidates_do_not_contain_current_flutter_storage() {
        let (local, roaming) = synthetic_roots();
        let flutter = local.join("mystia-steward-companion").join("flutter-v1");
        for candidate in map_known_folders(&local, &roaming).unwrap() {
            assert!(!candidate.starts_with(&flutter));
            assert!(!flutter.starts_with(&candidate));
        }
    }

    #[test]
    fn relative_system_roots_are_rejected() {
        let (local, roaming) = synthetic_roots();
        assert!(map_known_folders(Path::new("relative-local"), &roaming).is_err());
        assert!(map_known_folders(&local, Path::new("relative-roaming")).is_err());
    }

    #[cfg(not(windows))]
    #[test]
    fn unsupported_platform_does_not_resolve_candidates() {
        assert!(legacy_roots().is_err());
    }
}
