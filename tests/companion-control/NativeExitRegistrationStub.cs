namespace MystiaStewardCompanion.Plugin.CompanionControl;

// Portable smoke only: no Unity runtime or native detour is installed here.
// A prepared session must fail this dependency, exercising its real terminal
// path without accidentally opening a process, starting a client, or a pipe.
internal static class NativeExitRegistration
{
    internal static void EnsureRegistered(uint thread) => throw new ControlFailure("synthetic_native_registration_failure");
}
