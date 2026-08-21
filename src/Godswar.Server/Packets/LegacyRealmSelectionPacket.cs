using System.Text;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static class LegacyRealmSelectionPacket
{
    // All 26 retained client captures use this shape. Bytes other than the
    // selected ID include volatile client memory and must not be interpreted.
    public const int PacketLength = 44;
    public const int RealmIdOffset = 36;

    public static bool TryRead(
        GamePacket packet,
        out RealmId realmId)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Opcode != Opcodes.SelectServer ||
            packet.Length != PacketLength ||
            packet.Buffer.Length != PacketLength ||
            packet.Buffer[RealmIdOffset] == 0)
        {
            realmId = default;
            return false;
        }

        realmId = new RealmId(packet.Buffer[RealmIdOffset]);
        return true;
    }

    public static bool TryResolve(
        GamePacket packet,
        RealmCatalogSnapshot catalog,
        out RealmCatalogEntry? realm)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (TryRead(packet, out var realmId))
        {
            return catalog.TryFind(realmId, out realm);
        }

        realm = null;
        return false;
    }
}

internal sealed record LegacyGameLoginIdentity(
    string Username,
    string Identifier,
    RealmId RealmId);

internal static class LegacyGameLoginPacket
{
    public const int PacketLength = 62;
    public const int UsernameOffset = 4;
    public const int UsernameLength = 32;
    public const int IdentifierOffset = 36;
    public const int IdentifierLength = 25;
    public const int RealmIdOffset = 61;

    public static bool TryRead(
        GamePacket packet,
        out LegacyGameLoginIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Opcode != Opcodes.LoginGameServer ||
            packet.Length != PacketLength ||
            packet.Buffer.Length != PacketLength ||
            packet.Buffer[RealmIdOffset] == 0)
        {
            identity = null;
            return false;
        }

        var username = ReadNullTerminatedAscii(
            packet.Buffer.AsSpan(UsernameOffset, UsernameLength));
        var identifier = ReadNullTerminatedAscii(
            packet.Buffer.AsSpan(IdentifierOffset, IdentifierLength));
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(identifier))
        {
            identity = null;
            return false;
        }

        identity = new LegacyGameLoginIdentity(
            username,
            identifier,
            new RealmId(packet.Buffer[RealmIdOffset]));
        return true;
    }

    public static bool Matches(
        LegacyGameLoginIdentity identity,
        RealmCatalogEntry realm) =>
        identity.RealmId == realm.RealmId &&
        string.Equals(
            identity.Identifier,
            realm.Identifier,
            StringComparison.Ordinal);

    private static string ReadNullTerminatedAscii(
        ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        if (terminator >= 0)
        {
            field = field[..terminator];
        }

        return Encoding.ASCII.GetString(field);
    }
}
