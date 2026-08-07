"""Build deterministic manifests and a legacy/new completeness comparison."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
import platform
import re
import struct
import subprocess
import sys
import zipfile


KEY_METHODS = {
    "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel$$OnOutputSelected": "0x5872B0",
    "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel$$OnPanelClose": "0x5875E0",
    "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel$$UpdateAllVisual": "0x589FB0",
    "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel$$UpdateRecipeField": "0x58A570",
}
TOKEN_PATTERN = re.compile(r'Token = "(0x[0-9A-Fa-f]+)"')
RVA_PATTERN = re.compile(r'RVA = "(0x[0-9A-Fa-f]+)"')


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-root", required=True, type=Path)
    parser.add_argument("--analysis-root", required=True, type=Path)
    parser.add_argument("--legacy-root", required=True, type=Path)
    parser.add_argument("--toolchain-lock", required=True, type=Path)
    parser.add_argument("--steam-appmanifest", type=Path)
    return parser.parse_args()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def count_files(path: Path, pattern: str) -> int:
    return sum(1 for item in path.rglob(pattern) if item.is_file()) if path.is_dir() else 0


def count_files_excluding(path: Path, pattern: str, excluded_part: str) -> int:
    if not path.is_dir():
        return 0
    return sum(
        1
        for item in path.rglob(pattern)
        if item.is_file() and excluded_part not in item.relative_to(path).parts
    )


def collect_declaration_values(path: Path, pattern: re.Pattern[str]) -> set[str]:
    if not path.is_dir():
        return set()
    return {
        value.upper()
        for source in path.rglob("*.cs")
        for value in pattern.findall(source.read_text(encoding="utf-8-sig"))
    }


def relative_csharp_paths(path: Path) -> set[str]:
    return {
        source.relative_to(path).as_posix()
        for source in path.rglob("*.cs")
    } if path.is_dir() else set()


def metadata_version(path: Path) -> tuple[str, int]:
    with path.open("rb") as source:
        magic, version = struct.unpack("<II", source.read(8))
    return f"0x{magic:08X}", version


def unity_version(path: Path) -> str:
    match = re.search(rb"20\d\d\.\d+\.\d+[abfp]\d+", path.read_bytes())
    if not match:
        raise RuntimeError(f"Unable to detect Unity version in {path}")
    return match.group(0).decode("ascii")


def parse_build_id(path: Path | None) -> str | None:
    if not path or not path.is_file():
        return None
    match = re.search(r'"buildid"\s+"(\d+)"', path.read_text(encoding="utf-8"))
    return match.group(1) if match else None


def parse_legacy_stats(path: Path) -> dict[str, int | bool | str]:
    result: dict[str, int | bool | str] = {}
    if not path.is_file():
        return result
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if ":" not in line:
            continue
        key, value = (part.strip() for part in line.split(":", 1))
        if value.isdigit():
            result[key] = int(value)
        elif value in {"True", "False"}:
            result[key] = value == "True"
        else:
            result[key] = value
    return result


def legacy_zip_pseudocode_paths(path: Path) -> set[str]:
    if not path.is_file():
        return set()
    with zipfile.ZipFile(path) as archive:
        return {
            name
            for name in archive.namelist()
            if name.startswith("pseudocode/") and name.endswith(".c")
        }


def load_json(path: Path) -> dict[str, object]:
    return json.loads(path.read_text(encoding="utf-8")) if path.is_file() else {}


def command_output(command: list[str]) -> str:
    return subprocess.run(
        command,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    ).stdout.strip()


def load_or_infer_decompilation_report(
    report_path: Path,
    assembly_directory: Path,
    source_directory: Path,
    fallback_directory: Path | None = None,
) -> dict[str, object]:
    report = load_json(report_path)
    if report:
        return report

    assembly_names = sorted(path.name for path in assembly_directory.glob("*.dll"))
    partial_root = source_directory / "_partial"
    project_failures = sorted(
        name
        for name in assembly_names
        if (
            (partial_root / Path(name).stem).is_dir()
            or not (source_directory / Path(name).stem).is_dir()
        )
    )
    fallback_names = sorted(
        path.stem
        for path in fallback_directory.glob("*.il")
    ) if fallback_directory and fallback_directory.is_dir() else []
    failed_stems = {Path(name).stem for name in project_failures}
    fallback_failure_names = sorted(failed_stems - set(fallback_names))
    report = {
        "inferredFromGeneratedLayout": True,
        "assemblyDirectory": str(assembly_directory),
        "assemblyCount": len(assembly_names),
        "projectSuccessCount": len(assembly_names) - len(project_failures),
        "projectFailureCount": len(project_failures),
        "projectFailures": project_failures,
        "fallbackEnabled": fallback_directory is not None,
        "fallbackAssemblies": fallback_names,
        "fallbackFailureCount": len(fallback_failure_names),
        "fallbackFailures": fallback_failure_names,
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return report


def old_key_methods(function_index: Path) -> dict[str, dict[str, str]]:
    found: dict[str, dict[str, str]] = {}
    if not function_index.is_file():
        return found
    with function_index.open("r", encoding="utf-8-sig", newline="") as source:
        for row in csv.DictReader(source):
            name = row.get("name", "")
            if name in KEY_METHODS:
                address = int(row["address"], 16)
                row["rva"] = f"0x{address - 0x180000000:X}"
                found[name] = row
    return found


def new_key_methods(function_index: Path) -> dict[str, dict[str, str]]:
    found: dict[str, dict[str, str]] = {}
    if not function_index.is_file():
        return found
    with function_index.open("r", encoding="utf-8", newline="") as source:
        for row in csv.DictReader(source):
            name = row.get("name", "")
            if name in KEY_METHODS:
                found[name] = row
    return found


def legacy_failed_addresses(function_index: Path, pseudocode_paths: set[str]) -> set[str]:
    failed: set[str] = set()
    if not function_index.is_file():
        return failed
    with function_index.open("r", encoding="utf-8-sig", newline="") as source:
        for row in csv.DictReader(source):
            if row.get("pseudocode_file", "") not in pseudocode_paths:
                failed.add(row["address"].upper())
    return failed


def new_body_kinds(function_index: Path) -> dict[str, str]:
    kinds: dict[str, str] = {}
    if not function_index.is_file():
        return kinds
    with function_index.open("r", encoding="utf-8", newline="") as source:
        for row in csv.DictReader(source):
            kinds[row["address"].upper()] = row.get("body_kind", "unavailable")
    return kinds


def csv_addresses(path: Path, encoding: str = "utf-8") -> set[str]:
    if not path.is_file():
        return set()
    with path.open("r", encoding=encoding, newline="") as source:
        return {
            row["address"].upper()
            for row in csv.DictReader(source)
            if row.get("address")
        }


def main() -> int:
    arguments = parse_arguments()
    game_root = arguments.game_root.resolve()
    analysis_root = arguments.analysis_root.resolve()
    legacy_root = arguments.legacy_root.resolve()
    toolchain_lock = arguments.toolchain_lock.resolve()

    game_assembly = game_root / "GameAssembly.dll"
    game_data = game_root / "Touhou Mystia Izakaya_Data"
    metadata = game_data / "il2cpp_data" / "Metadata" / "global-metadata.dat"
    global_game_managers = game_data / "globalgamemanagers"
    scripting_assemblies_path = game_data / "ScriptingAssemblies.json"
    for required in (game_assembly, metadata, global_game_managers, scripting_assemblies_path, toolchain_lock):
        if not required.is_file():
            raise FileNotFoundError(required)

    scripting_assemblies = json.loads(scripting_assemblies_path.read_text(encoding="utf-8"))["names"]
    dummy_directory = analysis_root / "raw-metadata" / "DummyDll"
    dummy_names = sorted(path.name for path in dummy_directory.glob("*.dll"))
    manifest_names_with_dummy = sorted(set(scripting_assemblies) & set(dummy_names))
    manifest_names_without_dummy = sorted(set(scripting_assemblies) - set(dummy_names))
    dummy_names_without_manifest = sorted(set(dummy_names) - set(scripting_assemblies))
    metadata_magic, metadata_format_version = metadata_version(metadata)
    ida_stats = load_json(analysis_root / "ida" / "export" / "export_stats.json")
    ida_import_stats = load_json(analysis_root / "ida" / "import_stats.json")
    metadata_report = load_or_infer_decompilation_report(
        analysis_root / "reports" / "metadata-decompilation.json",
        dummy_directory,
        analysis_root / "managed-source" / "metadata",
    )
    interop_report = load_or_infer_decompilation_report(
        analysis_root / "reports" / "interop-decompilation.json",
        analysis_root / "interop-783" / "assemblies",
        analysis_root / "managed-source" / "interop-783",
        analysis_root / "managed-source" / "interop-783-il",
    )

    old_stats = parse_legacy_stats(legacy_root / "08_export_stats.txt")
    old_pseudocode_disk = count_files(legacy_root / "pseudocode", "*.c")
    old_zip_pseudocode_paths = legacy_zip_pseudocode_paths(legacy_root / "ida_ai.zip")
    old_pseudocode_zip = len(old_zip_pseudocode_paths)
    old_key_rows = old_key_methods(legacy_root / "01_functions_index.csv")
    new_key_rows = new_key_methods(analysis_root / "ida" / "export" / "functions.csv")
    old_failed = legacy_failed_addresses(
        legacy_root / "01_functions_index.csv",
        old_zip_pseudocode_paths,
    )
    new_bodies = new_body_kinds(analysis_root / "ida" / "export" / "functions.csv")
    old_failed_now_pseudocode = sum(new_bodies.get(address) == "pseudocode" for address in old_failed)
    old_failed_now_disassembly = sum(new_bodies.get(address) == "disassembly" for address in old_failed)
    old_failed_now_unavailable = sum(new_bodies.get(address) == "unavailable" for address in old_failed)
    old_failed_missing = sum(address not in new_bodies for address in old_failed)
    mapped_method_addresses = csv_addresses(analysis_root / "ida" / "method_map.csv")
    indexed_function_addresses = set(new_bodies)
    mapped_method_function_starts = mapped_method_addresses & indexed_function_addresses
    old_assembly_csharp = legacy_root / "Assembly-CSharp"
    new_assembly_csharp = analysis_root / "managed-source" / "metadata" / "Assembly-CSharp"
    old_tokens = collect_declaration_values(old_assembly_csharp, TOKEN_PATTERN)
    new_tokens = collect_declaration_values(new_assembly_csharp, TOKEN_PATTERN)
    old_rvas = collect_declaration_values(old_assembly_csharp, RVA_PATTERN)
    new_rvas = collect_declaration_values(new_assembly_csharp, RVA_PATTERN)
    assembly_csharp_paths_match = (
        relative_csharp_paths(old_assembly_csharp) == relative_csharp_paths(new_assembly_csharp)
    )
    input_files = {
        "gameAssembly": {
            "path": str(game_assembly),
            "size": game_assembly.stat().st_size,
            "sha256": sha256(game_assembly),
        },
        "globalMetadata": {
            "path": str(metadata),
            "size": metadata.stat().st_size,
            "sha256": sha256(metadata),
            "magic": metadata_magic,
            "version": metadata_format_version,
        },
        "globalGameManagers": {
            "path": str(global_game_managers),
            "size": global_game_managers.stat().st_size,
            "sha256": sha256(global_game_managers),
        },
        "scriptingAssemblies": {
            "path": str(scripting_assemblies_path),
            "size": scripting_assemblies_path.stat().st_size,
            "sha256": sha256(scripting_assemblies_path),
        },
    }
    if arguments.steam_appmanifest and arguments.steam_appmanifest.is_file():
        input_files["steamAppManifest"] = {
            "path": str(arguments.steam_appmanifest.resolve()),
            "size": arguments.steam_appmanifest.stat().st_size,
            "sha256": sha256(arguments.steam_appmanifest),
        }
    toolchain = load_json(toolchain_lock)
    manifest = {
        "schemaVersion": 1,
        "steamAppId": "1584090",
        "steamBuildId": parse_build_id(arguments.steam_appmanifest),
        "unityVersion": unity_version(global_game_managers),
        "hostEnvironment": {
            "dotnetSdk": command_output(["dotnet", "--version"]),
            "python": platform.python_version(),
            "pythonExecutable": sys.executable,
        },
        "inputs": input_files,
        "toolchain": toolchain.get("tools", {}),
        "assemblies": {
            "scriptingManifestCount": len(scripting_assemblies),
            "nonEmptyDummyCount": len(dummy_names),
            "scriptingManifestWithDummyCount": len(manifest_names_with_dummy),
            "dummyAssembliesOutsideScriptingManifest": dummy_names_without_manifest,
            "cpp2IlBepInExDummyCount": count_files(
                analysis_root / "cpp2il" / "bepinex-783-dummy",
                "*.dll",
            ),
            "manifestEntriesWithoutDummy": manifest_names_without_dummy,
            "interopAssemblyCount": count_files(analysis_root / "interop-783" / "assemblies", "*.dll"),
        },
        "searchableSources": {
            "metadataCSharpFileCount": count_files(analysis_root / "managed-source" / "metadata", "*.cs"),
            "interopCSharpFileCount": count_files_excluding(
                analysis_root / "managed-source" / "interop-783",
                "*.cs",
                "_partial",
            ),
            "interopIlFallbackFileCount": count_files(analysis_root / "managed-source" / "interop-783-il", "*.il"),
        },
        "idaImport": ida_import_stats,
        "idaExport": ida_stats,
        "idaMethodMapCoverage": {
            "uniqueMappedMethodAddressCount": len(mapped_method_addresses),
            "mappedAddressesAtFunctionStartCount": len(mapped_method_function_starts),
            "mappedAddressesMissingFromFunctionIndexCount": len(
                mapped_method_addresses - indexed_function_addresses
            ),
        },
        "metadataDecompilation": metadata_report,
        "interopDecompilation": interop_report,
        "legacyComparison": {
            "oldFailedFunctionCount": len(old_failed),
            "oldFailuresNowPseudocode": old_failed_now_pseudocode,
            "oldFailuresNowDisassembly": old_failed_now_disassembly,
            "oldFailuresNowUnavailable": old_failed_now_unavailable,
            "oldFailuresMissingFromNewIndex": old_failed_missing,
        },
    }

    manifest_path = analysis_root / "analysis-manifest.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    new_function_count = ida_stats.get("function_count", "pending")
    new_pseudocode = ida_stats.get("pseudocode_ok", "pending")
    new_fallback = ida_stats.get("disassembly_fallback_ok", "pending")
    new_unavailable = ida_stats.get("disassembly_fallback_failed", "pending")
    key_rows = []
    for method, expected_rva in KEY_METHODS.items():
        old_row = old_key_rows.get(method, {})
        old_rva = old_row.get("rva", "missing")
        old_body = (
            "pseudocode"
            if old_row.get("pseudocode_file", "") in old_zip_pseudocode_paths
            else "unavailable"
        )
        new_row = new_key_rows.get(method, {})
        new_rva = new_row.get("rva", "missing")
        new_body = new_row.get("body_kind", "missing")
        status = "match" if old_rva.upper() == expected_rva.upper() and new_rva.upper() == expected_rva.upper() else "review"
        key_rows.append(
            f"| `{method}` | `{old_rva}` | `{old_body}` | `{new_rva}` | `{new_body}` | {status} |"
        )

    report = f"""# 新旧 IL2CPP 分析完整性对比

