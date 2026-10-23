namespace Il2CppSystem.Collections.Generic;

/// <summary>
/// 仅用于严格集合读取器的故障注入，完整限定名及公开签名复现 783 包装。
/// 与真实程序集通过 RuntimeCore 别名隔离；不创建游戏对象，也不替代真实元数据验证。
/// </summary>
internal sealed class HashSet<T>
{
    private readonly T[] _values;
    private readonly string _fault;
    private int _countReads;
    public int DisposeCalls { get; private set; }
    public int Count => _fault == "count" && ++_countReads > 1 ? _values.Length + 1 : _values.Length;
    public HashSet(T[] values, string fault) { _values = values; _fault = fault; }
    public Enumerator GetEnumerator() => new(this);

    public sealed class Enumerator
    {
        private readonly HashSet<T> _owner;
        private int _index = -1;
        public Enumerator(HashSet<T> owner) => _owner = owner;
        public bool MoveNext()
        {
            if (_owner._fault == "move") throw new global::System.InvalidOperationException("move");
            _index++;
            if (_owner._fault == "short") return false;
            return _index < _owner._values.Length || _owner._fault == "extra";
        }
        public T Current => _owner._fault == "current"
            ? throw new global::System.InvalidOperationException("current") : _owner._values[_index];
        public void Dispose()
        {
            _owner.DisposeCalls++;
            if (_owner._fault == "dispose") throw new global::System.InvalidOperationException("dispose");
        }
    }
}
