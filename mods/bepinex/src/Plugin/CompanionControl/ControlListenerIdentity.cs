using System.Buffers.Binary;

namespace MystiaStewardCompanion.Plugin.CompanionControl;

// MIB_TCP{,6}TABLE_OWNER_PID with TCP_TABLE_OWNER_PID_LISTENER, not a TCP connection or a payload handshake.
internal static class ControlListenerIdentity
{
    internal static uint Resolve(ReadOnlySpan<byte> ipv4, ReadOnlySpan<byte> ipv6)
    {
        var count4 = Count(ipv4, 24);
        uint candidate = 0;
        for (var i = 0; i < count4; ++i)
        {
            var row = ipv4.Slice(4 + i * 24, 24);
            if (BinaryPrimitives.ReadUInt16BigEndian(row[8..]) != 32146) continue;
            ControlFrame.Require(row[4] == 127 && row[5] == 0 && row[6] == 0 && row[7] == 1, "listener_not_loopback");
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(row[20..]);
            ControlFrame.Require(candidate == 0 && pid != 0 && BinaryPrimitives.ReadUInt32LittleEndian(row) == 2, "listener_ambiguous");
            candidate = pid;
        }
        var count6 = Count(ipv6, 56);
        for (var i = 0; i < count6; ++i)
            ControlFrame.Require(BinaryPrimitives.ReadUInt16BigEndian(ipv6.Slice(4 + i * 56 + 20)) != 32146, "listener_ipv6_ambiguous");
        return candidate;
    }
    private static int Count(ReadOnlySpan<byte> table, int rowSize)
    {
        ControlFrame.Require(table.Length >= 4, "tcp_table_bounds");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        ControlFrame.Require(count <= (table.Length - 4) / rowSize, "tcp_table_bounds");
        return checked((int)count);
    }
}
