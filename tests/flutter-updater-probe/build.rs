use std::env;
use std::fs;
use std::path::{Path, PathBuf};

fn main() {
    println!("cargo:rerun-if-env-changed=MYSTIA_UPDATER_PROBE_GIT_SHA");
    let sha =
        env::var("MYSTIA_UPDATER_PROBE_GIT_SHA").unwrap_or_else(|_| "UNCONFIGURED".to_owned());
    assert!(
        sha == "UNCONFIGURED"
            || (sha.len() == 40
                && sha
                    .bytes()
                    .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))),
        "invalid probe Git SHA"
    );
    println!("cargo:rustc-env=MYSTIA_UPDATER_PROBE_EMBEDDED_GIT_SHA={sha}");
    let names = [
        "MYSTIA_UPDATER_PROBE_BUNDLE_ZIP",
        "MYSTIA_UPDATER_PROBE_BUNDLE_MANIFEST",
        "MYSTIA_UPDATER_PROBE_PRODUCT_VERSION",
    ];
    for name in names {
        println!("cargo:rerun-if-env-changed={name}");
    }
    let values: Vec<_> = names.iter().map(env::var_os).collect();
    let output = PathBuf::from(env::var_os("OUT_DIR").expect("OUT_DIR"));
    if values.iter().all(Option::is_none) {
        fs::write(output.join("bundle.zip"), []).expect("write empty test archive");
        fs::write(output.join("bundle-manifest.json"), []).expect("write empty test manifest");
        println!("cargo:rustc-env=MYSTIA_UPDATER_PROBE_EMBEDDED_VERSION=UNCONFIGURED");
        return;
    }
    assert!(
        values.iter().all(Option::is_some),
        "all three MYSTIA_UPDATER_PROBE_* build inputs are required together"
    );
    for (index, destination) in ["bundle.zip", "bundle-manifest.json"].iter().enumerate() {
        let source = Path::new(values[index].as_ref().expect("checked above"));
        assert!(
            source.is_absolute() && source.is_file(),
            "probe bundle inputs must be absolute regular files"
        );
        let limit = if index == 0 {
            512 * 1024 * 1024
        } else {
            1024 * 1024
        };
        assert!(
            fs::metadata(source).expect("input metadata").len() <= limit,
            "probe build input is oversized"
        );
        println!("cargo:rerun-if-changed={}", source.display());
        fs::copy(source, output.join(destination)).expect("copy embedded probe input");
    }
    let version = values[2]
        .as_ref()
        .expect("checked above")
        .to_str()
        .expect("Unicode product version");
    assert!(!version.contains(['\r', '\n']), "invalid version");
    println!("cargo:rustc-env=MYSTIA_UPDATER_PROBE_EMBEDDED_VERSION={version}");
}
