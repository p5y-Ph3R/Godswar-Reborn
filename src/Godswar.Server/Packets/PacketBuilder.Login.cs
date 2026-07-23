using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] ServerList()
    {
        return ReferencePackets.ServerList.ToArray();
    }

    public static byte[] SendServer()
    {
        return ReferencePackets.SendServer.ToArray();
    }

    public static byte[] BlankUser()
    {
        return ReferencePackets.BlankUser.ToArray();
    }

    public static byte[] AfterLogin()
    {
        const int recordLength = 44;
        var packet = new byte[AfterLoginManifest.Length * recordLength];

        for (var recordIndex = 0; recordIndex < AfterLoginManifest.Length; recordIndex++)
        {
            var record = packet.AsSpan(recordIndex * recordLength, recordLength);
            var (id, hash) = AfterLoginManifest[recordIndex];
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(0, 2), recordLength);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(2, 2), AfterLoginOpcode);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(4, 4), id);
            PacketText.WriteFixedAscii(record.Slice(8, 32), hash);
            record[40] = 0;
            record[41] = (byte)'8';
            record[42] = (byte)'8';
            record[43] = 0;
        }

        return packet;
    }

    public static byte[] ServerTime()
    {
        return ServerTime(DateTimeOffset.UtcNow);
    }

    public static byte[] ServerTime(DateTimeOffset now)
    {
        var unixSeconds = now.ToUnixTimeSeconds();
        if (unixSeconds is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        // Working-server captures use a fixed UTC-8 game-server offset even
        // during daylight-saving months, followed by the current Unix time.
        var packet = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.ServerTimeRequest);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), OriginalServerUtcOffsetSeconds);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), (uint)unixSeconds);
        return packet;
    }

    public static byte[] EnterPart2Unknown()
    {
        return ReferencePackets.EnterPart2Unknown.ToArray();
    }

    public static byte[] EnterPart2()
    {
        return ReferencePackets.EnterPart2.ToArray();
    }

    public static byte[] EnterPart4()
    {
        return ReferencePackets.EnterPart4.ToArray();
    }

    public static byte[] LoginFailed(ushort reason)
    {
        return
        [
            0x06, 0x00,
            (byte)(reason & 0xFF), (byte)(reason >> 8),
            0x00, 0x00,
            0xF0
        ];
    }

    public static byte[] GameServerRedirect(string host, int port)
    {
        var packet = ReferencePackets.NewGameServerTemplate.ToArray();
        PacketText.WriteFixedAscii(packet.AsSpan(5, Math.Min(23, packet.Length - 5)), host);
        if (packet.Length >= 44)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(40, 4), port);
        }

        return packet;
    }

    public static byte[] CreateRoleSuccess()
    {
        return [0x0C, 0x00, 0xB4, 0x27, 0x13, 0x27, 0x8D, 0x0B, 0x01, 0x00, 0x00, 0x00];
    }

    public static byte[] DeleteRoleSuccess()
    {
        return [0x0C, 0x00, 0xB4, 0x27, 0x14, 0x27, 0xA4, 0x75, 0x08, 0x00, 0x00, 0x00];
    }
}
