using AsmResolver.DotNet;
using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using LibCpp2IL;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length != 5)
{
    Console.Error.WriteLine(
        "Usage: InteropGenerator <GameAssembly.dll> <global-metadata.dat> " +
        "<Unity version> <Unity base libraries directory> <output directory>");
    return 2;
}

var gameAssemblyPath = RequireFile(args[0], "GameAssembly.dll");
var metadataPath = RequireFile(args[1], "global-metadata.dat");
var unityVersion = UnityVersion.Parse(args[2]);
var unityBaseLibrariesPath = RequireDirectory(args[3], "Unity base libraries directory");
var outputPath = Path.GetFullPath(args[4]);

if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any())
{
    throw new InvalidOperationException($"Output directory must be empty: {outputPath}");
}

Directory.CreateDirectory(outputPath);
AddTrustedPlatformAssemblies(unityBaseLibrariesPath);

InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();

try
{
    Console.WriteLine($"Initializing Cpp2IL for Unity {unityVersion}...");
    Cpp2IlApi.InitializeLibCpp2Il(gameAssemblyPath, metadataPath, unityVersion);

    var processingLayers = new List<Cpp2IlProcessingLayer>
    {
        new AttributeInjectorProcessingLayer(),
    };

    foreach (var layer in processingLayers)
    {
        layer.PreProcess(Cpp2IlApi.CurrentAppContext, processingLayers);
    }

    foreach (var layer in processingLayers)
    {
        layer.Process(Cpp2IlApi.CurrentAppContext);
    }

    List<AssemblyDefinition> sourceAssemblies =
        new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);

    Console.WriteLine($"Generating interop assemblies from {sourceAssemblies.Count} source assemblies...");
    var options = new GeneratorOptions
    {
        GameAssemblyPath = gameAssemblyPath,
        Source = sourceAssemblies,
        OutputDir = outputPath,
        UnityBaseLibsDir = unityBaseLibrariesPath,
    };

    Il2CppInteropGenerator
        .Create(options)
        .AddLogger(NullLogger.Instance)
        .AddInteropAssemblyGenerator()
        .Run();

    var generatedCount = Directory.EnumerateFiles(outputPath, "*.dll", SearchOption.TopDirectoryOnly).Count();
    Console.WriteLine($"Generated {generatedCount} interop assemblies in {outputPath}");
    return generatedCount > 0 ? 0 : 1;
}
finally
{
    LibCpp2IlMain.Reset();
    Cpp2IlApi.CurrentAppContext = null;
}

static string RequireFile(string path, string label)
{
    var fullPath = Path.GetFullPath(path);
    return File.Exists(fullPath)
        ? fullPath
        : throw new FileNotFoundException($"{label} does not exist.", fullPath);
}

static string RequireDirectory(string path, string label)
{
    var fullPath = Path.GetFullPath(path);
    return Directory.Exists(fullPath)
        ? fullPath
        : throw new DirectoryNotFoundException($"{label} does not exist: {fullPath}");
}

static void AddTrustedPlatformAssemblies(string assemblyDirectory)
{
    var current = AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
    var additions = string.Join(
        Path.PathSeparator,
        Directory.EnumerateFiles(assemblyDirectory, "*.dll", SearchOption.TopDirectoryOnly));
    var combined = string.IsNullOrEmpty(current)
        ? additions
        : $"{current}{Path.PathSeparator}{additions}";
    AppDomain.CurrentDomain.SetData("TRUSTED_PLATFORM_ASSEMBLIES", combined);
}