## 输入身份

- Steam App / Build：`1584090` / `{manifest['steamBuildId'] or 'unknown'}`
- Unity：`{manifest['unityVersion']}`；metadata：`{metadata_magic}` / v{metadata_format_version}
- 生成环境：.NET SDK `{manifest['hostEnvironment']['dotnetSdk']}`；Python `{manifest['hostEnvironment']['python']}`
- `GameAssembly.dll` SHA-256：`{input_files['gameAssembly']['sha256']}`
- `global-metadata.dat` SHA-256：`{input_files['globalMetadata']['sha256']}`
- 旧资料没有保存输入哈希，因此关键 RVA 一致只能证明高度相符，不能替代二进制哈希同一性证明。

## 可检索内容

| 项目 | 旧分析 | 新分析 |
| --- | ---: | ---: |
| ScriptingAssemblies 清单 | 未保存完整程序集层 | {len(scripting_assemblies)} |
| ScriptingAssemblies 中有 DummyDll 的条目 | 旧资料未提供 | {len(manifest_names_with_dummy)} / {len(scripting_assemblies)} |
| DummyDll 总数（含运行时/框架程序集） | 仅提供 Assembly-CSharp 源码 | {len(dummy_names)} |
| BepInEx #783 Cpp2IL 输入程序集 | 旧资料未提供 | {manifest['assemblies']['cpp2IlBepInExDummyCount']} |
| metadata C# 项目成功 | 未保存 | {metadata_report.get('projectSuccessCount', 'unknown')} / {metadata_report.get('assemblyCount', 'unknown')} |
| Assembly-CSharp C# 文件 | {count_files(old_assembly_csharp, '*.cs')} | {count_files(new_assembly_csharp, '*.cs')} |
| Assembly-CSharp 唯一 metadata token | {len(old_tokens)} | {len(new_tokens)} |
| Assembly-CSharp 唯一 Native RVA | {len(old_rvas)} | {len(new_rvas)} |
| 全部 metadata C# 文件 | 仅 Assembly-CSharp | {manifest['searchableSources']['metadataCSharpFileCount']} |
| BepInEx #783 interop C# 项目成功 | 旧资料未提供 | {interop_report.get('projectSuccessCount', 'unknown')} / {interop_report.get('assemblyCount', 'unknown')} |
| BepInEx #783 interop C# 文件 | 旧资料未提供 | {manifest['searchableSources']['interopCSharpFileCount']} |
| interop 精确 CIL fallback | 旧资料未提供 | {manifest['searchableSources']['interopIlFallbackFileCount']} |
| IDA 函数 | {old_stats.get('function_count', 'unknown')} | {new_function_count} |
| 导出前后函数集合稳定 | 旧资料未记录 | {ida_stats.get('function_set_stable', 'pending')} |
| 唯一托管方法地址位于 IDA 函数起点 | 旧资料未保存完整映射 | {len(mapped_method_function_starts)} / {len(mapped_method_addresses)} |
| Hex-Rays 伪代码 | {old_pseudocode_zip}（ZIP） | {new_pseudocode} |
| 伪代码失败后的反汇编 | 0 | {new_fallback} |
| 无效 Unicode 文本以原始指令字节保留 | 旧资料未记录 | {ida_stats.get('disassembly_text_decode_fallback_count', 'pending')} |
| 完全无函数体 | {old_stats.get('pseudocode_failed', 'unknown')} | {new_unavailable} |
| 函数体文件计数核对 | 旧展开目录与 ZIP 不一致 | {ida_stats.get('body_file_counts_verified', 'pending')} |

