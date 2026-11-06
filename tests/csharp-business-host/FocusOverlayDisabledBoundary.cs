using System.Text.Json.Nodes;

namespace MystiaStewardCompanion.Ui
{
    /// <summary>
    /// 仅用于此独立业务宿主的关闭态类型占位。这个测试项目不加载Unity、媒体缓存或专注层，
    /// 不可构造有效显示计划；正式富结构投影、来源作用域和发布边界由FlutterContractsHost覆盖。
    /// </summary>
    internal sealed class GameFocusOverlayPlans
    {
        private GameFocusOverlayPlans() { }
    }
}

namespace MystiaStewardCompanion.LocalApi
{
    internal sealed partial class LocalApiServer
    {
        /// <summary>
        /// 与生产功能默认关闭时的Prepare行为一致：推荐、自动化及BusinessPublication仍执行
        /// 正式源码，只省略本测试未启用的Unity展示投影，不替换任何业务判断或断言。
        /// </summary>
        private Ui.GameFocusOverlayPlans? PrepareFocusOverlayPlans(BusinessFrame frame,
            JsonObject result, JsonObject gameUiTargets) => null;

        /// <summary>关闭态只有null可提交；若测试边界意外产生UI计划，立即失败，不能默默丢弃。</summary>
        private static void CommitFocusOverlayPlans(Ui.GameFocusOverlayPlans? plans)
        {
            if (plans != null) throw new InvalidOperationException("独立业务宿主未启用游戏内专注显示，不能提交显示计划。");
        }
    }
}
