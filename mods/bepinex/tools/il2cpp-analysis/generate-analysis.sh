#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
lock_file="$script_dir/toolchain.lock.json"

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "Usage: $0 <game-root> <empty-output-root> [ida-install-directory]" >&2
  exit 2
fi

game_root=$(realpath "$1")
output_root=$(realpath -m "$2")

lock_value() {
  python3 - "$lock_file" "$1" <<'PY'
import json
import sys

value = json.load(open(sys.argv[1], encoding="utf-8"))
for part in sys.argv[2].split("."):
    value = value[part]
print(value)
PY
}

ida_install=${3:-$(lock_value tools.ida.defaultInstallDirectory)}
ida_install=$(realpath "$ida_install")
ida_batch="$ida_install/idat"
game_assembly="$game_root/GameAssembly.dll"
game_data="$game_root/Touhou Mystia Izakaya_Data"
global_metadata="$game_data/il2cpp_data/Metadata/global-metadata.dat"
global_game_managers="$game_data/globalgamemanagers"
scripting_assemblies="$game_data/ScriptingAssemblies.json"

for command_name in curl dotnet file python3 sha256sum strings unzip; do
  command -v "$command_name" >/dev/null || {
    echo "Required command is unavailable: $command_name" >&2
    exit 1
  }
done

dotnet_version=$(dotnet --version)
if [[ "$dotnet_version" != 10.* ]]; then
  echo "The IL2CPP analysis generator requires a .NET 10 SDK; found $dotnet_version." >&2
  exit 1
fi
if ! python3 -c 'import sys; raise SystemExit(sys.version_info < (3, 10))'; then
  echo "The IL2CPP analysis generator requires Python 3.10 or newer." >&2
  exit 1
fi

for required_file in "$game_assembly" "$global_metadata" "$global_game_managers" "$scripting_assemblies" "$ida_batch"; do
  [[ -f "$required_file" ]] || {
    echo "Required file does not exist: $required_file" >&2
    exit 1
  }
done

file "$game_assembly" | grep -Fq "PE32+ executable" || {
  echo "The main GameAssembly.dll is not a Windows x64 PE file: $game_assembly" >&2
  exit 1
}

if [[ -d "$output_root" ]] && [[ -n $(find "$output_root" -mindepth 1 -maxdepth 1 -print -quit) ]]; then
  echo "Output root must be empty: $output_root" >&2
  exit 1
fi

mkdir -p \
  "$output_root/inputs" \
  "$output_root/toolchain/downloads" \
  "$output_root/logs" \
  "$output_root/reports"
cp "$scripting_assemblies" "$output_root/inputs/ScriptingAssemblies.json"
steam_appmanifest=$(realpath -m "$game_root/../../appmanifest_1584090.acf")
if [[ -f "$steam_appmanifest" ]]; then
  cp "$steam_appmanifest" "$output_root/inputs/appmanifest_1584090.acf"
fi

verify_sha256() {
  local path=$1
  local expected=$2
  local actual
  actual=$(sha256sum "$path" | cut -d' ' -f1)
  [[ "$actual" == "$expected" ]] || {
    echo "SHA-256 mismatch for $path: expected $expected, got $actual" >&2
    exit 1
  }
}

download_verified() {
  local url=$1
  local destination=$2
  local expected_sha256=$3
  curl --fail --location --show-error --output "$destination.partial" "$url"
  verify_sha256 "$destination.partial" "$expected_sha256"
  mv "$destination.partial" "$destination"
}

il2cppdumper_version=$(lock_value tools.il2cppDumper.version)
il2cppdumper_asset=$(lock_value tools.il2cppDumper.asset)
il2cppdumper_archive="$output_root/toolchain/downloads/$il2cppdumper_asset"
download_verified \
  "$(lock_value tools.il2cppDumper.url)" \
  "$il2cppdumper_archive" \
  "$(lock_value tools.il2cppDumper.sha256)"
il2cppdumper_dir="$output_root/toolchain/il2cppdumper-$il2cppdumper_version"
mkdir "$il2cppdumper_dir"
unzip -q "$il2cppdumper_archive" -d "$il2cppdumper_dir"
cp "$script_dir/il2cppdumper-config.json" "$il2cppdumper_dir/config.json"

cpp2il_asset=$(lock_value tools.cpp2Il.asset)
cpp2il_download="$output_root/toolchain/downloads/$cpp2il_asset"
download_verified \
  "$(lock_value tools.cpp2Il.url)" \
  "$cpp2il_download" \
  "$(lock_value tools.cpp2Il.sha256)"
cpp2il_dir="$output_root/toolchain/cpp2il-2022.1.0-pre-release.21"
mkdir "$cpp2il_dir"
cp "$cpp2il_download" "$cpp2il_dir/Cpp2IL"
chmod 0755 "$cpp2il_dir/Cpp2IL"

bepinex_asset=$(lock_value tools.bepInEx.asset)
bepinex_archive="$output_root/toolchain/downloads/$bepinex_asset"
download_verified \
  "$(lock_value tools.bepInEx.url)" \
  "$bepinex_archive" \
  "$(lock_value tools.bepInEx.sha256)"
bepinex_dir="$output_root/toolchain/bepinex-win-783"
mkdir "$bepinex_dir"
unzip -q "$bepinex_archive" -d "$bepinex_dir"

ilspy_version=$(lock_value tools.ilspyCmd.version)
ilspy_dir="$output_root/toolchain/ilspycmd-$ilspy_version"
mkdir "$ilspy_dir"
dotnet tool install --tool-path "$ilspy_dir" ilspycmd --version "$ilspy_version"
ilspy_nupkg=$(find "$ilspy_dir/.store/ilspycmd/$ilspy_version" -type f -name "ilspycmd.$ilspy_version.nupkg" -print -quit)
[[ -n "$ilspy_nupkg" ]] || {
  echo "Unable to locate the installed ilspycmd package." >&2
  exit 1
}
verify_sha256 "$ilspy_nupkg" "$(lock_value tools.ilspyCmd.nupkgSha256)"
ilspy="$ilspy_dir/ilspycmd"

