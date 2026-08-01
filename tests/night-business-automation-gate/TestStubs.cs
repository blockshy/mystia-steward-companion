namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public List<string> Info { get; } = new();
        public List<string> Warnings { get; } = new();

        public void LogInfo(object data) => Info.Add(data?.ToString() ?? "");
        public void LogWarning(object data) => Warnings.Add(data?.ToString() ?? "");
    }
}

namespace DEYU.Singletons
{
    public class MonoSingleton<T>
        where T : class
    {
        public static T? Instance { get; set; }
    }
}

namespace NightScene
{
    public sealed class NightSceneDirector : DEYU.Singletons.MonoSingleton<NightSceneDirector>
    {
        private bool _isInTutorial;

        public bool ThrowOnTutorialRead { get; set; }

        public bool IsInTutorial
        {
            get
            {
                if (ThrowOnTutorialRead) throw new InvalidOperationException("simulated native tutorial read failure");
                return _isInTutorial;
            }
            set => _isInTutorial = value;
        }
    }
}

namespace MystiaStewardCompanion.Save
{
    internal static class RuntimeReflectionUtility
    {
        public static bool DirectorTypeAvailable { get; set; }

        public static Type? FindType(string fullName)
        {
            return DirectorTypeAvailable
                && string.Equals(fullName, "NightScene.NightSceneDirector", StringComparison.Ordinal)
                ? typeof(NightScene.NightSceneDirector)
                : null;
        }
    }

    internal static class RuntimeNightBusinessLifecycle
    {
        public static NightBusinessLifecycleSnapshot Snapshot { get; set; } = new(
            0,
            0,
            NightBusinessLifecyclePhase.Inactive,
            "not started",
            DateTime.MinValue,
            0);

        public static long Generation => Snapshot.Generation;
    }
}
