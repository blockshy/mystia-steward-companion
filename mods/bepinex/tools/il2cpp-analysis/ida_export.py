"""Export a collision-free, AI-searchable view of an IL2CPP IDA database."""

from __future__ import annotations

import csv
import gc
import json
import os
import re
import traceback

import ida_auto
import ida_bytes
import ida_funcs
import ida_hexrays
import ida_lines
import ida_name
import ida_nalt
import ida_pro
import ida_segment
import ida_xref
import idaapi
import idautils
import idc


SAFE_NAME = re.compile(r"[^A-Za-z0-9_.-]+")
CALL_TYPES = {ida_xref.fl_CN, ida_xref.fl_CF}
JUMP_TYPES = {ida_xref.fl_JN, ida_xref.fl_JF}


def required_output_directory() -> str:
    value = os.environ.get("MYSTIA_IDA_EXPORT_DIR")
    if not value:
        raise RuntimeError("Missing required environment variable: MYSTIA_IDA_EXPORT_DIR")
    path = os.path.abspath(value)
    os.makedirs(path, exist_ok=True)
    if any(os.scandir(path)):
        raise RuntimeError(f"IDA export directory must be empty: {path}")
    return path


def clean_name(name: str) -> str:
    cleaned = SAFE_NAME.sub("_", name).strip("._")
    return (cleaned or "sub")[:96]


def body_relative_path(kind: str, address: int, image_base: int, name: str, suffix: str) -> str:
    rva = address - image_base
    shard = f"{(rva >> 4) & 0xFF:02x}"
    filename = f"rva_{rva:08x}__{clean_name(name)}.{suffix}"
    return os.path.join(kind, shard, filename)


