using System.Reflection;
using System.Reflection.Emit;
using MystiaStewardCompanion.Save;

const string typeName = "MystiaStewardCompanion.Tests.LateLoadedProbe";

try
{
    AssertEqual<Type?>(null, RuntimeReflectionUtility.FindType(typeName), "Unknown type unexpectedly resolved.");

    var assembly = AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName($"runtime-reflection-smoke-{Guid.NewGuid():N}"),
        AssemblyBuilderAccess.Run);
    var module = assembly.DefineDynamicModule("main");
    var expected = module.DefineType(typeName, TypeAttributes.Public).CreateType();

    AssertEqual(expected, RuntimeReflectionUtility.FindType(typeName), "A type loaded after the first lookup remained negatively cached.");
    AssertEqual(expected, RuntimeReflectionUtility.FindType(typeName), "Successful type lookup was not stable.");
    Console.WriteLine("PASS: failed type lookups remain retryable and successful lookups are cached.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}
