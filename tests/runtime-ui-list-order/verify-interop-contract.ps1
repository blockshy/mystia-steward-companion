param(
    [Parameter(Mandatory = $true)][string[]]$InteropAssemblies,
    [Parameter(Mandatory = $true)][string]$CecilAssembly
)

$ErrorActionPreference = 'Stop'

# 只加载用于读取PE/CIL的Cecil，不加载游戏程序集到执行上下文，不触发IL2CPP类型初始化。
# 路径由开发者显式提供，既能检查实际安装版本，也能检查构建所依据的参考程序集。
[void][System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $CecilAssembly).Path)
$script:assertions = 0
function Assert-Contract([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
}

foreach ($assemblyPath in $InteropAssemblies) {
    $resolved = (Resolve-Path -LiteralPath $assemblyPath).Path
    $parameters = New-Object Mono.Cecil.ReaderParameters
    $parameters.ReadSymbols = $false
    $parameters.InMemory = $true
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($resolved, $parameters)
    try {
        $pair = @($assembly.MainModule.Types | Where-Object FullName -CEQ 'Il2CppSystem.Collections.Generic.KeyValuePair`2')
        $list = @($assembly.MainModule.Types | Where-Object FullName -CEQ 'Il2CppSystem.Collections.Generic.List`1')
        Assert-Contract ($pair.Count -eq 1 -and $list.Count -eq 1) '缺少唯一的原生List/KeyValuePair包装类型。'
        Assert-Contract (-not $pair[0].IsValueType -and $pair[0].IsClass -and $pair[0].IsSealed) 'KeyValuePair的CLR表示已变化，须重新审查封送边界。'
        Assert-Contract ($pair[0].BaseType.FullName -ceq 'Il2CppSystem.ValueType') 'KeyValuePair必须继承原生值类型包装。'

        $generic = @($list[0].Methods | Where-Object Name -CEQ 'set_Item')
        $boxed = @($list[0].Methods | Where-Object Name -CEQ 'System_Collections_IList_set_Item')
        Assert-Contract ($generic.Count -eq 1 -and $boxed.Count -eq 1) '必须存在唯一的泛型setter与非泛型IList setter。'
        Assert-Contract ($generic[0].Parameters.Count -eq 2 -and $generic[0].Parameters[1].ParameterType.IsGenericParameter) '泛型setter签名已变化。'
        Assert-Contract ($boxed[0].HasThis -and -not $boxed[0].IsStatic -and $boxed[0].ReturnType.FullName -ceq 'System.Void') 'IList setter实例/返回值契约不正确。'
        Assert-Contract ($boxed[0].Parameters.Count -eq 2 -and $boxed[0].Parameters[0].ParameterType.FullName -ceq 'System.Int32' -and $boxed[0].Parameters[1].ParameterType.FullName -ceq 'Il2CppSystem.Object') 'IList setter必须接收Int32及原生Object盒对象。'

        $genericCalls = @($generic[0].Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
        $boxedCalls = @($boxed[0].Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
        # 当前#783的泛型setter只认CLR值类型，而KVP是CLR引用类；不能把盒头地址按值payload送入原生函数。
        # 如果以后生成器修正了该实现，本断言将要求重新审核证据，不会静默套用本次结论。
        Assert-Contract ($genericCalls -ccontains 'get_IsValueType') '当前泛型setter不再使用CLR值类型判定，应复核生成器变化。'
        Assert-Contract ($genericCalls -ccontains 'Il2CppObjectBaseToPtr' -and $genericCalls -ccontains 'il2cpp_runtime_invoke') '泛型setter的对象指针传递路径不匹配。'
        Assert-Contract ($genericCalls -cnotcontains 'il2cpp_object_unbox') '泛型setter已新增原生拆箱，应重新核对本次故障假设。'
        Assert-Contract ($boxedCalls -ccontains 'Il2CppObjectBaseToPtr' -and $boxedCalls -ccontains 'il2cpp_runtime_invoke') '非泛型setter未按Object契约传递盒对象。'
        Assert-Contract ($boxedCalls -cnotcontains 'get_IsValueType' -and $boxedCalls -cnotcontains 'il2cpp_object_unbox') '非泛型setter不应由托管包装自行拆箱或猜测T。'
        [pscustomobject]@{
            Path = $resolved
            Sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
            PairClrKind = 'sealed class : Il2CppSystem.ValueType'
            GenericSetterToken = $generic[0].MetadataToken.ToString()
            SafeObjectSetterToken = $boxed[0].MetadataToken.ToString()
            ObjectSetter = $boxed[0].FullName
        } | ConvertTo-Json -Compress
    }
    finally { $assembly.Dispose() }
}
Write-Output "Interop metadata/CIL contract checks passed: $script:assertions assertions. No game assembly was executed."
