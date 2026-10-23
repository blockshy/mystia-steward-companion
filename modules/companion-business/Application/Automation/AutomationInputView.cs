using System.Text.Json.Nodes;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application.Automation;

/// <summary>
/// 推荐结果的只读组件视图。就绪状态与数组分开保存，避免为了添加 isCurrent/pending 等小字段，
/// 先深复制整棵推荐树再把它放进另一个 JSON 父节点。此类型不赋予数组任何执行权限。
/// </summary>
/// <remarks>
/// Rows 及其全部后代从传入开始必须永久只读；所有 JsonObject/JsonArray 必须已由宿主在独占阶段
/// 递归物化，不能把 .NET 6 解析节点的首次懒展开留给并发读者。视图不冻结或物化可变 JsonNode。
/// 普通外部 JSON 必须使用 AutomationCoordinator.Advance，由该入口取得独立所有权后再构造视图。
/// </remarks>
public readonly record struct AutomationResultView(JsonArray? Rows, bool IsCurrent, bool Pending, string? Error)
{
    /// <summary>从协调器已经独占的 JSON 副本读取状态；严格沿用原来的布尔、字符串和数组解析规则。</summary>
    internal static AutomationResultView FromOwnedJson(JsonObject value, string rowsKey) =>
        new(value[rowsKey] as JsonArray, J.Bool(value["isCurrent"]), J.Bool(value["pending"]), J.Str(value["error"]));
}

/// <summary>
/// 自动化输入的只读组件视图。它不把现有 JsonNode 重新挂到父节点，不复制整棵候选树，
/// 仅保存宿主已经冻结的托管组件引用及推荐就绪标量。
/// </summary>
/// <remarks>
/// 这不是仅在 Advance 调用栈内有效的借用：协调器会保留视图，供后续 Complete、人工确认和
/// HTTP Snapshot 使用。宿主必须保证所有组件及后代从发布起永久不修改；新快照、新权限、新结果
/// 只能创建新组件并整体替换。所有对象/数组还须在交给本视图之前递归枚举，完成 .NET 6 的懒展开；
/// ToJsonString/WriteTo 可能直接写出原 JsonElement，不是完成物化的保证。不能传入随后继续填写、
/// 复用为工作区或交给不受控调用方修改的树。
/// 该约束仅适用于显式的 AdvanceFrozen 入口；既有 Advance(JsonObject) 仍深复制公开可变输入。
/// </remarks>
public sealed class AutomationInputView
{
    internal JsonObject Snapshot { get; }
    internal JsonObject Preferences { get; }
    internal JsonObject Data { get; }
    internal JsonObject Favorites { get; }
    internal JsonObject Authority { get; }
    internal AutomationResultView Recommendations { get; }
    internal AutomationResultView NormalExecutionTargets { get; }

    private AutomationInputView(JsonObject snapshot, JsonObject preferences, JsonObject data, JsonObject favorites,
        JsonObject authority, AutomationResultView recommendations, AutomationResultView normalExecutionTargets)
    {
        Snapshot = snapshot;
        Preferences = preferences;
        Data = data;
        Favorites = favorites;
        Authority = authority;
        Recommendations = recommendations;
        NormalExecutionTargets = normalExecutionTargets;
    }

    /// <summary>
    /// 为可信宿主创建零深复制视图。调用即承诺组件已经完成构造、递归物化并永久只读；此处只检查必需
    /// 组件非空，不把“冻结”误写成一次权限验证。权限、代次、租约、订单身份仍由每次 Advance 检查。
    /// </summary>
    public static AutomationInputView FromFrozenComponents(JsonObject snapshot, JsonObject preferences,
        JsonObject data, JsonObject favorites, JsonObject authority, AutomationResultView recommendations,
        AutomationResultView normalExecutionTargets)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(favorites);
        ArgumentNullException.ThrowIfNull(authority);
        return new(snapshot, preferences, data, favorites, authority, recommendations, normalExecutionTargets);
    }

    /// <summary>
    /// 只允许协调器把自己刚取得的独立副本映射为组件；不向外提供“不复制的普通 JSON”快捷入口。
    /// 缺失或类型不符的对象仍按旧接口转为空对象，后续准入规则继续拒绝不完整输入。
    /// </summary>
    internal static AutomationInputView FromOwnedJson(JsonObject input) => new(
        J.Obj(input["snapshot"]), J.Obj(input["preferences"]), J.Obj(input["data"]), J.Obj(input["favorites"]),
        J.Obj(input["authority"]), AutomationResultView.FromOwnedJson(J.Obj(input["recommendations"]), "recommendations"),
        AutomationResultView.FromOwnedJson(J.Obj(input["normalExecutionTargets"]), "targets"));
}
