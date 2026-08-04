namespace MystiaStewardCompanion.Save;

// RuntimeOrderKind normally lives with the IL2CPP type resolver. Keep this smoke test pure managed
// by declaring the same canonical closed set instead of linking reflection and game-runtime helpers.
internal enum RuntimeOrderKind
{
    Normal,
    Special,
}
