namespace MystiaStewardCompanion.Save;

// RuntimeOrderKind normally lives with the IL2CPP type resolver. Keep this smoke pure managed by
// compiling only the exact scalar domain required by participation-state authorization.
internal enum RuntimeOrderKind
{
    Unknown,
    Normal,
    Special,
}
