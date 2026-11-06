using System.Reflection;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// #783 VertexHelper 的短期访问边界。正式引用不含 UnityEngine.UI，所以绑定已核实的
/// AddVert/索引签名；顶点通道使用 Il2Cppmscorlib 中的真实 List&lt;T&gt;。实例只在一次
/// OnPopulateMesh 回调栈内存在，绝不把 Graphic 共用的 VertexHelper 保存到下一帧。
/// </summary>
internal sealed class RuntimePinnedListMeshAccess
{
    private readonly object _owner;
    private readonly Action _clear;
    private readonly Func<int> _vertexCount, _indexCount;
    private readonly Action<Vector3, Color32, Vector4, Vector4, Vector4, Vector4, Vector3, Vector4> _addVertex;
    private readonly Action<int, int, int> _addTriangle;

    internal RuntimePinnedListMeshAccess(object owner)
    {
        var type = RuntimePinnedListUiAccess.Type("UnityEngine.UI.VertexHelper");
        if (!type.IsInstanceOfType(owner)) throw new InvalidOperationException("原生 VertexHelper 类型不一致。");
        _owner = owner;
        _clear = RuntimePinnedListUiAccess.Bind<Action>(owner, "Clear");
        _vertexCount = RuntimePinnedListUiAccess.Bind<Func<int>>(owner, "get_currentVertCount");
        _indexCount = RuntimePinnedListUiAccess.Bind<Func<int>>(owner, "get_currentIndexCount");
        _addTriangle = RuntimePinnedListUiAccess.Bind<Action<int, int, int>>(owner, "AddTriangle");
        var add = type.GetMethod("AddVert", BindingFlags.Instance | BindingFlags.Public, null,
            new[] { typeof(Vector3), typeof(Color32), typeof(Vector4), typeof(Vector4), typeof(Vector4),
                typeof(Vector4), typeof(Vector3), typeof(Vector4) }, null)
            ?? throw new InvalidOperationException("原生完整顶点写入接口不可用。");
        _addVertex = add.CreateDelegate<Action<Vector3, Color32, Vector4, Vector4, Vector4, Vector4, Vector3, Vector4>>(owner);
    }

    internal void Read(List<BackgroundBandVertex> vertices, List<int> indices)
    {
        vertices.Clear(); indices.Clear();
        // 首次 Filled=0 可在原生列表尚未初始化时返回。使用正式计数属性确认合法空网格，
        // 不要求这一帧必须已经存在九个通道集合；半空或异常数量仍按坏数据拒绝。
        var count = _vertexCount(); var indexCount = _indexCount();
        if (count == 0 && indexCount == 0) return;
        if (count < 1 || count > 65535 || indexCount < 3 || indexCount > 196605 || indexCount % 3 != 0)
            throw new InvalidOperationException("原生背景顶点数量不完整或超出有界容量。");
        // 这些名称对应 #783 interop 的确切原生字段；不是按对象名称搜索，也不是猜顶点顺序。
        var positions = Channel<Vector3>("m_Positions");
        var colors = Channel<Color32>("m_Colors");
        var uv0 = Channel<Vector4>("m_Uv0S"); var uv1 = Channel<Vector4>("m_Uv1S");
        var uv2 = Channel<Vector4>("m_Uv2S"); var uv3 = Channel<Vector4>("m_Uv3S");
        var normals = Channel<Vector3>("m_Normals"); var tangents = Channel<Vector4>("m_Tangents");
        var nativeIndices = Channel<int>("m_Indices");
        if (positions.Count != count || nativeIndices.Count != indexCount || colors.Count != count || normals.Count != count || tangents.Count != count
            || uv0.Count != count || uv1.Count != count || uv2.Count != count || uv3.Count != count
            || (count == 0) != (nativeIndices.Count == 0))
            throw new InvalidOperationException("原生背景顶点通道不完整或超出有界容量。");
        for (var index = 0; index < count; index++)
            vertices.Add(new BackgroundBandVertex(positions[index], normals[index], tangents[index], colors[index],
                uv0[index], uv1[index], uv2[index], uv3[index]));
        for (var index = 0; index < nativeIndices.Count; index++)
        {
            var value = nativeIndices[index];
            if (value < 0 || value >= count) throw new InvalidOperationException("原生背景索引超出顶点范围。");
            indices.Add(value);
        }
    }

    /// <summary>调用前已完成全部裁剪和容量验证；使用原接口回填，保留后续原生 IMeshModifier 流程。</summary>
    internal void Write(IReadOnlyList<BackgroundBandVertex> vertices, IReadOnlyList<int>? indices)
    {
        _clear();
        foreach (var vertex in vertices)
            _addVertex(vertex.Position, vertex.Color, vertex.Uv0, vertex.Uv1, vertex.Uv2, vertex.Uv3, vertex.Normal, vertex.Tangent);
        if (indices == null)
        {
            for (var index = 0; index < vertices.Count; index += 3) _addTriangle(index, index + 1, index + 2);
        }
        else
        {
            for (var index = 0; index < indices.Count; index += 3) _addTriangle(indices[index], indices[index + 1], indices[index + 2]);
        }
    }

    private Il2CppSystem.Collections.Generic.List<T> Channel<T>(string name) =>
        RuntimeReflectionUtility.GetMemberValue(_owner, name) as Il2CppSystem.Collections.Generic.List<T>
            ?? throw new InvalidOperationException($"原生背景顶点通道不可用：{name}。");
}