旧 `pseudocode/` 展开目录只有 {old_pseudocode_disk} 个文件，而 `ida_ai.zip` 中有 {old_pseudocode_zip} 个；
缺口集中在地址尾段，属于旧展开未完成，不是旧 ZIP 或命名碰撞造成的数据丢失。新 IDA 输出按 RVA
中低 8 位（`(RVA >> 4) & 0xff`）分成 256 个目录，并以导出统计核对实际文件数。

旧索引中 {len(old_failed)} 个无函数体地址，在新索引中有 {old_failed_now_pseudocode} 个恢复为 Hex-Rays
伪代码、{old_failed_now_disassembly} 个由反汇编覆盖、{old_failed_now_unavailable} 个仍完全不可用，另有
{old_failed_missing} 个未出现在新索引。该逐地址闭环比只比较两个版本的总函数数更能反映实际可检索性。

游戏清单中 {len(scripting_assemblies)} 个程序集有 {len(manifest_names_with_dummy)} 个对应非空 DummyDll；其余
{len(manifest_names_without_dummy)} 个条目在当前 IL2CPP metadata 中没有可输出类型，不应伪造空源码工程。另有
{len(dummy_names_without_manifest)} 个运行时/框架 DummyDll 本就不在 `ScriptingAssemblies.json` 中，因此 DummyDll 总数为
{len(dummy_names)}；这两个口径不能直接相减。

