"""按序列化 PPtr 与 Addressables GUID 解析客人头像绑定，不按显示名称推断身份。"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
from workspace_paths import checked_path


class AmbiguousGuestBinding(RuntimeError):
    """同一角色身份出现多份相互矛盾的引用时，整个生成过程必须失败。"""


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, action="append", required=True)
    parser.add_argument("--legacy-external-table", type=Path)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-directory", type=Path, required=True)
    args = parser.parse_args()
    args.output = checked_path(args.output, args.work_directory, must_exist=False)
    if args.output.exists():
        raise ValueError("输出必须是新文件")
    objects, external_tables = {}, {}
    for report_path in args.report:
        report = read(checked_path(report_path, args.work_directory))
        for entry in report["objects"]:
            objects[(entry["file"], str(entry["pathId"]))] = entry
        for entry in report.get("serializedFiles", []):
            external_tables[entry["file"]] = {external["fileId"]: external["file"] for external in entry["externals"]}
    if args.legacy_external_table:
        for entry in read(checked_path(args.legacy_external_table, args.work_directory)):
            external_tables[entry["file"]] = {external["fileId"]: external["name"] for external in entry["externals"]}

    def resolve(owner, pointer):
        file_id, path_id = pointer["m_FileID"], pointer["m_PathID"]
        if type(file_id) is not int or type(path_id) is not int or not path_id:
            raise ValueError("未知或空 PPtr")
        target_file = owner["file"] if file_id == 0 else external_tables[owner["file"]][file_id]
        return objects[(target_file, str(path_id))]

    def tree(entry):
        if entry["type"] != "MonoBehaviour":
            raise ValueError("角色引用没有指向序列化 Profile")
        return read(checked_path(Path(entry["typeTreePath"]), args.work_directory))

    locations = read(checked_path(args.catalog, args.work_directory))["locations"]
    bindings, missing = {}, []
    for profile in objects.values():
        if profile["type"] != "MonoBehaviour" or re.fullmatch(r"(?:DLC[1-5]_?|DLCMusic_)?SpecialNPCVisualProfile", profile.get("name") or "") is None:
            continue
        for guest in tree(profile)["characterPortrayals"]:
            guest_id = guest["id"]
            if type(guest_id) is not int or guest_id < 0:
                continue
            try:
                portrayal_set = resolve(profile, guest["characterPortrayal"])
                portrayal = resolve(portrayal_set, tree(portrayal_set)["defaultPortrayal"])
                data = tree(portrayal)
                index = data["faceInNoteBook"]
                references = data["m_VisualAssetAtlasReference"]
                if type(index) is not int or index < 0 or index >= len(references):
                    raise ValueError("笔记头像索引越界，不采用默认图冒充")
                reference = references[index]
                guid = reference["m_AssetGUID"]
                matches = [location for location in locations if location["type"] == "UnityEngine.U2D.SpriteAtlas" and guid in location["aliases"]]
                if len(matches) != 1 or not reference["m_SubObjectName"]:
                    raise ValueError("头像 GUID 无唯一图集或子 Sprite 为空")
                key = f"rare-guest:{guest_id}"
                binding = {"kind": "rare-guest", "id": guest_id, "atlasContainer": matches[0]["internalId"], "spriteName": reference["m_SubObjectName"], "proof": {"profile": profile["name"], "profileFile": profile["file"], "profilePathId": profile["pathId"], "portrayalSetFile": portrayal_set["file"], "portrayalSetPathId": portrayal_set["pathId"], "portrayalFile": portrayal["file"], "portrayalPathId": portrayal["pathId"], "faceInNoteBook": index, "atlasGuid": guid, "atlasLocation": matches[0]["index"], "atlasDependencies": matches[0]["dependencies"]}}
                if key in bindings and bindings[key] != binding:
                    # 不进入下方“缺失引用”降级分支，更不能保留先遇到的头像。
                    raise AmbiguousGuestBinding("同一角色 ID 的默认头像绑定冲突")
                bindings[key] = binding
            except (KeyError, ValueError, IndexError) as error:
                missing.append({"kind": "rare-guest", "id": guest_id, "reason": str(error)})
    result = {"version": 1, "bindings": list(bindings.values()), "missing": missing, "normalGroupStatus": "尚未生成；身体、眼睛及饰品须按真实引用与坐标合成，不能将无眼身体图冒充完整客群代表"}
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(args.output), "rareGuestBindings": len(bindings), "missing": missing}, ensure_ascii=False))


if __name__ == "__main__":
    main()
