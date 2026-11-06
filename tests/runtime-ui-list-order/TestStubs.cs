// 此项目只验证纯托管列表重排。颜色与trace验证替身仅满足正式目标模型的编译依赖，
// 不声明已经验证Unity图形、Harmony注入或IL2CPP实际字段布局。
namespace UnityEngine
{
    public readonly record struct Color(float r, float g, float b, float a);
}

namespace MystiaStewardCompanion.Save
{
    internal static class RuntimeOrderTraceIdService
    {
        internal static bool TryNormalizeTargetTraceId(RuntimeUiTargetKind kind, string traceId,
            bool enabled, out string normalized, out string failure)
        {
            normalized = traceId;
            var prefix = kind == RuntimeUiTargetKind.Rare ? "R-" : "N-";
            var valid = traceId.StartsWith(prefix, StringComparison.Ordinal)
                && traceId.Length > 2 && traceId[2..].All(character => character is >= '0' and <= '9');
            failure = valid ? "" : "invalid fixture trace";
            return valid;
        }
    }
}
