using System.Text.Json.Serialization;

namespace MystiaStewardCompanion.GameMedia;

/// <summary>命名空间与目录身份是图片唯一键；名称仅供展示，不参与文件路径或身份推断。</summary>
internal sealed record GameMediaKey(string Kind, int Id, string Name)
{
    [JsonIgnore] public string Key => Kind + ":" + Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>唯一允许离开 Unity 主线程的图像结果：托管 PNG 字节及明确尺寸，不含任何 wrapper。</summary>
internal sealed record GameMediaImage(byte[] Png, int Width, int Height);

/// <summary>基础材料顺序和重复项保留游戏原始配方语义，料理 ID 与配方 ID 不互换。</summary>
internal sealed record GameMediaRecipe(int FoodId, int RecipeId, int[] IngredientIds, int CookerType);

/// <summary>精确资源版本和目录计划。调用方创建后不得修改集合或成员。</summary>
internal sealed record GameMediaPlan(string SourceFingerprint, IReadOnlyList<GameMediaKey> Items,
    IReadOnlyList<GameMediaRecipe> Recipes,
    [property: JsonIgnore] string? StartupFingerprint = null);

/// <summary>完整校验通过的缓存与其原始提取计划；只能恢复图片服务，不能替代业务目录或运行时快照。</summary>
internal sealed record GameMediaCachedPlan(GameMediaPlan Plan, GameMediaManifest Manifest);

internal enum GameMediaExtractState { Pending, Ready, Missing, Failed }

/// <summary>实现者只能在 Controller 的主线程 Tick 中被调用；磁盘服务不持有 Unity 对象。</summary>
internal interface IRuntimeGameMediaExtractor
{
    /// <summary>
    /// 最近一次计划读取若只在等待纯托管后台准备任务，返回该任务作为提前重试信号。
    /// 服务只观察完成状态，不等待、不在线程池回调中调用提取器；真正的 Unity 目录未就绪
    /// 仍返回 null 并沿用低频重试，避免为加速缓存恢复而每帧探测原生集合。
    /// </summary>
    Task? PendingPlanPreparation => null;
    bool TryCreatePlan(out GameMediaPlan? plan, out string error);
    GameMediaExtractState Extract(GameMediaKey item, out GameMediaImage? image, out string message);
}

internal sealed record GameMediaItem(string Key, string Kind, int Id, string Name, string Sha256,
    int ByteLength, int Width, int Height);
internal sealed record GameMediaMissing(string Key, string Reason);
internal sealed record GameMediaManifest(int Version, string Generation, string SourceFingerprint,
    IReadOnlyList<GameMediaItem> Items, IReadOnlyList<GameMediaRecipe> Recipes, IReadOnlyList<GameMediaMissing> Missing);
internal sealed record GameMediaStatus(string Phase, string? Generation, int Completed, int Total,
    string Message, string? Error);

/// <summary>
/// 单次资源作业的低频诊断：仅固定事件名、状态、计数、已验证代次和缓存命中结果。
/// 不携带磁盘路径、原生异常或用户凭据，供宿主记录启动复用证据。
/// </summary>
internal sealed record GameMediaDiagnostic(string Event, string Phase, int Completed, int Total,
    string? Generation, bool? CacheHit, string? Reason = null);

/// <summary>服务向 HTTP 返回明确状态码；异常内容只含协议原因，不暴露本机路径或凭据。</summary>
internal sealed class GameMediaException : Exception
{
    internal int StatusCode { get; }
    internal GameMediaException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}
