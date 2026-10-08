#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(windows)]
#[allow(dead_code)]
#[path = "../windows_bootstrap.rs"]
mod windows_bootstrap;

fn main() {
    #[cfg(windows)]
    if let Err(error) = windows_bootstrap::install_fixture::run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
    #[cfg(not(windows))]
    {
        eprintln!("The isolated installation fixture requires Windows.");
        std::process::exit(2);
    }
}
