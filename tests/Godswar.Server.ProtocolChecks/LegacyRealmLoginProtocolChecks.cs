using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Realms;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class LegacyRealmLoginProtocolChecks
{
    public const string CheckName =
        "Bounded multi-realm legacy login protocol";

    public static async Task RunAsync()
    {
        var tempest = Entry(
            RealmId.Tempest,
            "Tempest",
            "KAL3jcIzqGgKvOf1dbYZKC8cS",
            "127.1.1.110",
            recommended: true,
            displayOrder: 1);
        var dwargon = Entry(
            RealmId.Dwargon,
            "Dwargon",
            "DWG3jcIzqGgKvOf1dbYZKC8cS",
            "127.1.1.111",
            recommended: false,
            displayOrder: 2);
        var catalog = new RealmCatalogSnapshot([dwargon, tempest]);

        CheckCatalogContract(catalog, tempest, dwargon);
        CheckServerList(catalog);
        CheckSendServer(dwargon);
        CheckRedirect(dwargon);
        CheckSelectServer(catalog, dwargon);
        CheckGameLogin(dwargon);
        CheckPostgresProjection();
        CheckBounds(tempest);
        await CheckRawHandlerFlowAsync(tempest, dwargon, catalog);
    }

    private static void CheckCatalogContract(
        RealmCatalogSnapshot catalog,
        RealmCatalogEntry tempest,
        RealmCatalogEntry dwargon)
    {
        Check.Equal(2, catalog.Entries.Length, "enabled realm count");
        Check.True(
            ReferenceEquals(tempest, catalog.Entries[0]) &&
            ReferenceEquals(dwargon, catalog.Entries[1]),
            "catalog ordering is display order then durable realm ID");
        Check.True(
            catalog.TryFind(RealmId.Dwargon, out var selected) &&
            ReferenceEquals(dwargon, selected),
            "catalog resolves durable realm identity");
        Check.True(
            !catalog.TryFind(new RealmId(3), out _),
            "catalog rejects an unadvertised realm");
    }

    private static void CheckServerList(RealmCatalogSnapshot catalog)
    {
        var packet = PacketBuilder.ServerList(catalog);
        Check.Equal(102, packet.Length, "two-realm list stream length");
        Check.Equal(
            (ushort)6,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "realm-list header frame length");
        Check.Equal(
            Opcodes.ServerList,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "realm-list header opcode");
        Check.Equal(
            (ushort)1,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)),
            "realm-list login success status");

        CheckRealmRecord(
            packet,
            6,
            "Tempest",
            1,
            recommended: true,
            terminal: false);
        CheckRealmRecord(
            packet,
            54,
            "Dwargon",
            2,
            recommended: false,
            terminal: true);

        var singleRealm = PacketBuilder.ServerList(
            new RealmCatalogSnapshot([catalog.Entries[0]]));
        Check.Equal(54, singleRealm.Length, "single-realm list stream length");
        CheckRealmRecord(
            singleRealm,
            6,
            "Tempest",
            1,
            recommended: true,
            terminal: true);

        var third = Entry(
            new RealmId(3),
            "Olympus",
            "OLY3jcIzqGgKvOf1dbYZKC8cS",
            "127.1.1.112",
            recommended: false,
            displayOrder: 3);
        var threeRealm = PacketBuilder.ServerList(
            new RealmCatalogSnapshot(catalog.Entries.Append(third)));
        Check.Equal(150, threeRealm.Length, "three-realm list stream length");
        CheckRealmRecord(
            threeRealm,
            6,
            "Tempest",
            1,
            recommended: true,
            terminal: false);
        CheckRealmRecord(
            threeRealm,
            54,
            "Dwargon",
            2,
            recommended: false,
            terminal: false);
        CheckRealmRecord(
            threeRealm,
            102,
            "Olympus",
            3,
            recommended: false,
            terminal: true);

        var empty = PacketBuilder.ServerList(new RealmCatalogSnapshot([]));
        Check.True(
            empty.SequenceEqual(
                new byte[] { 6, 0, 3, 0, 1, 0 }),
            "empty enabled catalog preserves the captured success frame");
        Check.True(
            PacketBuilder.ServerList().SequenceEqual(
                ReferencePackets.ServerList.ToArray()),
            "legacy no-argument server list remains byte-identical");
    }

    private static void CheckRealmRecord(
        byte[] stream,
        int offset,
        string expectedName,
        byte expectedRealmId,
        bool recommended,
        bool terminal)
    {
        var record = stream.AsSpan(offset, 48);
        Check.Equal(
            (ushort)48,
            BinaryPrimitives.ReadUInt16LittleEndian(record),
            $"{expectedName} record length");
        Check.Equal(
            Opcodes.GameServerInfo,
            BinaryPrimitives.ReadUInt16LittleEndian(record[2..]),
            $"{expectedName} record opcode");
        Check.Equal(
            expectedName,
            ReadAscii(record.Slice(4, 36)),
            $"{expectedName} display name");
        Check.Equal(
            expectedRealmId,
            record[40],
            $"{expectedName} one-byte realm ID");
        Check.Equal(
            recommended ? (byte)1 : (byte)0,
            record[41],
            $"{expectedName} recommendation flag");
        var expectedSuffix =
            ReferencePackets.ServerList.Slice(48, 6).ToArray();
        expectedSuffix[4] = terminal ? (byte)1 : (byte)0;
        Check.True(
            record[42..].SequenceEqual(expectedSuffix),
            $"{expectedName} preserves the suffix and terminal marker");
    }

    private static void CheckSendServer(RealmCatalogEntry dwargon)
    {
        var packet = PacketBuilder.SendServer(dwargon);
        Check.Equal(84, packet.Length, "SendServer packet length");
        Check.Equal(
            Opcodes.SendServer,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "SendServer opcode");
        Check.Equal(
            "Dwargon",
            ReadAscii(packet.AsSpan(36, 36)),
            "SendServer selected realm name");
        Check.Equal(
            (byte)2,
            packet[72],
            "SendServer selected one-byte realm ID");
    }

    private static void CheckRedirect(RealmCatalogEntry dwargon)
    {
        var packet = PacketBuilder.GameServerRedirect(dwargon);
        Check.Equal(72, packet.Length, "realm redirect length");
        Check.Equal(
            "127.1.1.111",
            ReadAscii(packet.AsSpan(5, 23)),
            "realm redirect host");
        Check.Equal(
            7_000,
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(40)),
            "realm redirect port");
        Check.Equal(
            dwargon.Identifier,
            ReadAscii(packet.AsSpan(45, 25)),
            "realm redirect fixed routing identifier");
    }

    private static void CheckSelectServer(
        RealmCatalogSnapshot catalog,
        RealmCatalogEntry dwargon)
    {
        var capturedTempest = new GamePacket(Convert.FromHexString(
            "2C000400746573743200E40E2F000000300000000000000000000000" +
            "0F00000000000000017E3D8200000000"));
        Check.True(
            LegacyRealmSelectionPacket.TryRead(
                capturedTempest,
                out var tempestId) &&
            tempestId == RealmId.Tempest,
            "captured stock-client selection reads only byte 36");

        var selection = new byte[LegacyRealmSelectionPacket.PacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            selection,
            LegacyRealmSelectionPacket.PacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            selection.AsSpan(2),
            Opcodes.SelectServer);
        selection.AsSpan(4).Fill(0xA5);
        selection[LegacyRealmSelectionPacket.RealmIdOffset] = 2;
        var packet = new GamePacket(selection);
        Check.True(
            LegacyRealmSelectionPacket.TryResolve(
                packet,
                catalog,
                out var selected) &&
            ReferenceEquals(dwargon, selected),
            "selection resolves only an advertised realm");

        selection[LegacyRealmSelectionPacket.RealmIdOffset] = 3;
        Check.True(
            !LegacyRealmSelectionPacket.TryResolve(
                new GamePacket(selection),
                catalog,
                out _),
            "unadvertised realm selection fails closed");
        selection[LegacyRealmSelectionPacket.RealmIdOffset] = 0;
        Check.True(
            !LegacyRealmSelectionPacket.TryRead(
                new GamePacket(selection),
                out _),
            "zero realm selection fails closed");
    }

    private static void CheckGameLogin(RealmCatalogEntry dwargon)
    {
        var bytes = new byte[LegacyGameLoginPacket.PacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            LegacyGameLoginPacket.PacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.LoginGameServer);
        Encoding.ASCII.GetBytes(
            "test2",
            bytes.AsSpan(
                LegacyGameLoginPacket.UsernameOffset,
                LegacyGameLoginPacket.UsernameLength));
        Encoding.ASCII.GetBytes(
            dwargon.Identifier,
            bytes.AsSpan(
                LegacyGameLoginPacket.IdentifierOffset,
                LegacyGameLoginPacket.IdentifierLength));
        bytes[LegacyGameLoginPacket.RealmIdOffset] = 2;

        Check.True(
            LegacyGameLoginPacket.TryRead(
                new GamePacket(bytes),
                out var identity) &&
            identity is not null &&
            identity.Username == "test2" &&
            identity.RealmId == RealmId.Dwargon &&
            LegacyGameLoginPacket.Matches(identity, dwargon),
            "game login binds copied identifier and realm ID");

        bytes[LegacyGameLoginPacket.RealmIdOffset] = 1;
        Check.True(
            LegacyGameLoginPacket.TryRead(
                new GamePacket(bytes),
                out identity) &&
            identity is not null &&
            !LegacyGameLoginPacket.Matches(identity, dwargon),
            "game login rejects a token/realm mismatch");
    }

    private static void CheckPostgresProjection()
    {
        var query = PostgresRealmCatalogReader.EnabledRealmQuery;
        Check.True(
            query.Contains("WHERE enabled", StringComparison.Ordinal) &&
            query.Contains(
                "ORDER BY display_order, id",
                StringComparison.Ordinal) &&
            query.Contains("LIMIT @rowLimit", StringComparison.Ordinal),
            "PostgreSQL projection is enabled-only, deterministic, and bounded");
    }

    private static void CheckBounds(RealmCatalogEntry tempest)
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => Entry(
                new RealmId(256),
                "TooFar",
                "BAD3jcIzqGgKvOf1dbYZKC8cS",
                "127.0.0.1",
                false,
                3),
            "realm IDs outside the client byte fail");
        Check.Throws<InvalidDataException>(
            () => new RealmCatalogSnapshot([tempest, tempest]),
            "duplicate realm catalog identities fail");

        var excessive = Enumerable.Range(
                1,
                RealmCatalogSnapshot.MaximumEntries + 1)
            .Select(index => Entry(
                new RealmId(index),
                $"Realm{index}",
                $"R{index:D2}" + new string('x', 22),
                "127.0.0.1",
                false,
                index));
        Check.Throws<InvalidDataException>(
            () => new RealmCatalogSnapshot(excessive),
            "realm catalog row count remains bounded");
    }

    private static RealmCatalogEntry Entry(
        RealmId id,
        string name,
        string identifier,
        string host,
        bool recommended,
        int displayOrder) =>
        new(
            id,
            name,
            identifier,
            host,
            gamePort: 7_000,
            serverLimit: 250,
            recommended,
            displayOrder);

    private static string ReadAscii(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        if (terminator >= 0)
        {
            field = field[..terminator];
        }

        return Encoding.ASCII.GetString(field);
    }

    private static byte[] LoginPacket()
    {
        var packet = new byte[68];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 68);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Login);
        PacketText.WriteFixedAscii(packet.AsSpan(4, 32), "test2");
        PacketText.WriteFixedAscii(packet.AsSpan(36, 32), "password");
        return packet;
    }

    private static byte[] SelectionPacket(byte realmId)
    {
        var packet = new byte[LegacyRealmSelectionPacket.PacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            LegacyRealmSelectionPacket.PacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.SelectServer);
        packet[LegacyRealmSelectionPacket.RealmIdOffset] = realmId;
        return packet;
    }

    private static byte[] OpcodePacket(ushort opcode)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        return packet;
    }

    private static byte[] Encrypt(byte[] clear)
    {
        var encrypted = (byte[])clear.Clone();
        new PacketCipher().Transform(encrypted);
        return encrypted;
    }

    private static ServerOptions LocalOptions() =>
        new()
        {
            RuntimeProfile = "LocalDevelopment",
            Storage = new StorageOptions
            {
                Provider = "Postgres",
                PostgresConnectionString =
                    "Host=127.0.0.1;Database=realm-login-check"
            },
            Authentication = new AuthenticationOptions
            {
                AllowLegacyRawAuthentication = true
            }
        };

    private sealed class FixedRealmCatalogReader(
        RealmCatalogSnapshot snapshot) : IRealmCatalogReader
    {
        public int ReadCount { get; private set; }

        public Task<RealmCatalogSnapshot> ReadEnabledAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class SequencedRealmCatalogReader : IRealmCatalogReader
    {
        private readonly RealmCatalogSnapshot[] _snapshots;

        public SequencedRealmCatalogReader(
            params RealmCatalogSnapshot[] snapshots)
        {
            _snapshots = snapshots;
        }

        public int ReadCount { get; private set; }

        public Task<RealmCatalogSnapshot> ReadEnabledAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadCount >= _snapshots.Length)
            {
                throw new InvalidOperationException(
                    "The scripted realm catalog was read too many times.");
            }

            return Task.FromResult(_snapshots[ReadCount++]);
        }
    }

    private sealed class LoginAccountStore : GameStoreTestStub
    {
        public override Task<GameAccount> LoginOrCreateAccountAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameAccount
            {
                Id = 7,
                Username = username
            });
    }
}
