using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Updates;

// 接受共享夹具目录时仅执行生产校验器，不执行/加载载荷，供 Node 跨语言矩阵比较退出码。
if (args.Length == 2 && args[0] == "--validate")
{
    try { BusinessPackageManifest.Validate(args[1]); return 0; }
    catch (InvalidDataException error) { Console.Error.WriteLine(error.Message); return 1; }
}
var root = Path.Combine(Path.GetTempPath(), "mystia-package-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Reset()
{
    // root 由当前进程独占生成，清理之前验证固定临时父目录与前缀。
    if (Path.GetDirectoryName(root) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(root).StartsWith("mystia-package-")) throw new InvalidOperationException("测试目录归属错误。");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    Directory.CreateDirectory(root);
    var files = new List<object>();
    foreach (var name in BusinessPackageManifest.Files.Concat(new[] { "companion/data/empty.asset" }))
    {
        var target = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, name.EndsWith("empty.asset") ? "" : "受控测试组件：" + name);
        var bytes = File.ReadAllBytes(target);
        files.Add(new { path = name, size = bytes.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
    }
    File.WriteAllText(Path.Combine(root, "package-manifest.json"), JsonSerializer.Serialize(new {
        schemaVersion=1, product="mystia-steward-companion", version="1.3.1", sourceCommit=new string('a',40),
        sourceDigest=new string('b',64), toolchainSha256=new string('c',64), businessProtocolVersion=1, clientProtocolVersion=2, files
    }));
}
void Edit(Action<JsonNode> update)
{
    var path=Path.Combine(root,"package-manifest.json"); var document=JsonNode.Parse(File.ReadAllText(path))!;
    update(document); File.WriteAllText(path,document.ToJsonString());
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
        Reject("缺少 " + name, () => File.Delete(Path.Combine(root,name)));
        Reject("混版 " + name, () => File.AppendAllText(Path.Combine(root,name),"另一轮构建"));
        // 必需项的零长度连同声明摘要一起更新，确保拒绝原因是内容要求，而非偶然的摘要不匹配。
        Reject("空必需项 " + name, () => {
            File.WriteAllText(Path.Combine(root,name),"");
            Edit(json => { var entry=json["files"]!.AsArray().Single(item=>item!["path"]!.GetValue<string>()==name)!;
                entry["size"]=0; entry["sha256"]=Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(); });
        });
        Reject("清单生成前遗漏 " + name, () => {
            File.Delete(Path.Combine(root,name));
            Edit(json => { var files=json["files"]!.AsArray(); files.Remove(files.Single(item=>item!["path"]!.GetValue<string>()==name)); });
        });
    }
    Reject("额外DLL",()=>File.WriteAllText(Path.Combine(root,"unexpected.dll"),"bad"));
    Reject("客户端多余资源",()=>File.WriteAllText(Path.Combine(root,"companion/data/extra"),"bad"));
    Reject("重复字段",()=>File.WriteAllText(Path.Combine(root,"package-manifest.json"),File.ReadAllText(Path.Combine(root,"package-manifest.json")).Replace("\"schemaVersion\":1","\"schemaVersion\":1,\"schemaVersion\":1")));
    Reject("重复路径",()=>Edit(json=>json["files"]![1]=JsonNode.Parse(json["files"]![0]!.ToJsonString())));
    Reject("大小写重复",()=>Edit(json=>json["files"]![1]!["path"]=json["files"]![0]!["path"]!.GetValue<string>().ToUpperInvariant()));
    foreach(var value in new[]{"../outside.dll","/absolute","companion/../escape","companion/a:b","companion/NUL","companion/trailing.","companion\\wrong"})
        Reject(value,()=>Edit(json=>json["files"]![0]!["path"]=value));
    Reject("协议",()=>Edit(json=>json["clientProtocolVersion"]=1));
    Reject("未知字段",()=>Edit(json=>json["surprise"]=true));
    Reject("超大文件声明",()=>Edit(json=>json["files"]![0]!["size"]=536870913L));
    Reject("非法哈希",()=>Edit(json=>json["files"]![0]!["sha256"]=new string('g',64)));
    Reject("超长摘要",()=>File.WriteAllText(Path.Combine(root,"package-manifest.json"),new string('a',4194305)));
    // ZIP 在写入前拒绝危险目录项；测试仅创建当前人工根下的小型文本 ZIP。
    foreach (var names in new[] { new[] { "../escape" }, new[] { "mystia-steward-companion/../escape" },
        new[] { "mystia-steward-companion/a", "mystia-steward-companion/A" }, new[] { "other-product/data" },
        new[] { "mystia-steward-companion/CON" } })
    {
        Reset(); var zip = Path.Combine(root,"danger.zip"); var output=Path.Combine(root,"extract");
        using(var archive=System.IO.Compression.ZipFile.Open(zip,System.IO.Compression.ZipArchiveMode.Create))
            foreach(var name in names) { using var writer=new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("test"); }
        try { BusinessPackageManifest.Extract(zip,output,CancellationToken.None); throw new Exception("危险ZIP未拒绝。"); }
        catch(InvalidDataException) { if(Directory.Exists(output)) throw new Exception("拒绝前创建了解压目录。"); checks++; }
    }
    Reset();
    var linkedZip=Path.Combine(root,"linked.zip");
    using(var archive=System.IO.Compression.ZipFile.Open(linkedZip,System.IO.Compression.ZipArchiveMode.Create))
    { var entry=archive.CreateEntry("mystia-steward-companion/link"); entry.ExternalAttributes=unchecked((int)0xa1ff0000); }
    try { BusinessPackageManifest.Extract(linkedZip,Path.Combine(root,"extract"),CancellationToken.None); throw new Exception("ZIP链接未拒绝。"); }
    catch(InvalidDataException) { checks++; }
    Console.WriteLine($"PASS: {checks} 项新版完整包校验断言；未执行载荷或接触游戏。");
    return 0;
}
finally { Directory.Delete(root,recursive:true); }
