// Windows 图标通过 SDK rc.exe 编译进独立更新器。显式跟踪 ICO，避免增量构建复用旧图标。
use std::{env, fs, path::PathBuf, process::Command};
fn main() {
    println!("cargo:rerun-if-changed=resources/app_icon.ico");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") { return; }
    let root = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let output = PathBuf::from(env::var("OUT_DIR").unwrap());
    let icon = root.join("resources/app_icon.ico").to_string_lossy().replace('\\', "/");
    let source = output.join("updater.rc"); let resource = output.join("updater.res");
    fs::write(&source, format!("32512 ICON \"{icon}\"\n")).unwrap();
    let kits = env::var("WindowsSdkDir").map(PathBuf::from).unwrap_or_else(|_| PathBuf::from(env::var("ProgramFiles(x86)").unwrap()).join("Windows Kits/10"));
    let compiler = kits.join("bin/10.0.26100.0/x64/rc.exe");
    let status = Command::new(compiler).arg("/nologo").arg(format!("/fo{}",resource.display())).arg(source).status().expect("无法启动锁定 Windows SDK rc.exe");
    assert!(status.success(), "Windows 图标资源编译失败");
    println!("cargo:rustc-link-arg-bin=mystia-steward-companion-updater={}", resource.display());
}
