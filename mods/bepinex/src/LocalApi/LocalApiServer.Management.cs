using System.Text.Json;

namespace MystiaStewardCompanion.LocalApi;

internal sealed partial class LocalApiServer
{
    private DiagnosticPackageStore DiagnosticPackages
    {
        get
        {
            lock (_diagnosticPackagesLock)
                return _diagnosticPackages ??= new DiagnosticPackageStore(ResolveDiagnosticPackageDirectory());
        }
    }

    /// <summary>密码仅放 JSON 请求体，不进入 URL。未知、重复、显式 null 或空请求一律拒绝。</summary>
    private static LocalApiConnectionConfigUpdate ReadConnectionConfigUpdate(HttpRequestData request)
    {
        try
        {
            using var document = JsonDocument.Parse(HttpRequestReader.ReadRequiredJsonBody(request),
                new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            bool? enabled = null; string? host = null; string? token = null;
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                switch (property.Name)
                {
                    case "lanEnabled": enabled = property.Value.GetBoolean(); break;
                    case "lanHost": host = property.Value.GetString() ?? throw new JsonException(); break;
                    case "token": token = property.Value.GetString() ?? throw new JsonException(); break;
                    default: throw new JsonException();
                }
            }
            if (names.Count == 0) throw new JsonException();
            return new LocalApiConnectionConfigUpdate { LanEnabled = enabled, LanBindHost = host, Token = token };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            // 不能回显 JSON 异常中的输入内容，避免密码流入日志或错误状态。
            throw new CompanionDeviceAuthorityException(400, "连接设置格式无效，请检查后重新提交。");
        }
    }

    private string ListDiagnosticPackagesJson()
    {
        try { return ToJson(new LocalApiDiagnosticPackageListDto { Ok = true, Items = DiagnosticPackages.List() }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ToJson(new LocalApiDiagnosticPackageListDto { Error = "无法安全读取诊断包目录，请在本机检查目录权限及文件状态。" });
        }
    }

    private string DeleteDiagnosticPackageJson(string id)
    {
        if (string.IsNullOrEmpty(id)) return ToJson(new LocalApiErrorDto { Error = "请选择已列举的诊断包。" });
        try
        {
            DiagnosticPackages.Delete(id);
            return ToJson(new { ok = true, id });
        }
        catch (InvalidOperationException error) { return ToJson(new LocalApiErrorDto { Error = error.Message }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ToJson(new LocalApiErrorDto { Error = "未能删除该诊断包，请刷新并检查本机文件权限；不要重复提交旧确认。" });
        }
    }
}
