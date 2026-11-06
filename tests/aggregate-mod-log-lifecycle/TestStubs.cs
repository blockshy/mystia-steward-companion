namespace BepInEx
{
    internal static class Paths
    {
        public static string ConfigPath { get; set; } = Path.GetTempPath();
    }
}

namespace BepInEx.Logging
{
    [Flags]
    internal enum LogLevel
    {
        None = 0,
        Fatal = 1,
        Error = 2,
        Warning = 4,
        Message = 8,
        Info = 16,
        Debug = 32,
        All = Fatal | Error | Warning | Message | Info | Debug,
    }

    internal interface ILogSource
    {
        string SourceName { get; }
    }

    internal interface ILogListener : IDisposable
    {
        LogLevel LogLevelFilter { get; }

        void LogEvent(object sender, LogEventArgs eventArgs);
    }

    internal sealed class LogEventArgs : EventArgs
    {
        public LogEventArgs(object? data, LogLevel level, ILogSource? source)
        {
            Data = data;
            Level = level;
            Source = source;
        }

        public object? Data { get; }

        public LogLevel Level { get; }

        public ILogSource? Source { get; }
    }

    internal sealed class TestLogSource : ILogSource
    {
        public TestLogSource(string sourceName)
        {
            SourceName = sourceName;
        }

        public string SourceName { get; }
    }

    /// <summary>
    /// 在当前线程同步分发合成日志，验证监听器注册、解绑和文件生命周期。
    /// 对监听器集合取副本只为允许回调期间变更注册，不代表真实 BepInEx 的线程调度。
    /// </summary>
    internal static class Logger
    {
        public static ICollection<ILogListener> Listeners { get; } = new List<ILogListener>();

        public static void Emit(string message)
        {
            var eventArgs = new LogEventArgs(message, LogLevel.Info, new TestLogSource("smoke"));
            foreach (var listener in Listeners.ToArray())
            {
                listener.LogEvent(typeof(Logger), eventArgs);
            }
        }
    }
}

namespace MystiaStewardCompanion.Save
{
    /// <summary>只记录诊断缓存重置次数，避免日志生命周期测试依赖实际游戏目录。</summary>
    internal static class RuntimeStaticDataDiagnosticFormatter
    {
        public static int ResetCount { get; private set; }

        public static void Reset()
        {
            ResetCount++;
        }
    }

    internal static class SpecialBusinessDiagnostics
    {
        public static int ResetCount { get; private set; }

        public static void Reset()
        {
            ResetCount++;
        }
    }
}
