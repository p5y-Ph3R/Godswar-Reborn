using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Realms;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int RealmListHeaderLength = 6;
    private const int RealmListRecordLength = 48;
    private const int RealmListRecordNameOffset = 4;
    private const int RealmListRecordNameLength = 36;
    private const int RealmListRecordIdOffset = 40;
    private const int RealmListRecordRecommendedOffset = 41;
    private const int RealmListRecordTerminalOffset = 46;
    private const int SendServerNameOffset = 36;
    private const int SendServerNameLength = 36;
    private const int SendServerRealmIdOffset = 72;
    private const int RedirectHostOffset = 5;
    private const int RedirectHostLength = 23;
    private const int RedirectPortOffset = 40;
    private const int RedirectIdentifierOffset = 45;
    private const int RedirectIdentifierLength = 25;

    public static byte[] ServerList()
    {
        return ReferencePackets.ServerList.ToArray();
    }

    public static byte[] ServerList(RealmCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var packet = new byte[
            RealmListHeaderLength +
            catalog.Entries.Length * RealmListRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            RealmListHeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.ServerList);
        // Opcode 3 carries a login-result status, not a realm count. The
        // stock client dispatches status 1 as success and status 2 as
        // AccountUnuse before it consumes the following realm records.
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(4, 2),
            1);

        for (var index = 0; index < catalog.Entries.Length; index++)
        {
            var realm = catalog.Entries[index];
            var record = packet.AsSpan(
                RealmListHeaderLength + index * RealmListRecordLength,
                RealmListRecordLength);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record[..2],
                RealmListRecordLength);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.Slice(2, 2),
                Opcodes.GameServerInfo);
            PacketText.WriteFixedAscii(
                record.Slice(
                    RealmListRecordNameOffset,
                    RealmListRecordNameLength),
                realm.Name);
            record[RealmListRecordIdOffset] = realm.LegacyWireId;
            record[RealmListRecordRecommendedOffset] =
                realm.Recommended ? (byte)1 : (byte)0;

            // Preserve the client-compatible captured suffix, then encode the
            // one evidenced field within it. The stock client treats byte 46
            // as a boolean end-of-list marker and finalizes the realm list
            // only when it is non-zero.
            ReferencePackets.ServerList
                .Slice(48, 6)
                .CopyTo(record[42..]);
            record[RealmListRecordTerminalOffset] =
                index == catalog.Entries.Length - 1 ? (byte)1 : (byte)0;
        }

        return packet;
    }

    public static byte[] SendServer()
    {
        return ReferencePackets.SendServer.ToArray();
    }

    public static byte[] SendServer(RealmCatalogEntry realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var packet = ReferencePackets.SendServer.ToArray();
        PacketText.WriteFixedAscii(
            packet.AsSpan(SendServerNameOffset, SendServerNameLength),
            realm.Name);
        packet[SendServerRealmIdOffset] = realm.LegacyWireId;
        return packet;
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

    public static byte[] ServerTime(RealmCalendar realmCalendar) =>
        ServerTime(realmCalendar, DateTimeOffset.UtcNow);

    public static byte[] ServerTime(
        RealmCalendar realmCalendar,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(realmCalendar);
        var unixSeconds = now.ToUnixTimeSeconds();
        if (unixSeconds is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        var realmUtcOffsetSeconds = checked((int)
            realmCalendar.GetUtcOffset(now).TotalSeconds);
        // Origin converts the packet clock as Unix seconds minus this field,
        // so the native wire value is UTC minus local time (the bias), not
        // the conventional local-minus-UTC offset.
        var nativeUtcBiasSeconds = checked(-realmUtcOffsetSeconds);
        var packet = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.ServerTimeRequest);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4, 4),
            nativeUtcBiasSeconds);
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

    public static byte[] GameServerRedirect(RealmCatalogEntry realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var packet = ReferencePackets.NewGameServerTemplate.ToArray();
        PacketText.WriteFixedAscii(
            packet.AsSpan(RedirectHostOffset, RedirectHostLength),
            realm.Host);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(RedirectPortOffset, 4),
            realm.GamePort);
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                RedirectIdentifierOffset,
                RedirectIdentifierLength),
            realm.Identifier);
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
