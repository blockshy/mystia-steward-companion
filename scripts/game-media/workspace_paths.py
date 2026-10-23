"""离线管线统一路径边界：只读明确工作目录内副本，拒绝链接/重解析祖先。"""
from pathlib import Path
import os
import stat


def checked_path(value: Path, work: Path, *, must_exist: bool = True) -> Path:
    # 必须先检查原始祖先，再 resolve；提前 resolve 会掩盖 junction 的存在。
    absolute = Path(os.path.abspath(value))
    root = Path(os.path.abspath(work))
    for candidate in (absolute, *absolute.parents, root, *root.parents):
        try:
            metadata = candidate.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & 0x400:
            raise ValueError("离线工作路径不得经过符号链接或重解析点")
    result, resolved_root = absolute.resolve(), root.resolve()
    if resolved_root not in result.parents:
        raise ValueError("输入/输出必须位于显式工作目录内")
    if must_exist and not result.is_file():
        raise ValueError("预期工作目录内的现有普通文件")
    return result
