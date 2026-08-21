using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Protocol;

namespace Godswar.Server.LegacyLoginProbe;

internal enum LegacyProbeMode : byte
{
    Game = 0,
    LoginRouting = 1
}

internal sealed record LoginRoutingExpectedRealm(
    byte RealmId,
    string Name,
    bool Recommended);

internal sealed record LoginRoutingProbeOptions(
    string Label,
    IPAddress Address,
    int LoginPort,
    string Username,
    string Password,
    byte SelectedRealmId,
    string SelectedRealmIdentifier,
    string ExpectedGameHost,
    int ExpectedGamePort,
    IReadOnlyList<LoginRoutingExpectedRealm> ExpectedRealms);

internal static class LoginRoutingProbe
{
    private const int MaximumRealmCount = 16;

    public static async Task<LoginRoutingProbeResult> RunAsync(
        LoginRoutingProbeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var packets = new List<LoginRoutingPacketRecord>(
            options.ExpectedRealms.Count + 3);
        await using var peer = await LegacyProbePeer.ConnectAsync(
            options.Address,
            options.LoginPort,
            cancellationToken);

        var login = ProbePackets.Login(
            options.Username,
            options.Password);
        try
        {
            await peer.SendAsync(login, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(login);
        }

        var header = await ReadAsync(
            peer,
            packets,
            "server-list-header",
            cancellationToken);
        RequirePacket(
            header,
            expectedLength: 6,
            Opcodes.ServerList,
            "server-list header");
        var loginStatus = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(4, 2));
        if (loginStatus != 1)
        {
            throw new InvalidDataException(
                $"Server returned login status {loginStatus}; expected success status 1.");
        }

        var advertised = new List<LoginRoutingRealmRecord>(
            options.ExpectedRealms.Count);
        for (var index = 0;
             index < options.ExpectedRealms.Count;
             index++)
        {
            var packet = await ReadAsync(
                peer,
                packets,
                "server-list-realm",
                cancellationToken);
            RequirePacket(
                packet,
                expectedLength: 48,
                Opcodes.GameServerInfo,
                $"server-list realm {index}");
            var expectedTerminal =
                index == options.ExpectedRealms.Count - 1 ? (byte)1 : (byte)0;
            if (packet[46] != expectedTerminal)
            {
                throw new InvalidDataException(
                    $"Advertised realm {index} carried terminal marker " +
                    $"{packet[46]}; expected {expectedTerminal}.");
            }

            var realm = new LoginRoutingRealmRecord(
                packet[40],
                ReadFixedAscii(packet.AsSpan(4, 36)),
                ReadBooleanByte(packet[41], $"realm {index} recommendation"));
            var expected = options.ExpectedRealms[index];
            if (realm.RealmId != expected.RealmId ||
                !string.Equals(
                    realm.Name,
                    expected.Name,
                    StringComparison.Ordinal) ||
                realm.Recommended != expected.Recommended)
            {
                throw new InvalidDataException(
                    $"Advertised realm {index} did not match " +
                    $"{expected.RealmId}:{expected.Name}:" +
                    $"{expected.Recommended}.");
            }

            advertised.Add(realm);
        }

        await peer.SendAsync(
            ProbePackets.SelectServer(options.SelectedRealmId),
            cancellationToken);
        var sendServerPacket = await ReadAsync(
            peer,
            packets,
            "selected-realm",
            cancellationToken);
        RequirePacket(
            sendServerPacket,
            expectedLength: 84,
            Opcodes.SendServer,
            "selected realm");
        var selected = new LoginRoutingSelectedRealm(
            sendServerPacket[72],
            ReadFixedAscii(sendServerPacket.AsSpan(36, 36)));
        var expectedSelected = options.ExpectedRealms.Single(
            realm => realm.RealmId == options.SelectedRealmId);
        if (selected.RealmId != expectedSelected.RealmId ||
            !string.Equals(
                selected.Name,
                expectedSelected.Name,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "SendServer did not confirm the requested realm.");
        }

        await peer.SendAsync(
            ProbePackets.LoginReturnInfo(),
            cancellationToken);
        var redirectPacket = await ReadAsync(
            peer,
            packets,
            "game-redirect",
            cancellationToken);
        RequirePacket(
            redirectPacket,
            expectedLength: 72,
            Opcodes.ResponseGameServer,
            "game redirect");
        var redirect = new LoginRoutingRedirect(
            ReadFixedAscii(redirectPacket.AsSpan(5, 23)),
            BinaryPrimitives.ReadInt32LittleEndian(
                redirectPacket.AsSpan(40, 4)),
            ReadFixedAscii(redirectPacket.AsSpan(45, 25)));
        if (!string.Equals(
                redirect.Host,
                options.ExpectedGameHost,
                StringComparison.Ordinal) ||
            redirect.GamePort != options.ExpectedGamePort ||
            !string.Equals(
                redirect.RealmIdentifier,
                options.SelectedRealmIdentifier,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Game redirect did not match the selected realm route.");
        }

        return new LoginRoutingProbeResult(
            options.Label,
            "login-routing",
            options.Address.ToString(),
            options.LoginPort,
            options.Username,
            DateTimeOffset.UtcNow,
            advertised,
            selected,
            redirect,
            packets);
    }

    private static void Validate(LoginRoutingProbeOptions options)
    {
        if (options.ExpectedRealms.Count is < 1 or > MaximumRealmCount)
        {
            throw new ArgumentException(
                $"Login routing expects 1..{MaximumRealmCount} realms.");
        }
        if (options.ExpectedRealms.Any(realm => realm.RealmId == 0) ||
            options.ExpectedRealms
                .Select(realm => realm.RealmId)
                .Distinct()
                .Count() != options.ExpectedRealms.Count)
        {
            throw new ArgumentException(
                "Expected realm IDs must be non-zero and unique.");
        }
        if (options.ExpectedRealms.Count(
                realm => realm.RealmId == options.SelectedRealmId) != 1)
        {
            throw new ArgumentException(
                "The selected realm must appear exactly once in the expected list.");
        }
    }

    private static async Task<byte[]> ReadAsync(
        LegacyProbePeer peer,
        List<LoginRoutingPacketRecord> packets,
        string phase,
        CancellationToken cancellationToken)
    {
        if (packets.Count >= MaximumRealmCount + 3)
        {
            throw new InvalidDataException(
                "Login routing response exceeded its packet bound.");
        }

        var packet = await peer.ReadAsync(cancellationToken);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, 2));
        packets.Add(new LoginRoutingPacketRecord(
            packets.Count,
            phase,
            opcode,
            Opcodes.Name(opcode),
            packet.Length,
            Convert.ToHexString(SHA256.HashData(packet)),
            Convert.ToBase64String(packet)));
        return packet;
    }

    private static void RequirePacket(
        byte[] packet,
        int expectedLength,
        ushort expectedOpcode,
        string description)
    {
        if (packet.Length != expectedLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2, 2)) != expectedOpcode)
        {
            throw new InvalidDataException(
                $"The {description} was not opcode {expectedOpcode} " +
                $"with length {expectedLength}.");
        }
    }

    private static bool ReadBooleanByte(byte value, string description) =>
        value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException(
                $"The {description} was not zero or one.")
        };

    private static string ReadFixedAscii(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        if (terminator >= 0)
        {
            field = field[..terminator];
        }

        return Encoding.ASCII.GetString(field);
    }
}

internal sealed record LoginRoutingProbeResult(
    string Label,
    string Mode,
    string Host,
    int LoginPort,
    string Username,
    DateTimeOffset CompletedAt,
    IReadOnlyList<LoginRoutingRealmRecord> AdvertisedRealms,
    LoginRoutingSelectedRealm SelectedRealm,
    LoginRoutingRedirect Redirect,
    IReadOnlyList<LoginRoutingPacketRecord> Packets);

internal sealed record LoginRoutingRealmRecord(
    byte RealmId,
    string Name,
    bool Recommended);

internal sealed record LoginRoutingSelectedRealm(
    byte RealmId,
    string Name);

internal sealed record LoginRoutingRedirect(
    string Host,
    int GamePort,
    string RealmIdentifier);

internal sealed record LoginRoutingPacketRecord(
    int Index,
    string Phase,
    ushort Opcode,
    string Name,
    int Length,
    string Sha256,
    string ClearBytesBase64);
