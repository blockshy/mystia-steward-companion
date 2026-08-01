using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MystiaStewardCompanion.Save;

internal readonly record struct YuumaCookerTopologyPosition(int X, int Y, int Z);

internal readonly record struct YuumaCookerTopologyControllerIdentity(
    int ControllerIndex,
    string ControllerIdentity,
    YuumaCookerTopologyPosition GridPosition);

internal sealed record YuumaCookerTopologySnapshotIdentity(
    string Signature,
    int ControllerCount,
    int LockedControllerCount);

internal static class YuumaCookerTopologySnapshotIdentityBuilder
{
    private const int SignatureVersion = 1;

    public static bool TryCreate(
        IReadOnlyList<YuumaCookerTopologyControllerIdentity> controllers,
        IReadOnlyCollection<YuumaCookerTopologyPosition> lockedPositions,
        out YuumaCookerTopologySnapshotIdentity snapshot,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        ArgumentNullException.ThrowIfNull(lockedPositions);
        snapshot = null!;

        var orderedControllers = controllers
            .OrderBy(controller => controller.ControllerIndex)
            .ToArray();
        var knownPositions = new HashSet<YuumaCookerTopologyPosition>();
        var knownIdentities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < orderedControllers.Length; index++)
        {
            var controller = orderedControllers[index];
            if (controller.ControllerIndex != index
                || !IsCanonicalControllerIdentity(controller.ControllerIdentity)
                || !knownPositions.Add(controller.GridPosition)
                || !knownIdentities.Add(controller.ControllerIdentity))
            {
                diagnostic = $"controller-identity-invalid; index={index}; actual={controller.ControllerIndex}; "
                    + $"identity={controller.ControllerIdentity}; position={controller.GridPosition}";
                return false;
            }
        }

        var orderedLockedPositions = lockedPositions
            .Distinct()
            .OrderBy(position => position.X)
            .ThenBy(position => position.Y)
            .ThenBy(position => position.Z)
            .ToArray();
        if (orderedLockedPositions.Length != lockedPositions.Count
            || orderedLockedPositions.Any(position => !knownPositions.Contains(position)))
        {
            diagnostic = "locked-cooker-position-not-in-current-controller-directory";
            return false;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, SignatureVersion);
        AppendInt32(hash, orderedControllers.Length);
        foreach (var controller in orderedControllers)
        {
            AppendInt32(hash, controller.ControllerIndex);
            AppendString(hash, controller.ControllerIdentity);
            AppendPosition(hash, controller.GridPosition);
        }

        AppendInt32(hash, orderedLockedPositions.Length);
        foreach (var position in orderedLockedPositions)
        {
            AppendPosition(hash, position);
        }

        snapshot = new YuumaCookerTopologySnapshotIdentity(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            orderedControllers.Length,
            orderedLockedPositions.Length);
        diagnostic = $"topology-snapshot-complete; controllers={snapshot.ControllerCount}; "
            + $"locked={snapshot.LockedControllerCount}; signature={snapshot.Signature}";
        return true;
    }

    private static void AppendPosition(IncrementalHash hash, YuumaCookerTopologyPosition position)
    {
        AppendInt32(hash, position.X);
        AppendInt32(hash, position.Y);
        AppendInt32(hash, position.Z);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static bool IsCanonicalControllerIdentity(string value)
    {
        if (value.Length <= 2 || value[0] != '0' || value[1] != 'x') return false;

        var nonZero = false;
        for (var index = 2; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= '1' and <= '9' or >= 'A' and <= 'F')
            {
                nonZero = true;
                continue;
            }

            if (character != '0') return false;
        }

        return nonZero;
    }
}
