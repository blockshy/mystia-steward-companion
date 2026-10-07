//! Fixed adapter for the existing MystiaFlutterProbe interactive task.
//! Build with locked Rust 1.97.1 and MYSTIA_NODE_DRIVER_GIT_SHA=<full commit>.
//! No shell text, arbitrary executable, suite, or output destination is accepted.

use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::process::Command;

const NODE_RUNS: &str = r"D:\dev\mystia-node\runs";
const POWERSHELL: &str = r"D:\dev\mystia-node\tools\powershell-7.6.4\pwsh.exe";
const ADAPTER: &str = "Node-OldMod-Probe.ps1";
const BUILD_SHA: Option<&str> = option_env!("MYSTIA_NODE_DRIVER_GIT_SHA");

#[derive(Debug, PartialEq, Eq)]
struct Arguments {
    run_id: String,
    result_file: String,
}

fn run_id_valid(value: &str) -> bool {
    (1..=80).contains(&value.len())
        && value.as_bytes()[0].is_ascii_alphanumeric()
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || b"_-".contains(&byte))
}

fn sha_valid(value: &str) -> bool {
    value.len() == 40
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn path_key(value: &str) -> String {
    value.replace('/', "\\").to_ascii_lowercase()
}

fn parse(arguments: impl IntoIterator<Item = OsString>) -> Result<Arguments, String> {
    let values = arguments
        .into_iter()
        .map(|value| {
            value
                .into_string()
                .map_err(|_| "Non-Unicode argument".to_owned())
        })
        .collect::<Result<Vec<_>, _>>()?;
    // Match the worker's exact protocol/order, including the valueless flag.
    if values.len() != 7
        || values[0] != "--probe"
        || values[1] != "--run-id"
        || values[3] != "--suite"
        || values[4] != "all"
        || values[5] != "--result-file"
        || !run_id_valid(&values[2])
    {
        return Err(
            "Expected --probe --run-id <id> --suite all --result-file <owned path>".to_owned(),
        );
    }
    let expected = format!(r"{NODE_RUNS}\{}\probe-result.json", values[2]);
    if path_key(&values[6]) != path_key(&expected) {
        return Err("Result file must be this run's fixed probe-result.json".to_owned());
    }
    Ok(Arguments {
        run_id: values[2].clone(),
        result_file: values[6].clone(),
    })
}

fn check_plain(path: &Path, directory: bool) -> Result<(), String> {
    for (index, ancestor) in path.ancestors().enumerate() {
        let metadata = std::fs::symlink_metadata(ancestor)
            .map_err(|error| format!("Inspect {}: {error}", ancestor.display()))?;
        let link = metadata.file_type().is_symlink();
        #[cfg(windows)]
        let link = {
            use std::os::windows::fs::MetadataExt;
            link || metadata.file_attributes() & 0x400 != 0
        };
        if link
            || ((index > 0 || directory) && !metadata.is_dir())
            || (index == 0 && !directory && !metadata.is_file())
        {
            return Err(format!(
                "Expected plain {}: {}",
                if directory { "directory" } else { "file" },
                ancestor.display()
            ));
        }
    }
    Ok(())
}

fn child_exit_code(code: Option<i32>) -> i32 {
    match code {
        Some(0) => 0,
        Some(value) if value > 0 => value,
        _ => 1,
    }
}

fn execute(arguments: &Arguments, executable: &Path, git_sha: &str) -> Result<i32, String> {
    if !cfg!(windows) {
        return Err("The node driver only runs on Windows".to_owned());
    }
    if !sha_valid(git_sha) {
        return Err(
            "Build requires MYSTIA_NODE_DRIVER_GIT_SHA=<40 lowercase hexadecimal characters>"
                .to_owned(),
        );
    }
    check_plain(executable, false)?;
    let payload = executable.parent().ok_or("Driver has no payload parent")?;
    let run_root = PathBuf::from(NODE_RUNS).join(&arguments.run_id);
    let expected_payload = run_root.join("payload");
    check_plain(&expected_payload, true)?;
    if payload.canonicalize().map_err(|error| error.to_string())?
        != expected_payload
            .canonicalize()
            .map_err(|error| error.to_string())?
    {
        return Err("Driver must run directly from this run's payload directory".to_owned());
    }
    // Any existing destination, including a dangling link, forbids replay.
    match std::fs::symlink_metadata(&arguments.result_file) {
        Ok(_) => return Err("The run already has probe-result.json".to_owned()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(error.to_string()),
    }
    let adapter = payload.join(ADAPTER);
    check_plain(&adapter, false)?;
    check_plain(Path::new(POWERSHELL), false)?;
    let status = Command::new(POWERSHELL)
        .args(["-NoLogo", "-NoProfile", "-NonInteractive", "-MTA", "-File"])
        .arg(adapter)
        .args([
            "-RunId",
            &arguments.run_id,
            "-ResultFile",
            &arguments.result_file,
            "-ExpectedGitSha",
            git_sha,
        ])
        .current_dir(payload)
        .status()
        .map_err(|error| format!("Start fixed node adapter: {error}"))?;
    Ok(child_exit_code(status.code()))
}

fn main() {
    let result = parse(std::env::args_os().skip(1)).and_then(|arguments| {
        let executable = std::env::current_exe().map_err(|error| error.to_string())?;
        execute(&arguments, &executable, BUILD_SHA.unwrap_or(""))
    });
    let code = match result {
        Ok(code) => code,
        Err(error) => {
            eprintln!("Old Mod node driver refused: {error}");
            1
        }
    };
    std::process::exit(code);
}

#[cfg(test)]
mod tests {
    use super::*;

    fn valid() -> Vec<OsString> {
        [
            "--probe",
            "--run-id",
            "20261007-old_mod-01",
            "--suite",
            "all",
            "--result-file",
            r"D:\dev\mystia-node\runs\20261007-old_mod-01\probe-result.json",
        ]
        .into_iter()
        .map(OsString::from)
        .collect()
    }

    #[test]
    fn exact_worker_protocol() {
        let parsed = parse(valid()).unwrap();
        assert_eq!(parsed.run_id, "20261007-old_mod-01");
    }

    #[test]
    fn rejects_replay_ambiguity_and_arbitrary_commands() {
        for (index, value) in [
            (0, "--run"),
            (1, "--suite"),
            (2, "../other"),
            (3, "--run-id"),
            (4, "window"),
            (5, "--command"),
            (6, r"D:\dev\mystia-node\runs\other\probe-result.json"),
        ] {
            let mut arguments = valid();
            arguments[index] = value.into();
            assert!(parse(arguments).is_err());
        }
        let mut arguments = valid();
        arguments.extend([OsString::from("--run-id"), OsString::from("duplicate")]);
        assert!(parse(arguments).is_err());
        assert!(parse(valid().into_iter().take(6)).is_err());
    }

    #[test]
    fn identifiers_and_output_have_closed_alphabets() {
        for value in ["", "-bad", " a", "a b", "a\n", "a:stream", "a/b", "汉字"] {
            assert!(!run_id_valid(value));
        }
        assert!(!run_id_valid(&"a".repeat(81)));
        assert!(run_id_valid(&"a".repeat(80)));
        for path in [
            r"D:\dev\mystia-node\runs\20261007-old_mod-01\..\probe-result.json",
            r"D:\dev\mystia-node\runs\20261007-old_mod-01\probe-result.json:stream",
            r"\\server\share\probe-result.json",
        ] {
            let mut arguments = valid();
            arguments[6] = path.into();
            assert!(parse(arguments).is_err());
        }
    }

    #[test]
    fn source_identity_and_failure_exit_are_not_forged() {
        assert!(sha_valid("283bd56cd10564d64169a8ea521f9fdffe0019b4"));
        assert!(!sha_valid("283bd56"));
        assert!(!sha_valid(&"G".repeat(40)));
        assert_eq!(child_exit_code(Some(0)), 0);
        assert_eq!(child_exit_code(Some(7)), 7);
        assert_eq!(child_exit_code(Some(-1073740771)), 1);
        assert_eq!(child_exit_code(None), 1);
    }

    #[test]
    fn real_child_exit_status_is_observed() {
        let executable = std::env::current_exe().unwrap();
        for expected in [0, 7] {
            let status = Command::new(&executable)
                .args(["--exact", "tests::child_process_fixture"])
                .env("MYSTIA_NODE_DRIVER_TEST_EXIT", expected.to_string())
                .stdout(std::process::Stdio::null())
                .status()
                .unwrap();
            assert_eq!(child_exit_code(status.code()), expected);
        }
    }

    #[test]
    fn child_process_fixture() {
        // This entry point only exists in the rustc --test executable.
        if let Ok(value) = std::env::var("MYSTIA_NODE_DRIVER_TEST_EXIT") {
            std::process::exit(value.parse::<i32>().unwrap());
        }
    }

    #[test]
    fn real_plain_path_checks_reject_wrong_types_and_links() {
        let suffix = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = std::env::temp_dir().join(format!(
            "mystia-node-driver-test-{}-{suffix}",
            std::process::id()
        ));
        std::fs::create_dir(&root).unwrap();
        let file = root.join("adapter.ps1");
        std::fs::write(&file, b"fixed test fixture").unwrap();
        assert!(check_plain(&root, true).is_ok());
        assert!(check_plain(&file, false).is_ok());
        assert!(check_plain(&root, false).is_err());
        assert!(check_plain(&file, true).is_err());
        assert!(check_plain(&root.join("missing"), false).is_err());
        #[cfg(unix)]
        {
            let linked = root.join("linked-adapter.ps1");
            std::os::unix::fs::symlink(&file, &linked).unwrap();
            assert!(check_plain(&linked, false).is_err());
        }
        std::fs::remove_dir_all(&root).unwrap();
    }
}
