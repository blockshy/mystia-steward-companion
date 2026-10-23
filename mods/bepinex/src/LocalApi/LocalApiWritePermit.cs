namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 手工写操作的短期许可，绑定请求进入时的设备和权威修订，不授予自动化租约。
/// 排队等待期间不占用权威锁；实际副作用必须在 Execute 中复核并完成，
/// 因而切主/修改生效配置可以先发生，并使尚未开始的旧请求失效。
/// </summary>
internal sealed class LocalApiWritePermit
{
    private readonly object _authorityGate;
    private readonly Action _validate;
    private readonly Action _invalidate;

    internal LocalApiWritePermit(object authorityGate, Action validate, Action invalidate)
    {
        _authorityGate = authorityGate;
        _validate = validate;
        _invalidate = invalidate;
    }

    /// <summary>仅包围实际写入，不包围等待主线程的过程；验证与副作用之间不能释放锁。</summary>
    public TResult Execute<TResult>(Func<TResult> action)
    {
        lock (_authorityGate)
        {
            _validate();
            _invalidate();
            try { return action(); }
            finally { _invalidate(); }
        }
    }
}
