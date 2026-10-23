"""离线解析当前 Addressables 1.18 目录，仅操作此前提取到 temp 的 JSON。

字段布局已交叉核对当前游戏的 ContentCatalogData.CreateLocator RVA 0x29B5220
与 SerializationUtilities.ReadObjectFromByteArray RVA 0x29BEB00。这里只解码值和
索引，不加载目录中声明的程序集、资源提供器、URL，也不调用游戏解密或 Unity API。
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import struct
from workspace_paths import checked_path


def i32(data: bytes, offset: int) -> int:
    if offset < 0 or offset + 4 > len(data):
        raise ValueError(f"目录 int32 越界：{offset}/{len(data)}")
    return struct.unpack_from("<i", data, offset)[0]


def key_value(data: bytes, offset: int):
    if not 0 <= offset < len(data):
        raise ValueError("目录 key 越界")
    kind = data[offset]
    start = offset + 1
    if kind in (0, 1):
        size = i32(data, start)
        if size < 0 or start + 4 + size > len(data):
            raise ValueError("目录字符串长度越界")
        return data[start + 4 : start + 4 + size].decode("ascii" if kind == 0 else "utf-16-le")
    if kind in (2, 3, 4):
        fmt = {2: "<H", 3: "<I", 4: "<i"}[kind]
        return struct.unpack_from(fmt, data, start)[0]
    if kind in (5, 6):
        size = data[start]
        raw = data[start + 1 : start + 1 + size]
        if len(raw) != size:
            raise ValueError("目录 Hash/Type 字符串长度越界")
        # Type 只保留其文本，绝不按目录内容创建 COM 类型或加载程序集。
        return {"kind": "hash128" if kind == 5 else "type", "value": raw.decode("ascii")}
    raise ValueError(f"不支持的目录 key 类型：{kind}，拒绝猜测")


def decode(path: Path):
    catalog = json.loads(path.read_text(encoding="utf-8-sig"))
    if catalog.get("m_InternalIdPrefixes"):
        raise ValueError("本原型尚未处理非空 InternalIdPrefixes")
    bucket_data = base64.b64decode(catalog["m_BucketDataString"], validate=True)
    key_data = base64.b64decode(catalog["m_KeyDataString"], validate=True)
    entry_data = base64.b64decode(catalog["m_EntryDataString"], validate=True)
    bucket_count = i32(bucket_data, 0)
    if bucket_count != i32(key_data, 0) or not 0 <= bucket_count <= 200000:
        raise ValueError("key 与 bucket 数量不一致或超限")
    buckets, keys = [], []
    cursor = 4
    for _ in range(bucket_count):
        offset, count = i32(bucket_data, cursor), i32(bucket_data, cursor + 4)
        cursor += 8
        if count < 0 or cursor + count * 4 > len(bucket_data):
            raise ValueError("bucket 条目数量越界")
        keys.append(key_value(key_data, offset))
        buckets.append([i32(bucket_data, cursor + index * 4) for index in range(count)])
        cursor += count * 4
    if cursor != len(bucket_data):
        raise ValueError("bucket 尾部存在未解释字节")
    count = i32(entry_data, 0)
    if count < 0 or len(entry_data) != 4 + count * 28:
        raise ValueError("entry 数量与固定 28 字节布局不符")
    # 角色图像引用保存 Addressables GUID；主键可能只是显示名称，必须保留目录中
    # 同一 location 的真实别名，才能按 GUID 精确关联而不按中文名称猜图。
    aliases = [[] for _ in range(count)]
    for key, bucket in zip(keys, buckets):
        for index in bucket:
            if index < 0 or index >= count:
                raise ValueError("别名 location 索引越界")
            aliases[index].append(key)
    records = []
    for index in range(count):
        internal, provider, dependency, dep_hash, extra, primary, resource = struct.unpack_from("<7i", entry_data, 4 + index * 28)
        for value, values in ((internal, catalog["m_InternalIds"]), (provider, catalog["m_ProviderIds"]), (primary, keys), (resource, catalog["m_resourceTypes"])):
            if not 0 <= value < len(values):
                raise ValueError("entry 引用索引越界")
        if dependency < -1 or dependency >= len(buckets):
            raise ValueError("entry 依赖 key 越界")
        dependencies = [] if dependency == -1 else buckets[dependency]
        if any(value < 0 or value >= count for value in dependencies):
            raise ValueError("依赖 location 索引越界")
        records.append({
            "index": index, "key": keys[primary], "aliases": aliases[index], "internalId": catalog["m_InternalIds"][internal],
            "provider": catalog["m_ProviderIds"][provider], "type": catalog["m_resourceTypes"][resource]["m_ClassName"],
            "dependencyKey": None if dependency == -1 else keys[dependency], "dependencies": dependencies,
        })
    return records, len(keys)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("catalog", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-directory", type=Path, required=True)
    parser.add_argument("--match", default=r"(?i)(Ingredient|Beverage|Food|Cooker).*spriteatlas|NormalGuestPixel|GuestProfile|StandardCooker|ExpandableGameDataPack")
    args = parser.parse_args()
    root = args.work_directory.resolve()
    path = checked_path(args.catalog, args.work_directory)
    output = checked_path(args.output, args.work_directory, must_exist=False)
    if root not in path.parents or root not in output.parents or output.exists():
        raise ValueError("输入输出必须在探针 temp 中，且不得覆盖旧证据")
    records, key_count = decode(path)
    pattern = re.compile(args.match)
    selected = [record for record in records if pattern.search(record["internalId"]) or pattern.search(record["type"])]
    visited = set()
    pending = [record["index"] for record in selected]
    while pending:
        index = pending.pop()
        if index in visited:
            continue
        visited.add(index)
        pending.extend(records[index]["dependencies"])
    bundles = [records[index] for index in sorted(visited) if "AssetBundleProvider" in records[index]["provider"]]
    result = {
        "catalogSha256": hashlib.sha256(path.read_bytes()).hexdigest(), "keyCount": key_count,
        "locationCount": len(records), "selectionRegex": args.match, "selected": selected,
        "dependencyBundles": bundles, "locations": records,
    }
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(output), "keys": key_count, "locations": len(records), "selected": len(selected), "bundles": len(bundles)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
