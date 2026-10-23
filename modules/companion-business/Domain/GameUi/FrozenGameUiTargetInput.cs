using System.Text.Json.Nodes;

namespace MystiaStewardCompanion.Business.Domain.GameUi;

/// <summary>
/// 游戏辅助目标的强类型只读输入。宿主可直接借用本轮冻结快照和业务结果中的节点，
/// 不必为了传参把目录、完整推荐及所有订单再次装入一个会深复制的 JSON 对象。
/// </summary>
/// <remarks>
/// “冻结”是调用方的所有权约定，而不是把可变 <see cref="JsonNode"/> 包装成不可变容器：
/// 调用期间宿主不得修改这些节点，服务也不写入、移动或长期保存它们。输出仍由服务独立构造，
/// 可在调用结束后由消费者修改；不能把此结构当作跨代次执行许可或跳过宿主的当前身份检查。
/// </remarks>
/// <param name="Operation">目标通道，仅接受 rare 或 normal；来源与协调操作继续使用现有专用入口。</param>
/// <param name="Data">已规范化的本轮目录；只读借用，不能包含游戏对象或 IL2CPP 包装器。</param>
/// <param name="Recommendations">稀客已选主方案列表；本入口不重新计算评分或改变方案顺序。</param>
/// <param name="Orders">普客当前订单列表；精确订单身份校验仍由目标服务执行。</param>
/// <param name="ExecutionTargets">普客已选特殊执行目标列表。</param>
/// <param name="ExecutionTargetsCurrent">特殊执行目标是否当前；false 不能借用旧目标获得执行资格。</param>
/// <param name="Color">已保存的业务通道颜色；仍由宿主发布边界校验线协议格式。</param>
/// <param name="Features">本通道游戏辅助分项；输出会复制，输入父节点保持不变。</param>
/// <param name="SpecialBusiness">本轮特殊经营规则；JSON 适配入口保留 rare 的 options 优先级。</param>
/// <param name="BusinessGeneration">本轮经营代次，仅用于核对普客特殊目标的原有完整规则。</param>
/// <param name="PrioritizeMissionRecipe">是否优先已验证的任务配方；不绕过任务上下文验证。</param>
public readonly record struct FrozenGameUiTargetInput(
    string Operation,
    JsonObject Data,
    JsonNode? Recommendations = null,
    JsonNode? Orders = null,
    JsonNode? ExecutionTargets = null,
    bool ExecutionTargetsCurrent = false,
    string Color = "",
    JsonNode? Features = null,
    JsonNode? SpecialBusiness = null,
    double BusinessGeneration = 0,
    bool PrioritizeMissionRecipe = false);
