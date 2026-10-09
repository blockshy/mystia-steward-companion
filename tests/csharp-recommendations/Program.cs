using System.Text;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Recommendation;

// 每一行都是独立 fixture；输出采用逐行 JSON，方便 Node oracle 对完整值树作深度比较。
// 不接触游戏与网络；任何实现异常均标记当前用例失败，不能伪装成空结果。
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
string? line;
while ((line = Console.ReadLine()) is not null)
{
    try
    {
        var request = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("缺少测试请求。");
        var operation = RecommendationJson.Str(request["operation"]);
        var input = RecommendationJson.Obj(request["args"]);
        var before = input.ToJsonString();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = operation.StartsWith("support.", StringComparison.Ordinal)
            ? SupportDispatch.Invoke(operation[8..], input) : RecommendationEngine.Invoke(operation, input);
        stopwatch.Stop();
        if (before != input.ToJsonString()) throw new InvalidOperationException("纯计算修改了输入值树。");
        Console.WriteLine(RecommendationJson.Object(("ok", true), ("result", result), ("elapsedMs", stopwatch.Elapsed.TotalMilliseconds)).ToJsonString());
    }
    catch (Exception error)
    {
        Console.WriteLine(RecommendationJson.Object(("ok", false), ("error", error.ToString())).ToJsonString());
    }
}
