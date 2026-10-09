fn main() {
    // tauri-build 的 Windows 资源编译未自动声明 ICO 输入依赖。
    // 显式跟踪整个图标目录，保证只替换图标时也重新生成资源库，避免增量构建的
    // 主程序和独立 updater 继续链接旧图标；其他平台沿用同一资源变更触发规则。
    println!("cargo:rerun-if-changed=icons");
    tauri_build::build()
}