def write_text(root: str, relative_path: str, content: str) -> None:
    path = os.path.join(root, relative_path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as output:
        output.write(content)


def count_body_files(output_dir: str, kind: str, suffix: str) -> tuple[int, int]:
    root = os.path.join(output_dir, kind)
    if not os.path.isdir(root):
        return 0, 0
    shard_count = 0
    file_count = 0
    for shard in os.scandir(root):
        if not shard.is_dir():
            continue
        shard_count += 1
        file_count += sum(
            entry.is_file() and entry.name.endswith(suffix)
            for entry in os.scandir(shard.path)
        )
    return file_count, shard_count


def pseudocode_for(address: int) -> str:
    cfunc = ida_hexrays.decompile(address)
    if cfunc is None:
        raise RuntimeError("Hex-Rays returned no cfunc")
    lines = [ida_lines.tag_remove(line.line) for line in cfunc.get_pseudocode()]
    return "\n".join(lines) + "\n"


def disassembly_for(function: ida_funcs.func_t) -> tuple[str, int]:
    lines: list[str] = []
    text_decode_fallback_count = 0
    for chunk_start, chunk_end in idautils.Chunks(function.start_ea):
        lines.append(f"; chunk 0x{chunk_start:X}-0x{chunk_end:X}")
        for address in idautils.Heads(chunk_start, chunk_end):
            item_size = ida_bytes.get_item_size(address)
            raw = ida_bytes.get_bytes(address, item_size) or b""
            raw_hex = raw.hex(" ")
            try:
                disassembly = ida_lines.tag_remove(idc.generate_disasm_line(address, 0) or "")
            except UnicodeError:
                text_decode_fallback_count += 1
                disassembly = "; IDA instruction text unavailable (invalid Unicode); raw bytes retained"
            lines.append(f"{address:016X}  {raw_hex:<47}  {disassembly}")
    return "\n".join(lines) + "\n", text_decode_fallback_count


def export_imports(output_dir: str) -> int:
    count = 0
    with open(os.path.join(output_dir, "imports.csv"), "w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["module", "address", "name", "ordinal"])
        for module_index in range(ida_nalt.get_import_module_qty()):
            module_name = ida_nalt.get_import_module_name(module_index) or ""

            def callback(address: int, name: str | None, ordinal: int) -> bool:
                nonlocal count
                writer.writerow([module_name, f"0x{address:X}", name or "", ordinal])
                count += 1
                return True

            ida_nalt.enum_import_names(module_index, callback)
    return count


def export_entries(output_dir: str, image_base: int) -> int:
    count = 0
    with open(os.path.join(output_dir, "exports.csv"), "w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["index", "ordinal", "address", "rva", "name"])
        for index, ordinal, address, name in idautils.Entries():
            writer.writerow([index, ordinal, f"0x{address:X}", f"0x{address - image_base:X}", name])
            count += 1
    return count


def export_segments(output_dir: str) -> int:
    count = 0
    with open(os.path.join(output_dir, "segments.csv"), "w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["name", "class", "start", "end", "size", "permissions", "bitness", "type"])
        for address in idautils.Segments():
            segment = ida_segment.getseg(address)
            if segment is None:
                continue
            writer.writerow([
                ida_segment.get_segm_name(segment),
                ida_segment.get_segm_class(segment),
                f"0x{segment.start_ea:X}",
                f"0x{segment.end_ea:X}",
                segment.end_ea - segment.start_ea,
                segment.perm,
                segment.bitness,
                segment.type,
            ])
            count += 1
    return count


def export_strings(output_dir: str, image_base: int) -> int:
    strings = idautils.Strings()
    strings.setup(
        strtypes=[
            ida_nalt.STRTYPE_C,
            ida_nalt.STRTYPE_C_16,
            ida_nalt.STRTYPE_C_32,
            ida_nalt.STRTYPE_PASCAL,
            ida_nalt.STRTYPE_PASCAL_16,
            ida_nalt.STRTYPE_PASCAL_32,
            ida_nalt.STRTYPE_LEN2,
            ida_nalt.STRTYPE_LEN2_16,
            ida_nalt.STRTYPE_LEN2_32,
            ida_nalt.STRTYPE_LEN4,
            ida_nalt.STRTYPE_LEN4_16,
            ida_nalt.STRTYPE_LEN4_32,
        ],
        only_7bit=False,
        ignore_instructions=False,
    )
    count = 0
    with open(os.path.join(output_dir, "strings.csv"), "w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["address", "rva", "length", "type", "value"])
        for item in strings:
            writer.writerow([
                f"0x{item.ea:X}",
                f"0x{item.ea - image_base:X}",
                item.length,
                item.strtype,
                str(item),
            ])
            count += 1
    return count


def export_names(output_dir: str, image_base: int) -> int:
    count = 0
    with open(os.path.join(output_dir, "names.csv"), "w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["address", "rva", "name", "demangled"])
        for address, name in idautils.Names():
            demangled = ida_name.demangle_name(name, ida_name.MNG_SHORT_FORM) or ""
            writer.writerow([f"0x{address:X}", f"0x{address - image_base:X}", name, demangled])
            count += 1
    return count


def export_functions(
    output_dir: str,
    image_base: int,
    function_addresses: list[int],
) -> dict[str, int]:
    stats = {
        "function_count": len(function_addresses),
        "pseudocode_ok": 0,
        "pseudocode_failed": 0,
        "disassembly_fallback_ok": 0,
        "disassembly_fallback_failed": 0,
        "disassembly_text_decode_fallback_count": 0,
        "call_xref_count": 0,
    }

    with (
        open(os.path.join(output_dir, "functions.csv"), "w", encoding="utf-8", newline="") as function_stream,
        open(os.path.join(output_dir, "call_xrefs.csv"), "w", encoding="utf-8", newline="") as xref_stream,
        open(os.path.join(output_dir, "decompilation_failures.csv"), "w", encoding="utf-8", newline="") as failure_stream,
    ):
        function_writer = csv.writer(function_stream)
        xref_writer = csv.writer(xref_stream)
        failure_writer = csv.writer(failure_stream)
        function_writer.writerow([
            "address", "rva", "name", "demangled", "size", "chunk_count", "flags",
            "body_kind", "body_path", "decompilation_error",
        ])
        xref_writer.writerow([
            "source_address", "source_rva", "source_function", "instruction_address",
            "target_address", "target_rva", "target_function", "kind",
        ])
        failure_writer.writerow(["address", "rva", "name", "error", "fallback_path"])

        for index, address in enumerate(function_addresses, start=1):
            function = ida_funcs.get_func(address)
            if function is None:
                continue
            chunks = list(idautils.Chunks(address))
            name = ida_name.get_name(address) or f"sub_{address:X}"
            demangled = ida_name.demangle_name(name, ida_name.MNG_SHORT_FORM) or ""
            body_kind = "pseudocode"
            body_path = body_relative_path("pseudocode", address, image_base, name, "c")
            error = ""

            try:
                write_text(output_dir, body_path, pseudocode_for(address))
                stats["pseudocode_ok"] += 1
            except Exception as exception:
                stats["pseudocode_failed"] += 1
                error = f"{type(exception).__name__}: {exception}"
                body_kind = "disassembly"
                body_path = body_relative_path("disassembly", address, image_base, name, "asm")
                try:
                    disassembly, text_decode_fallback_count = disassembly_for(function)
                    write_text(output_dir, body_path, disassembly)
                    stats["disassembly_text_decode_fallback_count"] += text_decode_fallback_count
                    stats["disassembly_fallback_ok"] += 1
                except Exception as fallback_exception:
                    stats["disassembly_fallback_failed"] += 1
                    body_kind = "unavailable"
                    body_path = ""
                    error += f"; fallback {type(fallback_exception).__name__}: {fallback_exception}"
                failure_writer.writerow([
                    f"0x{address:X}", f"0x{address - image_base:X}", name, error, body_path,
                ])

            function_writer.writerow([
                f"0x{address:X}",
                f"0x{address - image_base:X}",
                name,
                demangled,
                sum(chunk_end - chunk_start for chunk_start, chunk_end in chunks),
                len(chunks),
                function.flags,
                body_kind,
                body_path,
                error,
            ])

            for instruction in idautils.FuncItems(address):
                for xref in idautils.XrefsFrom(instruction, 0):
                    kind = ""
                    if xref.type in CALL_TYPES:
                        kind = "call"
                    elif xref.type in JUMP_TYPES:
                        target_function = ida_funcs.get_func(xref.to)
                        if target_function is None or target_function.start_ea == function.start_ea:
                            continue
                        kind = "tail_jump"
                    else:
                        continue

                    target_function = ida_funcs.get_func(xref.to)
                    target_start = target_function.start_ea if target_function else xref.to
                    target_name = ida_name.get_name(target_start) or f"sub_{target_start:X}"
                    xref_writer.writerow([
                        f"0x{address:X}",
                        f"0x{address - image_base:X}",
                        name,
                        f"0x{instruction:X}",
                        f"0x{xref.to:X}",
                        f"0x{xref.to - image_base:X}",
                        target_name,
                        kind,
                    ])
                    stats["call_xref_count"] += 1

            if index % 1_000 == 0:
                ida_hexrays.clear_cached_cfuncs()
                gc.collect()
                print(
                    f"[mystia] Exported {index}/{len(function_addresses)} functions; "
                    f"pseudocode={stats['pseudocode_ok']}, fallback={stats['disassembly_fallback_ok']}"
                )

    return stats


def main() -> int:
    output_dir = required_output_directory()
    image_base = idaapi.get_imagebase()
    hexrays_available = bool(ida_hexrays.init_hexrays_plugin())
    if not hexrays_available:
        raise RuntimeError("Hex-Rays decompiler is unavailable")

    print("[mystia] Waiting for IDA auto-analysis to reach a fixed point...")
    ida_auto.auto_wait()
    print(f"[mystia] Exporting IDA database to {output_dir}")
    stats: dict[str, object] = {
        "input_file": ida_nalt.get_input_file_path(),
        "database_path": idc.get_idb_path(),
        "image_base": f"0x{image_base:X}",
        "ida_version": idaapi.get_kernel_version(),
        "hexrays_version": ida_hexrays.get_hexrays_version(),
        "hexrays_available": hexrays_available,
        "import_count": export_imports(output_dir),
        "export_count": export_entries(output_dir, image_base),
        "segment_count": export_segments(output_dir),
        "string_count": export_strings(output_dir, image_base),
        "name_count": export_names(output_dir, image_base),
    }
    pre_export_function_addresses = list(idautils.Functions())
    stats.update(export_functions(output_dir, image_base, pre_export_function_addresses))
    print("[mystia] Waiting for post-export IDA auto-analysis...")
    ida_auto.auto_wait()
    pre_export_function_set = set(pre_export_function_addresses)
    post_export_function_set = set(idautils.Functions())
    stats.update({
        "post_export_function_count": len(post_export_function_set),
        "post_export_added_function_count": len(
            post_export_function_set - pre_export_function_set
        ),
        "post_export_missing_function_count": len(
            pre_export_function_set - post_export_function_set
        ),
        "function_set_stable": post_export_function_set == pre_export_function_set,
    })
    pseudocode_file_count, pseudocode_shard_count = count_body_files(output_dir, "pseudocode", ".c")
    disassembly_file_count, disassembly_shard_count = count_body_files(output_dir, "disassembly", ".asm")
    stats.update({
        "pseudocode_file_count": pseudocode_file_count,
        "pseudocode_shard_count": pseudocode_shard_count,
        "disassembly_file_count": disassembly_file_count,
        "disassembly_shard_count": disassembly_shard_count,
        "body_file_counts_verified": (
            pseudocode_file_count == stats["pseudocode_ok"]
            and disassembly_file_count == stats["disassembly_fallback_ok"]
        ),
    })

    with open(os.path.join(output_dir, "export_stats.json"), "w", encoding="utf-8") as output:
        json.dump(stats, output, ensure_ascii=False, indent=2)
        output.write("\n")

    readme = f"""# IDA IL2CPP export

- Input: `{stats['input_file']}`
- Database: `{stats['database_path']}`
- Image base: `{stats['image_base']}`
- IDA: `{stats['ida_version']}`
- Hex-Rays: `{stats['hexrays_version']}`
- Functions: `{stats['function_count']}`
- Post-export functions: `{stats['post_export_function_count']}`
- Post-export added functions: `{stats['post_export_added_function_count']}`
- Post-export missing functions: `{stats['post_export_missing_function_count']}`
- Function set stable: `{stats['function_set_stable']}`
- Pseudocode: `{stats['pseudocode_ok']}`
- Disassembly fallbacks: `{stats['disassembly_fallback_ok']}`
- Instructions retaining raw bytes after invalid-Unicode text: `{stats['disassembly_text_decode_fallback_count']}`
- Unavailable bodies: `{stats['disassembly_fallback_failed']}`
- Body file counts verified: `{stats['body_file_counts_verified']}`

Start with `functions.csv`. Every body filename contains its RVA, so duplicate IL2CPP
names cannot overwrite one another. `call_xrefs.csv` contains cross-function calls and
tail jumps; intra-function control-flow edges are intentionally omitted. Function size
and chunk count cover every IDA chunk, and disassembly fallbacks include chunk markers.
"""
    write_text(output_dir, "README.md", readme)
    print(f"[mystia] Export complete: {json.dumps(stats, ensure_ascii=False)}")
    return 0 if (
        stats["function_set_stable"]
        and stats["disassembly_fallback_failed"] == 0
        and stats["body_file_counts_verified"]
    ) else 1


try:
    ida_pro.qexit(main())
except Exception:
    traceback.print_exc()
    ida_pro.qexit(1)
