using System.Net.Sockets;
using System.Text;
using MystiaStewardCompanion.GameMedia;
using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.LocalApi;

internal sealed partial class LocalApiServer
{
    /// <summary>
    /// 资源变化先使全部业务帧/排队许可失效，再撤销旧租约。持续料理还会在每个原生副作用前
    /// 通过 RuntimeAutomationControlState 直接检查 ready，因此不依赖 HTTP 客户端是否主动释放租约。
    /// 事件总在资源锁外发布，保持既有 authority → lease → control 的锁次序。
    /// </summary>
    private void OnGameMediaReadinessChanged()
    {
        InvalidateBusinessInput();
        if (_gameMediaService?.IsReady != false) return;
        lock (_authorityTransitionLock)
        lock (_automationLeaseLock)
        {
            _automationLease = null;
            RuntimeAutomationControlState.RevokeLease("game-media-not-ready",
                "游戏图片尚未就绪；已有料理保留在原厨具，等待图片、主设备连接与配置就绪。");
        }
    }

    private GameMediaService RequireGameMediaService() => _gameMediaService
        ?? throw new GameMediaException(503, "game-media-unavailable");

    /// <summary>
    /// 初始化期间保留设备注册/同步、连接恢复、日志和更新管理；业务写入与 lease 必须等待整代就绪。
    /// 重读本身不要求 ready，否则初始化失败后无法恢复。队列写入还在实际执行前复核同一判断。
    /// </summary>
    private static bool RequiresGameMediaReady(string path) => path.StartsWith("/favorites/", StringComparison.Ordinal)
        || path.StartsWith("/custom-recipes/", StringComparison.Ordinal)
        || path.StartsWith("/inventory/", StringComparison.Ordinal)
        || path.StartsWith("/orders/", StringComparison.Ordinal)
        || path.StartsWith("/rare-guests/invite", StringComparison.Ordinal)
        || path is "/automation/lease/acquire" or "/automation/barriers/ack" or "/business/automation/retry" or "/business/automation/reset";

    private void RequireGameMediaReady()
    {
        if (_gameMediaService != null && !_gameMediaService.IsReady)
            throw new GameMediaException(409, "game-media-not-ready");
    }

    /// <summary>只有已认证请求可到达；严格当前代次，正文为实际 PNG，不能按 key 拼接任意磁盘路径。</summary>
    private void WriteGameMediaImage(NetworkStream stream, string query)
    {
        var body = RequireGameMediaService().Image(ReadStringQuery(query, "generation"), ReadStringQuery(query, "key"));
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: "
            + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
    }
}