unity_version=$(python3 - "$global_game_managers" <<'PY'
import re
import sys

data = open(sys.argv[1], "rb").read()
match = re.search(rb"20\d\d\.\d+\.\d+[abfp]\d+", data)
if not match:
    raise SystemExit("Unable to detect the Unity version")
print(match.group(0).decode("ascii"))
PY
)
unity_base_version=${unity_version%%[abfp]*}
locked_unity_version=$(lock_value tools.unityBaseLibraries.unityVersion)
[[ "$unity_base_version" == "$locked_unity_version" ]] || {
  echo "Unity version $unity_version does not match the locked base libraries $locked_unity_version." >&2
  exit 1
}

raw_metadata="$output_root/raw-metadata"
mkdir "$raw_metadata"
(
  cd "$il2cppdumper_dir"
  env DOTNET_ROLL_FORWARD=Major dotnet "$il2cppdumper_dir/Il2CppDumper.dll" \
    "$game_assembly" \
    "$global_metadata" \
    "$raw_metadata"
) 2>&1 | tee "$output_root/logs/il2cppdumper.log"

cpp2il_dummy="$output_root/cpp2il/bepinex-783-dummy"
mkdir -p "$cpp2il_dummy"
(
  cd "$cpp2il_dir"
  NO_COLOR=1 "$cpp2il_dir/Cpp2IL" \
    --game-path "$game_root" \
    --exe-name "Touhou Mystia Izakaya.exe" \
    --force-binary-path "$game_assembly" \
    --force-metadata-path "$global_metadata" \
    --force-unity-version "$unity_version" \
    --use-processor attributeinjector \
    --output-as dll_default \
    --output-to "$cpp2il_dummy"
) 2>&1 | tee "$output_root/logs/cpp2il-bepinex-783-dummy.log"

python3 "$script_dir/decompile_assemblies.py" \
  --ilspy "$ilspy" \
  --assemblies "$raw_metadata/DummyDll" \
  --output "$output_root/managed-source/metadata" \
  --logs "$output_root/logs/ilspy-metadata" \
  --report "$output_root/reports/metadata-decompilation.json"

unity_libs="$output_root/interop-783/unity-libs"
mkdir -p "$unity_libs"
unity_archive="$unity_libs/$(lock_value tools.unityBaseLibraries.asset)"
download_verified \
  "$(lock_value tools.unityBaseLibraries.url)" \
  "$unity_archive" \
  "$(lock_value tools.unityBaseLibraries.sha256)"
unzip -q "$unity_archive" -d "$unity_libs"

interop_output="$output_root/interop-783/assemblies"
mkdir "$interop_output"
dotnet run \
  --project "$script_dir/InteropGenerator/InteropGenerator.csproj" \
  -c Release \
  -p:BepInExCorePath="$bepinex_dir/BepInEx/core" \
  -p:BepInExDotnetPath="$bepinex_dir/dotnet" \
  -- \
  "$game_assembly" \
  "$global_metadata" \
  "$unity_version" \
  "$unity_libs" \
  "$interop_output" \
  2>&1 | tee "$output_root/logs/interop-generator-783.log"

python3 "$script_dir/decompile_assemblies.py" \
  --ilspy "$ilspy" \
  --assemblies "$interop_output" \
  --output "$output_root/managed-source/interop-783" \
  --fallback-il-output "$output_root/managed-source/interop-783-il" \
  --logs "$output_root/logs/ilspy-interop-783" \
  --report "$output_root/reports/interop-decompilation.json"

ida_database_dir="$output_root/ida/database"
ida_export_dir="$output_root/ida/export"
mkdir -p "$ida_database_dir" "$ida_export_dir"
ida_database="$ida_database_dir/GameAssembly.i64"
env \
  TVHEADLESS=1 \
  TERM=xterm \
  MYSTIA_IDA_SCRIPT_JSON="$raw_metadata/script.json" \
  MYSTIA_IDA_HEADER="$raw_metadata/il2cpp.h" \
  MYSTIA_IDA_IMPORT_STATS="$output_root/ida/import_stats.json" \
  MYSTIA_IDA_METHOD_MAP="$output_root/ida/method_map.csv" \
  "$ida_batch" \
  -A \
  -c \
  -Opdb:off \
  -L"$output_root/logs/ida-import.log" \
  -o"$ida_database" \
  -S"$script_dir/ida_import.py" \
  "$game_assembly"

set +e
env \
  TVHEADLESS=1 \
  TERM=xterm \
  MYSTIA_IDA_EXPORT_DIR="$ida_export_dir" \
  "$ida_batch" \
  -A \
  -Opdb:off \
  -L"$output_root/logs/ida-export.log" \
  -S"$script_dir/ida_export.py" \
  "$ida_database"
ida_export_status=$?
set -e

report_arguments=(
  --game-root "$game_root"
  --analysis-root "$output_root"
  --legacy-root "$output_root/../backup/legacy-analysis-20260608"
  --toolchain-lock "$lock_file"
)
if [[ -f "$steam_appmanifest" ]]; then
  report_arguments+=(--steam-appmanifest "$steam_appmanifest")
fi
python3 "$script_dir/build_analysis_report.py" "${report_arguments[@]}"

if [[ $ida_export_status -ne 0 ]]; then
  echo "IDA export was incomplete; inspect the export statistics and log in $output_root." >&2
  exit "$ida_export_status"
fi

echo "Analysis complete: $output_root"
