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
