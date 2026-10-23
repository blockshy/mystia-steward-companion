"""把离线精确 Sprite 导出转换为 Flutter 透明缩略图与静态身份索引。

本脚本只处理导出器已经生成的 PNG / TypeTree JSON；不读取游戏进程、不执行游戏
方法，也不推导推荐方案。图像保持透明且最长边不超过 128 像素，像素小图不放大。
输出必须是新目录，验证全部输入后才生成索引；调用方检查后再放入客户端资产目录。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path

from PIL import Image
from workspace_paths import checked_path


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require_integer(value, label: str, minimum: int = 0) -> int:
    # Python 的 bool 是 int 子类，必须单独拒绝以免把损坏配置解释为实体 ID。
    if type(value) is not int or value < minimum:
        raise ValueError(f"{label} 必须为不小于 {minimum} 的整数")
    return value


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export", type=Path, action="append", required=True)
    parser.add_argument("--profile-report", type=Path, action="append", required=True)
    parser.add_argument("--guest-bindings", type=Path)
    parser.add_argument("--catalog-report", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-directory", type=Path, required=True)
    args = parser.parse_args()
    output = checked_path(args.output, args.work_directory, must_exist=False)
    if output.exists():
        raise ValueError("输出目录必须不存在；不覆盖既有导出证据")

    items: dict[str, tuple[Path, dict]] = {}
    sources: dict[str, dict] = {}
    tool_evidence: list[dict] = []
    missing_guests: list[dict] = []
    for directory in args.export:
        directory = checked_path(directory, args.work_directory, must_exist=False)
        checked_path(directory / "manifest.json", args.work_directory)
        checked_path(directory / "source-evidence.json", args.work_directory)
        manifest = read_json(directory / "manifest.json")
        evidence = read_json(directory / "source-evidence.json")
        missing_guests.extend(evidence.get("missingGuests", []))
        if manifest.get("version") != 1:
            raise ValueError("不支持的图片清单版本")
        rows = {row["key"]: row for row in evidence["images"]}
        if len(rows) != len(evidence["images"]) or rows.keys() != manifest["items"].keys():
            raise ValueError("图像来源证据与索引不一一对应")
        for key, relative in manifest["items"].items():
            if re.fullmatch(r"(?:food|beverage|ingredient|cooker|rare-guest|normal-group):[0-9]+", key) is None:
                raise ValueError("未知图片命名空间或非法 ID")
            expected = "images/" + key.replace(":", "-") + ".png"
            if relative != expected or rows[key]["path"] != expected:
                raise ValueError("图片路径不能由资产显示名称或任意相对路径指定")
            path = checked_path(directory / relative, args.work_directory)
            if path.is_symlink() or digest(path) != rows[key]["sha256"]:
                raise ValueError(f"图片来源 SHA256 不符：{key}")
            if key in items and digest(items[key][0]) != digest(path):
                raise ValueError(f"图片身份冲突：{key}")
            items[key] = (path, rows[key])
        for source in evidence["sources"]:
            # 纳管证据只保留 Addressables 相对名称和摘要，不写入机器用户名/磁盘位置。
            clean = {name: source[name] for name in ("relativePath", "length", "sha256")}
            if clean["relativePath"] in sources and sources[clean["relativePath"]] != clean:
                raise ValueError("相同来源文件有多个不同摘要")
            sources[clean["relativePath"]] = clean
        adaptation = dict(evidence.get("loaderAdaptation", {}))
        if "afterIL" in adaptation:
            adaptation["afterIL"] = [re.sub(r'ldstr ".*"', 'ldstr "<AssetStudioDirectory>/x64"', line) for line in adaptation["afterIL"]]
        tool_evidence.append({
            "toolPins": evidence.get("toolPins", {}),
            "loaderAdaptation": adaptation,
        })

    recipes: dict[str, tuple[list[int], int]] = {}
    if args.catalog_report:
        catalog_report = read_json(checked_path(args.catalog_report, args.work_directory))
        for source in catalog_report["sourceFiles"]:
            if source["relativePath"] != "catalog.bundle":
                raise ValueError("catalog 来源报告包含了其它输入")
            clean = {name: source[name] for name in ("relativePath", "length", "sha256")}
            sources[clean["relativePath"]] = clean
    recipe_sources: list[dict] = []
    for report_path in args.profile_report:
        report = read_json(checked_path(report_path, args.work_directory))
        for entry in report["objects"]:
            if entry["type"] != "MonoBehaviour" or re.fullmatch(r"(?:DLC[1-5]_)?RecipeProfile", entry.get("name") or "") is None:
                continue
            path = checked_path(Path(entry["typeTreePath"]), args.work_directory)
            profile = read_json(path)
            if profile["m_Name"] != entry["name"] or not isinstance(profile.get("recipes"), list):
                raise ValueError("RecipeProfile 精确类型/结构不符")
            recipe_sources.append({"name": entry["name"], "bundle": entry["originalBundle"], "file": entry["file"], "pathId": entry["pathId"], "typeTreeSha256": digest(path)})
            for recipe in profile["recipes"]:
                food_id = require_integer(recipe["foodID"], "foodID")
                recipe_id = require_integer(recipe["id"], "recipeId")
                ingredient_ids = [require_integer(value, "ingredientId") for value in recipe["ingredients"]]
                cooker = require_integer(recipe["cookerType"], "cookerType", 1)
                if cooker > 5 or not ingredient_ids:
                    raise ValueError("配方厨具或基础食材不在已核验类型范围")
                key = f"{food_id}:{recipe_id}"
                value = (ingredient_ids, cooker)
                if key in recipes and recipes[key] != value:
                    raise ValueError(f"同一 foodId:recipeId 的依赖不唯一：{key}")
                recipes[key] = value
    if not recipes or not items:
        raise ValueError("不可生成空资源集")

    image_directory = output / "images"
    image_directory.mkdir(parents=True)
    image_evidence = []
    manifest_items = {}
    for key in sorted(items):
        path, proof = items[key]
        relative = "images/" + key.replace(":", "-") + ".png"
        target = output / relative
        with Image.open(path) as image:
            if image.format != "PNG" or min(image.size) <= 0 or max(image.size) > 8192:
                raise ValueError("非预期 PNG 或图片尺寸异常")
            rgba = image.convert("RGBA")
            original_size = list(rgba.size)
            rgba.thumbnail((128, 128), Image.Resampling.LANCZOS)
            if rgba.getextrema()[3][1] == 0:
                raise ValueError(f"实体图片完全透明：{key}")
            rgba.save(target, format="PNG", optimize=True)
            image_evidence.append({**proof, "originalSha256": proof["sha256"], "sha256": digest(target), "originalSize": original_size, "width": rgba.width, "height": rgba.height})
        manifest_items[key] = relative
    source_fingerprint = hashlib.sha256(json.dumps(sorted(sources.values(), key=lambda s: s["relativePath"]), sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    manifest = {"version": 1, "items": manifest_items}
    (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    missing_recipe_images = sorted({f"ingredient:{ingredient}" for ingredients, _ in recipes.values() for ingredient in ingredients} - items.keys())
    guest_bindings = read_json(checked_path(args.guest_bindings, args.work_directory)) if args.guest_bindings else None
    evidence = {"version": 1, "gameWasStartedOrConnected": False, "sourceFingerprint": source_fingerprint, "thumbnailMaxEdge": 128, "sources": sorted(sources.values(), key=lambda s: s["relativePath"]), "images": image_evidence, "recipeProfiles": recipe_sources, "recipeCount": len(recipes), "missingRecipeIngredientImages": missing_recipe_images, "missingGuests": missing_guests, "guestBindings": guest_bindings, "tools": tool_evidence}
    (output / "source-evidence.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    lines = ["// 由 scripts/game-media/prepare_flutter_media.py 离线生成，请勿手工按名称补图。", f"// 来源指纹：{source_fingerprint}", "// 游戏图片版权归原作者；本索引不授予图片再分发许可。", "// 缺失或冲突身份保留通用图标；配方依赖严格使用 foodId:recipeId 双键。", "const Map<String, String> gameMediaAssets = {"]
    lines.extend(f"  '{key}': 'assets/game-media/{relative}'," for key, relative in manifest_items.items())
    lines.extend(["};", "", "// 基础食材沿序列化顺序保存，重复 ID 表示重复用量，不在前端重新计算。", "const Map<String, List<int>> gameMediaRecipeIngredients = {"])
    lines.extend(f"  '{key}': {ingredients}," for key, (ingredients, _) in sorted(recipes.items()))
    lines.extend(["};", "", "// 1 锅、2 烤架、3 油锅、4 蒸锅、5 料理台；只展示实际配方引用。", "const Map<String, int> gameMediaRecipeCookers = {"])
    lines.extend(f"  '{key}': {cooker}," for key, (_, cooker) in sorted(recipes.items()))
    lines.extend(["};", ""])
    (output / "game_media_catalog.g.dart").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"output": str(output), "counts": dict(Counter(key.split(":")[0] for key in items)), "recipes": len(recipes), "missingRecipeIngredientImages": missing_recipe_images, "sourceFingerprint": source_fingerprint}, ensure_ascii=False))


if __name__ == "__main__":
    main()
