using System.Diagnostics;
using System.Reflection;
using MystiaStewardCompanion.Plugin;

// 本宿主只启动自身的隔离子进程。所有 Steam 标记、API 参数与 token 都是明确的合成数据，
// 既不读取游戏进程的私有环境，也不启动 Steam、游戏、管家或真实更新程序。
var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

var gameKeys = new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId" };
var flags = new[] { "SteamNoOverlayUIDrawing" };
const string preservedKey = "MYSTIA_PROCESS_FIXTURE_PRESERVE";
const string preservedValue = "中文路径与空格 C:\\fixture folder\\data";
var fixtureArguments = new[]
{
    "--api=http://127.0.0.1:43210",
    "--game-pid=12345",
    "--token=synthetic fixture token 中文",
};

// 最末级子进程直接检查实际收到的环境块和 ArgumentList，验证不止停留在字典变更。
if (args.FirstOrDefault() == "--child")
{
    foreach (var key in gameKeys) Check(Environment.GetEnvironmentVariable(key) == null, "子进程不得继承游戏关联标记");
    foreach (var key in flags) Check(Environment.GetEnvironmentVariable(key) == "1", "子进程叠加层偏好必须显式禁用");
    Check(Environment.GetEnvironmentVariable(preservedKey) == preservedValue, "无关 Unicode 环境值必须保留");
    Check(args.Skip(1).SequenceEqual(fixtureArguments), "参数中空格和中文不得被 shell 拆分或改写");
    Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PATH")), "正常系统启动环境仍须可用");
    Console.WriteLine($"PASS: companion child environment {checks} assertions");
    return;
}

// 中间级进程模拟游戏已带 Steam 身份的实际继承条件。只在它的子进程描述中隔离，
// 随后确认自身环境逐项不变；并没有在测试主进程里临时 SetEnvironmentVariable。
if (args.FirstOrDefault() == "--parent-fixture")
{
    foreach (var key in gameKeys) Check(Environment.GetEnvironmentVariable(key) == "synthetic-game", "隔离父进程必须真实带有合成游戏标记");
    var before = gameKeys.Concat(flags).ToDictionary(key => key, Environment.GetEnvironmentVariable);
    var child = SelfStart(isolated: true);
    child.ArgumentList.Add("--child");
    foreach (var argument in fixtureArguments) child.ArgumentList.Add(argument);
    await Run(child);
    foreach (var (key, value) in before)
        Check(Environment.GetEnvironmentVariable(key) == value, "创建工具子进程不得修改父进程环境");
    Console.WriteLine($"PASS: companion isolated parent {checks} assertions");
    return;
}

var originalEnvironment = gameKeys.Concat(flags).ToDictionary(key => key, Environment.GetEnvironmentVariable);
var start = CompanionProcessEnvironment.CreateStartInfo("fixture.exe", "fixture directory");
Check(!start.UseShellExecute && !start.CreateNoWindow, "普通管家须继续直接启动且不改变窗口创建选项");
Check(start.FileName == "fixture.exe" && start.WorkingDirectory == "fixture directory", "环境策略不得改写启动目标或工作目录");
Check(CompanionProcessEnvironment.CreateStartInfo("updater.exe", "runner", createNoWindow: true).CreateNoWindow,
    "独立更新器保留原有无控制台启动选项");
foreach (var key in gameKeys) start.Environment[key.ToUpperInvariant()] = "synthetic-game";
foreach (var key in flags) start.Environment[key.ToLowerInvariant()] = "0";
start.Environment[preservedKey] = preservedValue;
start.Environment["SteamUnrelatedFixture"] = "unchanged";
foreach (var argument in fixtureArguments) start.ArgumentList.Add(argument);
var unrelated = start.Environment.Where(pair => !gameKeys.Concat(flags)
    .Contains(pair.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key, pair => pair.Value);

CompanionProcessEnvironment.Apply(start);
foreach (var key in gameKeys)
    Check(!start.Environment.Keys.Contains(key, StringComparer.OrdinalIgnoreCase), "游戏身份标记大小写变体必须全部移除");
foreach (var key in flags)
    Check(start.Environment[key] == "1" && start.Environment.Keys.Count(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase)) == 1,
        "叠加偏好必须只留下一个明确的规范值");
Check(unrelated.All(pair => start.Environment.TryGetValue(pair.Key, out var value) && value == pair.Value),
    "PATH、系统目录、临时目录及无关 Steam 字段不可被通配清空");
Check(start.ArgumentList.SequenceEqual(fixtureArguments), "局部环境处理不得改写连接参数");
var once = start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value);
CompanionProcessEnvironment.Apply(start);
Check(once.Count == start.Environment.Count && once.All(pair => start.Environment[pair.Key] == pair.Value),
    "重复应用策略必须幂等");
foreach (var (key, value) in originalEnvironment)
    Check(Environment.GetEnvironmentVariable(key) == value, "测试主进程环境必须保持不变");

var shell = new ProcessStartInfo("fixture.exe") { UseShellExecute = true };
var rejected = false;
try { CompanionProcessEnvironment.Apply(shell); }
catch (InvalidOperationException) { rejected = true; }
Check(rejected && shell.UseShellExecute, "ShellExecute 调用必须拒绝，不能静默绕过环境隔离");

var parent = SelfStart(isolated: false);
parent.ArgumentList.Add("--parent-fixture");
foreach (var key in gameKeys) parent.Environment[key] = "synthetic-game";
foreach (var key in flags) parent.Environment[key] = "0";
parent.Environment[preservedKey] = preservedValue;
await Run(parent);
Console.WriteLine($"PASS: companion process environment {checks} assertions; only managed fixture processes were started");

// 同一测试既可由 dotnet DLL 运行，也可由 apphost 运行。仅使用当前宿主和当前程序集的
// 确切路径，避免依赖 PATH 中的其它命令；Windows 测试窗口隐藏且退出时间有界。
ProcessStartInfo SelfStart(bool isolated)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("测试宿主路径不可用");
    var directory = AppContext.BaseDirectory;
    var info = isolated
        ? CompanionProcessEnvironment.CreateStartInfo(executable, directory, createNoWindow: true)
        : new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
    if (OperatingSystem.IsWindows()) info.WindowStyle = ProcessWindowStyle.Hidden;
    info.RedirectStandardOutput = true;
    info.RedirectStandardError = true;
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    return info;
}

async Task Run(ProcessStartInfo info)
{
    using var process = Process.Start(info) ?? throw new InvalidOperationException("隔离测试进程启动失败");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { await process.WaitForExitAsync(deadline.Token); }
    catch (OperationCanceledException)
    {
        // 只结束本测试刚创建的精确进程树，绝不按进程名称寻找或关闭游戏/正式客户端。
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        throw new InvalidOperationException("隔离环境测试超时");
    }
    Check(process.ExitCode == 0, "隔离进程环境或参数断言失败");
    Check((await error).Length == 0, "隔离进程不应输出错误");
    Console.Write(await output);
}
