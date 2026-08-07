"""Batch-import Il2CppDumper metadata into an IDA database."""

from __future__ import annotations

import csv
import json
import os
import traceback

import ida_auto
import ida_funcs
import ida_pro
import idaapi
import idautils
import idc


def required_environment_path(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"Missing required environment variable: {name}")
    path = os.path.abspath(value)
    if not os.path.isfile(path):
        raise FileNotFoundError(path)
    return path


def set_name(address: int, name: str) -> bool:
    flags = idc.SN_NOWARN | idc.SN_NOCHECK
    if idc.set_name(address, name, flags):
        return True
    return bool(idc.set_name(address, f"{name}_{address:X}", flags))


def recreate_function(start: int, end: int) -> bool:
    next_function = idc.get_next_func(start)
    if next_function != idc.BADADDR and next_function < end:
        end = next_function

    if idc.get_func_attr(start, idc.FUNCATTR_START) == start:
        ida_funcs.del_func(start)

    return bool(ida_funcs.add_func(start, end))


def main() -> int:
    script_path = required_environment_path("MYSTIA_IDA_SCRIPT_JSON")
    header_path = required_environment_path("MYSTIA_IDA_HEADER")
    stats_path = os.environ.get("MYSTIA_IDA_IMPORT_STATS")
    image_base = idaapi.get_imagebase()

    print("[mystia] Waiting for initial IDA auto-analysis...")
    ida_auto.auto_wait()

    print(f"[mystia] Parsing Il2CppDumper declarations: {header_path}")
    declaration_errors = idc.parse_decls(
        header_path,
        idc.PT_FILE | idc.PT_SIL,
    )
    if declaration_errors < 0:
        raise RuntimeError("IDA failed to parse il2cpp.h")

    print(f"[mystia] Loading Il2CppDumper mapping: {script_path}")
    with open(script_path, "r", encoding="utf-8") as source:
        data = json.load(source)

    addresses = sorted(set(int(value) for value in data.get("Addresses", [])))
    functions_added = 0
    functions_failed = 0
    for index, relative_start in enumerate(addresses):
        start = image_base + relative_start
        if index + 1 < len(addresses):
            end = image_base + addresses[index + 1]
        else:
            segment_end = idc.get_segm_end(start)
            end = segment_end if segment_end != idc.BADADDR else idc.BADADDR

        if recreate_function(start, end):
            functions_added += 1
        else:
            functions_failed += 1

        if (index + 1) % 10_000 == 0:
            print(f"[mystia] Function boundaries: {index + 1}/{len(addresses)}")

    names_applied = 0
    names_failed = 0
    signatures_applied = 0
    signatures_failed = 0
    script_methods = data.get("ScriptMethod", [])
    method_map_path = os.environ.get("MYSTIA_IDA_METHOD_MAP")
    if method_map_path:
        method_map_path = os.path.abspath(method_map_path)
        os.makedirs(os.path.dirname(method_map_path), exist_ok=True)
        with open(method_map_path, "w", encoding="utf-8", newline="") as method_map:
            writer = csv.writer(method_map)
            writer.writerow(["address", "rva", "name", "signature"])
            for method in script_methods:
                relative_address = int(method["Address"])
                writer.writerow([
                    f"0x{image_base + relative_address:X}",
                    f"0x{relative_address:X}",
                    method["Name"],
                    method.get("Signature") or "",
                ])

    methods_by_address = {int(method["Address"]): method for method in script_methods}
    unique_methods = sorted(methods_by_address.items())
    for index, (relative_address, method) in enumerate(unique_methods):
        address = image_base + relative_address
        if set_name(address, method["Name"]):
            names_applied += 1
        else:
            names_failed += 1

        signature = method.get("Signature")
        if signature:
            try:
                parsed = idc.parse_decl(signature, 0)
                if parsed and idc.apply_type(address, parsed, 1):
                    signatures_applied += 1
                else:
                    signatures_failed += 1
            except Exception:
                signatures_failed += 1

        if (index + 1) % 10_000 == 0:
            print(f"[mystia] Unique method addresses named: {index + 1}/{len(unique_methods)}")

    strings_applied = 0
    for index, literal in enumerate(data.get("ScriptString", []), start=1):
        address = image_base + int(literal["Address"])
        idc.set_name(address, f"StringLiteral_{index}", idc.SN_NOWARN)
        idc.set_cmt(address, literal["Value"], 1)
        strings_applied += 1

    metadata_applied = 0
    metadata_signatures_failed = 0
    for metadata in data.get("ScriptMetadata", []):
        address = image_base + int(metadata["Address"])
        name = metadata["Name"]
        set_name(address, name)
        idc.set_cmt(address, name, 1)
        signature = metadata.get("Signature")
        if signature:
            try:
                parsed = idc.parse_decl(signature, 0)
                if not parsed or not idc.apply_type(address, parsed, 1):
                    metadata_signatures_failed += 1
            except Exception:
                metadata_signatures_failed += 1
        metadata_applied += 1

    metadata_methods_applied = 0
    for metadata_method in data.get("ScriptMetadataMethod", []):
        address = image_base + int(metadata_method["Address"])
        method_address = image_base + int(metadata_method["MethodAddress"])
        name = metadata_method["Name"]
        set_name(address, name)
        idc.set_cmt(address, name, 1)
        idc.set_cmt(address, f"{method_address:X}", 0)
        metadata_methods_applied += 1

    print("[mystia] Waiting for post-import IDA auto-analysis...")
    ida_auto.auto_wait()

    stats = {
        "image_base": f"0x{image_base:X}",
        "declaration_errors": declaration_errors,
        "address_count": len(addresses),
        "functions_added": functions_added,
        "functions_failed": functions_failed,
        "script_method_count": len(script_methods),
        "unique_script_method_address_count": len(unique_methods),
        "names_applied": names_applied,
        "names_failed": names_failed,
        "signatures_applied": signatures_applied,
        "signatures_failed": signatures_failed,
        "strings_applied": strings_applied,
        "metadata_applied": metadata_applied,
        "metadata_signatures_failed": metadata_signatures_failed,
        "metadata_methods_applied": metadata_methods_applied,
        "final_function_count": sum(1 for _ in idautils.Functions()),
    }

    if stats_path:
        stats_path = os.path.abspath(stats_path)
        os.makedirs(os.path.dirname(stats_path), exist_ok=True)
        with open(stats_path, "w", encoding="utf-8") as output:
            json.dump(stats, output, ensure_ascii=False, indent=2)
            output.write("\n")

    print(f"[mystia] Import complete: {json.dumps(stats, ensure_ascii=False)}")
    database_path = idc.get_idb_path()
    if not idc.save_database(database_path, 0):
        raise RuntimeError(f"Failed to save IDA database: {database_path}")
    return 0


try:
    ida_pro.qexit(main())
except Exception:
    traceback.print_exc()
    ida_pro.qexit(1)
