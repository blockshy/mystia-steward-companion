namespace MystiaStewardCompanion.Plugin;

/// <summary>
/// 将凭据切换与已经排队的本机控制发送串行化。发送在真正执行时读取最新值，
/// 不使用排队时捕获的旧 Token；更新返回后，旧发送已结束，之后只能发送新凭据。
/// 此锁不进入 Unity 更新循环，只覆盖有界本机控制请求与进程创建。
/// </summary>
internal sealed class CompanionConnectionToken
{
    private readonly object _sync = new();
    private string? _current;
    public void Reset() { lock (_sync) _current = null; }
    public void Set(string token) { lock (_sync) _current = token; }
    public T Use<T>(string initialToken, Func<string, T> operation)
    {
        lock (_sync) return operation(_current ?? initialToken);
    }
}
