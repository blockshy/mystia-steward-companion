#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(windows)]
#[path = "../windows_bootstrap.rs"]
mod windows_bootstrap;

fn main() {
    #[cfg(windows)]
    if let Err(error) = windows_bootstrap::run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
    #[cfg(not(windows))]
    {
        eprintln!("Windows bootstrap is unavailable on this platform; use cargo test for read-only P0 contracts.");
        std::process::exit(2);
    }
}