旧、新 Assembly-CSharp 分别有 {count_files(old_assembly_csharp, '*.cs')} / {count_files(new_assembly_csharp, '*.cs')}
个 C# 文件；相对路径集合{'一致' if assembly_csharp_paths_match else '不一致'}，token
交集为 {len(old_tokens & new_tokens)}，新资料另有 {len(new_tokens - old_tokens)} 个、旧资料另有
{len(old_tokens - new_tokens)} 个；Native RVA 交集为 {len(old_rvas & new_rvas)}，新资料另有
{len(new_rvas - old_rvas)} 个、旧资料另有 {len(old_rvas - new_rvas)} 个。新资料明显覆盖更多声明，但因旧输入
哈希和 Steam build 未保存，差异也可能包含游戏版本变化，不能把它描述成同一二进制上的纯工具提升。

## 关键运行时交叉验证

| 方法 | 旧 RVA | 旧函数体 | 新 RVA | 新函数体 | 结果 |
| --- | --- | --- | --- | --- | --- |
{chr(10).join(key_rows)}

新 interop 同时确认了 `DEYU.Singletons.Singleton<T>.Instance`、`DataBaseDay.allNPCs`、
`RunTimeAlbum.RecordedSpecialNPCs` 的静态属性 wrapper，`NPC.possibleDestinations` 的
`Il2CppReferenceArray<Destination>`，以及 `SchedulerNode.Character.characterIdentity` 的公开字段形态。
这些形态只用于与 Assembly-CSharp/IDA 结论交叉验证，不能用 DummyDll 的空方法体推断运行时行为。

## 已排除的无效路径

- Cpp2IL `{toolchain['tools']['cpp2Il']['version']}` 的 `dll_il_recovery` 输出器实际只写
  `ldnull; throw`，不提供方法恢复；新流程不生成或发布这类误导性“恢复源码”。
- Steam 目录中安装的 BepInEx 不属于分析输入；interop 只由锁定的 Windows x64 #783 包离线生成。
- ILSpy 项目模式不能表示的复杂 interop 泛型程序集不会被静默忽略；它们改以精确 CIL 输出并在报告中列名。
"""
    (analysis_root / "comparison-report.md").write_text(report, encoding="utf-8")
    print(manifest_path)
    print(analysis_root / "comparison-report.md")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
