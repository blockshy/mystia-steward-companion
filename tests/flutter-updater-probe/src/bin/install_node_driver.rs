//! Closed adapter for the existing interactive node. It cannot select arbitrary
//! commands, scripts or destinations, and never turns process exit0 into PASS.
use std::path::{Path, PathBuf};
use std::process::Command;

const RUNS: &str = r"D:\dev\mystia-node\runs";
const POWERSHELL: &str = r"D:\dev\mystia-node\tools\powershell-7.6.4\pwsh.exe";
const SCRIPT: &str = "Node-Install-Fixture.ps1";
const GIT_SHA: &str = env!("MYSTIA_UPDATER_PROBE_EMBEDDED_GIT_SHA");

fn path_key(path: &str) -> String {
    path.replace('/', "\\").to_ascii_lowercase()
}
fn parse(values: &[String]) -> Result<(&str, &str), String> {
    if values.len() != 7
        || values[0] != "--probe"
        || values[1] != "--run-id"
        || values[3] != "--suite"
        || values[4] != "all"
        || values[5] != "--result-file"
    {
        return Err("Expected the exact fixed node argument sequence.".into());
    }
    let id = &values[2];
    if id.is_empty()
        || id.len() > 80
        || !id.as_bytes()[0].is_ascii_alphanumeric()
        || !id
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || b"_-".contains(&c))
    {
        return Err("Invalid node run identity.".into());
    }
    if path_key(&values[6]) != path_key(&format!(r"{RUNS}\{id}\probe-result.json")) {
        return Err("Result must belong to this exact node run.".into());
    }
    Ok((id, &values[6]))
}
fn plain(path: &Path, directory: bool) -> Result<(), String> {
    for (index, parent) in path.ancestors().enumerate() {
        let metadata = std::fs::symlink_metadata(parent).map_err(|e| e.to_string())?;
        let linked = metadata.file_type().is_symlink();
        #[cfg(windows)]
        let linked = {
            use std::os::windows::fs::MetadataExt;
            linked || metadata.file_attributes() & 0x400 != 0
        };
        if linked
            || ((index > 0 || directory) && !metadata.is_dir())
            || (index == 0 && !directory && !metadata.is_file())
        {
            return Err("Node inputs must be plain owned paths.".into());
        }
    }
    Ok(())
}
fn execute() -> Result<i32, String> {
    if !cfg!(windows) {
        return Err("Node installation adapter requires Windows.".into());
    }
    if GIT_SHA.len() != 40
        || !GIT_SHA
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
    {
        return Err("Node installation adapter has no compiled commit.".into());
    }
    let args: Vec<_> = std::env::args().skip(1).collect();
    let (id, result) = parse(&args)?;
    let payload = PathBuf::from(RUNS).join(id).join("payload");
    plain(&payload, true)?;
    let executable = std::env::current_exe().map_err(|e| e.to_string())?;
    plain(&executable, false)?;
    if executable
        .parent()
        .ok_or("Driver parent absent")?
        .canonicalize()
        .map_err(|e| e.to_string())?
        != payload.canonicalize().map_err(|e| e.to_string())?
    {
        return Err("Driver must reside in this exact node payload.".into());
    }
    match std::fs::symlink_metadata(result) {
        Ok(_) => return Err("Node result already exists; replay refused.".into()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
        Err(e) => return Err(e.to_string()),
    }
    let script = payload.join(SCRIPT);
    plain(&script, false)?;
    plain(Path::new(POWERSHELL), false)?;
    let status = Command::new(POWERSHELL)
        .args(["-NoLogo", "-NoProfile", "-NonInteractive", "-File"])
        .arg(script)
        .args([
            "-RunId",
            id,
            "-ResultFile",
            result,
            "-ExpectedGitSha",
            GIT_SHA,
        ])
        .current_dir(payload)
        .status()
        .map_err(|e| e.to_string())?;
    // The fixed PowerShell adapter validates the actual native + full manifest
    // report. This exit code only propagates that adapter's success/failure.
    Ok(match status.code() {
        Some(0) => 0,
        Some(code) if code > 0 => code,
        _ => 1,
    })
}
fn main() {
    let code = match execute() {
        Ok(code) => code,
        Err(error) => {
            eprintln!("Installation node refused: {error}");
            1
        }
    };
    std::process::exit(code);
}

#[cfg(test)]
mod tests {
    use super::*;
    fn valid() -> Vec<String> {
        [
            "--probe",
            "--run-id",
            "updater-fixture-01",
            "--suite",
            "all",
            "--result-file",
            r"D:\dev\mystia-node\runs\updater-fixture-01\probe-result.json",
        ]
        .map(str::to_owned)
        .to_vec()
    }
    #[test]
    fn accepts_only_fixed_node_shape() {
        assert_eq!(parse(&valid()).unwrap().0, "updater-fixture-01");
    }
    #[test]
    fn rejects_arbitrary_suite_arguments_traversal_and_foreign_result() {
        for (index, value) in [
            (0, "--execute"),
            (2, "../other"),
            (4, "focus"),
            (5, "--command"),
            (6, r"D:\dev\mystia-node\runs\other\probe-result.json"),
        ] {
            let mut args = valid();
            args[index] = value.into();
            assert!(parse(&args).is_err());
        }
        let mut duplicate = valid();
        duplicate.push("--probe".into());
        assert!(parse(&duplicate).is_err());
    }
}
