"""离线图片管线的关键拒绝分支回归；合成 JSON，不依赖游戏或 Flutter SDK。"""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import resolve_guest_bindings
from workspace_paths import checked_path


class GuestIdentityTests(unittest.TestCase):
    def setUp(self):
        # 临时目录固定在仓库 temp 中；tearDown 只清理本测试独占的新目录。
        parent = Path(__file__).resolve().parents[2] / "temp"
        parent.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="game-media-test-", dir=parent)
        self.work = Path(self.temporary.name).resolve()
        self.assertIn(parent.resolve(), self.work.parents)

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, name, value):
        path = self.work / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def prepare(self, second=False, missing=False):
        objects = []

        def add(path_id, name, payload):
            path = self.write(f"tree-{path_id}.json", payload)
            objects.append({"type": "MonoBehaviour", "file": "CAB-test", "pathId": str(path_id), "name": name, "typeTreePath": str(path)})

        guests = [{"id": 1, "characterPortrayal": {"m_FileID": 0, "m_PathID": 99 if missing else 2}}]
        if second:
            guests.append({"id": 1, "characterPortrayal": {"m_FileID": 0, "m_PathID": 4}})
        add(1, "SpecialNPCVisualProfile", {"characterPortrayals": guests})
        add(2, "set-one", {"defaultPortrayal": {"m_FileID": 0, "m_PathID": 3}})
        add(3, "portrait-one", {"faceInNoteBook": 0, "m_VisualAssetAtlasReference": [{"m_AssetGUID": "guid-one", "m_SubObjectName": "真实脸一"}]})
        add(4, "set-two", {"defaultPortrayal": {"m_FileID": 0, "m_PathID": 5}})
        add(5, "portrait-two", {"faceInNoteBook": 0, "m_VisualAssetAtlasReference": [{"m_AssetGUID": "guid-two", "m_SubObjectName": "真实脸二"}]})
        report = self.write("report.json", {"objects": objects, "serializedFiles": []})
        catalog = self.write("catalog.json", {"locations": [{"type": "UnityEngine.U2D.SpriteAtlas", "aliases": ["guid-one"], "internalId": "Assets/one.spriteatlas", "index": 1, "dependencies": [10]}, {"type": "UnityEngine.U2D.SpriteAtlas", "aliases": ["guid-two"], "internalId": "Assets/two.spriteatlas", "index": 2, "dependencies": [11]}]})
        self.output = self.work / "bindings.json"
        return ["resolve_guest_bindings.py", "--report", str(report), "--catalog", str(catalog), "--work-directory", str(self.work), "--output", str(self.output)]

    def test_exact_guid_keeps_identity_chain(self):
        with patch.object(sys, "argv", self.prepare()):
            resolve_guest_bindings.main()
        result = json.loads(self.output.read_text(encoding="utf-8"))
        self.assertEqual(result["bindings"][0]["atlasContainer"], "Assets/one.spriteatlas")
        self.assertEqual(result["bindings"][0]["proof"]["portrayalPathId"], "3")
        self.assertEqual(result["missing"], [])

    def test_conflicting_same_guest_never_keeps_first_picture(self):
        with patch.object(sys, "argv", self.prepare(second=True)):
            with self.assertRaises(resolve_guest_bindings.AmbiguousGuestBinding):
                resolve_guest_bindings.main()
        self.assertFalse(self.output.exists(), "歧义必须停止输出，不能保留先遇到的头像")

    def test_missing_pointer_is_reported_without_guessing(self):
        with patch.object(sys, "argv", self.prepare(missing=True)):
            resolve_guest_bindings.main()
        result = json.loads(self.output.read_text(encoding="utf-8"))
        self.assertEqual(result["bindings"], [])
        self.assertEqual(result["missing"][0]["id"], 1)

    def test_work_boundary_rejects_external_input_and_output(self):
        nested = self.work / "scope"
        nested.mkdir()
        external = self.write("outside.json", {})
        with self.assertRaises(ValueError):
            checked_path(external, nested)
        with self.assertRaises(ValueError):
            checked_path(self.work / "outside-new.json", nested, must_exist=False)


if __name__ == "__main__":
    unittest.main()
