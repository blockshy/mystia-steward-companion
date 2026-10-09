using System.Security.Cryptography;
using MystiaStewardCompanion.Updates;

// 全部测试文件只存在于专用临时目录；不会操作本机游戏安装或真实更新配置。
var root = Path.Combine(Path.GetTempPath(), "mystia-business-package-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Reset()
{
    var lines = new List<string>();
    foreach (var name in BusinessPackageManifest.Files)
    {
        var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "受控测试组件：" + name);
        lines.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() + "  " + name);
    }
    File.WriteAllLines(Path.Combine(root, "business-bundle.sha256"), lines);
}
void Reject(string label, Action damage)
{
    Reset(); damage();
    try { BusinessPackageManifest.Validate(root); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException("未拒绝损坏包：" + label);
}
try
{
    Reset(); BusinessPackageManifest.Validate(root); checks++;
    foreach (var name in BusinessPackageManifest.Files)
    {
        Reject("缺少 " + name, () => File.Delete(Path.Combine(root, name)));
        Reject("混版 " + name, () => File.AppendAllText(Path.Combine(root, name), "另一轮构建"));
        Reject("空文件 " + name, () => File.WriteAllText(Path.Combine(root, name), ""));
    }
    var manifest = Path.Combine(root, "business-bundle.sha256");
    Reject("缺少摘要", () => File.Delete(manifest));
    Reject("重复条目", () => { var lines = File.ReadAllLines(manifest); lines[1] = lines[0]; File.WriteAllLines(manifest, lines); });
    Reject("越界路径", () => { var lines = File.ReadAllLines(manifest); lines[0] = lines[0][..66] + "../outside.dll"; File.WriteAllLines(manifest, lines); });
    Reject("多余条目", () => File.AppendAllText(manifest, "bad\n"));
    Reject("超长摘要", () => File.WriteAllText(manifest, new string('a', 5000)));
    Reject("无效哈希", () => { var lines = File.ReadAllLines(manifest); lines[0] = "g" + lines[0][1..]; File.WriteAllLines(manifest, lines); });
    Console.WriteLine($"PASS: {checks} 项业务成套包完整性断言；未执行或加载任何组件。");
}
finally
{
    // root 是本进程生成的固定前缀 GUID 临时路径，且只含上述测试文件。
    Directory.Delete(root, recursive: true);
}
