"""Decompile a directory of managed assemblies with a pinned ilspycmd."""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ilspy", required=True, type=Path)
    parser.add_argument("--assemblies", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--logs", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--fallback-il-output", type=Path)
    parser.add_argument("--workers", type=int, default=4)
    return parser.parse_args()


def require_empty_directory(path: Path) -> None:
    if path.exists() and any(path.iterdir()):
        raise RuntimeError(f"Directory must be empty: {path}")
    path.mkdir(parents=True, exist_ok=True)


def run_process(command: list[str], log_path: Path) -> int:
    with log_path.open("w", encoding="utf-8") as log:
        completed = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=False)
    return completed.returncode


def main() -> int:
    arguments = parse_arguments()
    ilspy = arguments.ilspy.resolve()
    assemblies = arguments.assemblies.resolve()
    output = arguments.output.resolve()
    logs = arguments.logs.resolve()
    report_path = arguments.report.resolve()
    fallback_output = arguments.fallback_il_output.resolve() if arguments.fallback_il_output else None

    if not ilspy.is_file():
        raise FileNotFoundError(ilspy)
    if not assemblies.is_dir():
        raise NotADirectoryError(assemblies)
    if arguments.workers < 1:
        raise ValueError("--workers must be positive")

    require_empty_directory(output)
    require_empty_directory(logs)
    if fallback_output:
        require_empty_directory(fallback_output)

    assembly_paths = sorted(assemblies.glob("*.dll"), key=lambda path: path.name.casefold())
    if not assembly_paths:
        raise RuntimeError(f"No DLL files found in {assemblies}")

    def decompile_project(assembly_path: Path) -> dict[str, object]:
        name = assembly_path.stem
        destination = output / name
        destination.mkdir()
        log_path = logs / f"{name}.log"
        command = [
            str(ilspy),
            "--disable-updatecheck",
            "--nested-directories",
            "-p",
            "-r",
            str(assemblies),
            "-o",
            str(destination),
            str(assembly_path),
        ]
        return_code = run_process(command, log_path)
        return {
            "assembly": assembly_path.name,
            "projectReturnCode": return_code,
            "projectOutput": str(destination),
            "projectLog": str(log_path),
        }

    results: list[dict[str, object]] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=arguments.workers) as executor:
        futures = {executor.submit(decompile_project, path): path for path in assembly_paths}
        for future in concurrent.futures.as_completed(futures):
            result = future.result()
            results.append(result)
            status = "OK" if result["projectReturnCode"] == 0 else "PROJECT_FAIL"
            print(f"{status}\t{result['assembly']}", flush=True)

    project_failures = [result for result in results if result["projectReturnCode"] != 0]
    fallback_failures: list[dict[str, object]] = []
    if project_failures and fallback_output:
        partial_root = output / "_partial"
        partial_root.mkdir()
        for result in sorted(project_failures, key=lambda item: str(item["assembly"]).casefold()):
            assembly_name = str(result["assembly"])
            name = Path(assembly_name).stem
            project_output = Path(str(result["projectOutput"]))
            partial_output = partial_root / name
            if project_output.exists():
                shutil.move(str(project_output), partial_output)
            command = [
                str(ilspy),
                "--disable-updatecheck",
                "--ilcode",
                "-r",
                str(assemblies),
                "-o",
                str(fallback_output),
                str(assemblies / assembly_name),
            ]
            log_path = logs / f"{name}.il.log"
            return_code = run_process(command, log_path)
            result["partialProjectOutput"] = str(partial_output)
            result["fallbackReturnCode"] = return_code
            result["fallbackLog"] = str(log_path)
            result["fallbackOutput"] = str(fallback_output / f"{name}.il")
            if return_code != 0 or not Path(str(result["fallbackOutput"])).is_file():
                fallback_failures.append(result)
                print(f"IL_FAIL\t{assembly_name}", flush=True)
            else:
                print(f"IL_OK\t{assembly_name}", flush=True)

    results.sort(key=lambda item: str(item["assembly"]).casefold())
    report = {
        "ilspy": str(ilspy),
        "assemblyDirectory": str(assemblies),
        "assemblyCount": len(assembly_paths),
        "projectSuccessCount": len(assembly_paths) - len(project_failures),
        "projectFailureCount": len(project_failures),
        "fallbackEnabled": fallback_output is not None,
        "fallbackFailureCount": len(fallback_failures),
        "results": results,
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    with report_path.open("w", encoding="utf-8") as report_file:
        json.dump(report, report_file, ensure_ascii=False, indent=2)
        report_file.write("\n")

    if project_failures and not fallback_output:
        return 1
    return 1 if fallback_failures else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:
        print(f"decompile_assemblies.py: {exception}", file=sys.stderr)
        raise
