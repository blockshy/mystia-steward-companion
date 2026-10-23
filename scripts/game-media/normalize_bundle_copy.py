"""仅还原项目 temp 内的 bundle 副本，绝不改写游戏资源。

依据当前同版本 Native 中 IZKYAESStreamProcessor.EncryptStream.Read，读取变换是
对每个字节异或同一 byte。使用已知 UnityFS 完整签名校验这一变换，拒绝其它格式；
不调用游戏、解密器程序集或任何网络服务，不提取用户凭据。
"""
from pathlib import Path
import hashlib
import json
import sys
from workspace_paths import checked_path

root = Path(sys.argv[3]).resolve()
source = checked_path(Path(sys.argv[1]), root)
target = checked_path(Path(sys.argv[2]), root, must_exist=False)
if root not in source.parents or root not in target.parents or target.exists():
    raise ValueError("仅允许 temp 探针副本，且目标必须不存在")
data = source.read_bytes()
signature = b"UnityFS\0"
if len(data) < 32:
    raise ValueError("bundle 过短")
key = data[0] ^ signature[0]
table = bytes(value ^ key for value in range(256))
if data[:len(signature)].translate(table) != signature:
    raise ValueError("资源不符合本版本已核验的流变换，停止处理")
normalized = data.translate(table)
# UnityFS 在签名后是大端格式版本；当前游戏实际版本 8。未知版本留给后续显式分析。
version = int.from_bytes(normalized[8:12], "big")
if version != 8:
    raise ValueError(f"非预期的 UnityFS 版本：{version}")
with target.open("xb") as stream:
    stream.write(normalized)
print(json.dumps({
    "source": str(source), "target": str(target), "transform": "identity" if key == 0 else "verified-fixed-xor",
    "length": len(data), "sourceSha256": hashlib.sha256(data).hexdigest(),
    "normalizedSha256": hashlib.sha256(normalized).hexdigest(),
}))
