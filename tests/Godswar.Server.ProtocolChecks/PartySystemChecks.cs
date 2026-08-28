using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PartySystemChecks
{
    public const string CheckName =
        "Five-player party and leader-only instance initiation";
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    public static async Task RunAsync()
    {
        CheckActionWireContract();

        await using var registry = new GameSessionRegistry();
        var players = Enumerable.Range(0, 6)
            .Select(CreatePlayer)
            .ToArray();
        try
        {
            foreach (var player in players)
            {
                registry.JoinMap(
                    player.Session,
                    player.Character.AccountId,
                    player.Character,
                    player.ObjectId);
            }

            var now = new DateTimeOffset(
                2026,
                8,
                24,
                12,
                0,
                0,
                TimeSpan.Zero);
            await CheckHandlerRoundTripAsync(
                registry,
                players,
                now);
            for (var index = 2;
                 index < PartyProtocol.MaximumMembers;
                 index++)
            {
                var invitation = registry.InvitePartyMember(
                    players[0].Session,
                    players[0].Character.Name,
                    players[index].Character.Name,
                    now.AddSeconds(index));
                Check.True(
                    invitation.Status == PartyOperationStatus.Applied,
                    $"party invitation {index}");
                CheckInvitePacket(
                    invitation.Deliveries.Single().Packet,
                    players[0],
                    players[index]);

                var accepted = registry.AcceptPartyInvite(
                    players[index].Session,
                    players[0].Character.Name,
                    players[index].Character.Name,
                    now.AddSeconds(index));
                Check.True(
                    accepted.Status == PartyOperationStatus.Applied,
                    $"party acceptance {index}");
                Check.Equal(
                    index + 1,
                    accepted.Deliveries.Count,
                    $"party refresh recipient count {index}");
            }

            CheckFiveMemberLimit(registry, players, now);
            CheckLeaderAuthority(registry, players);
            CheckInvitationExpiry(registry, players, now);
        }
        finally
        {
            foreach (var player in players)
            {
                registry.Remove(player.Session);
                await player.Session.DisposeAsync();
            }
        }
    }

    private static async Task CheckHandlerRoundTripAsync(
        GameSessionRegistry registry,
        IReadOnlyList<PartyCheckPlayer> players,
        DateTimeOffset now)
    {
        var worldContent = PinnedWorldContentReader.Create(
            "party-system-check-v1",
            [(short)GameDefaults.AthensCapitalMap],
            [],
            [],
            [],
            now);
        var leaderHandler = CreateHandler(
            registry,
            players[0],
            worldContent);
        var memberHandler = CreateHandler(
            registry,
            players[1],
            worldContent);

        await InvokeHandlerAsync(
            leaderHandler,
            PacketBuilder.PartyAction(
                Opcodes.PartyInvite,
                0x00001448,
                players[0].Character.Name,
                players[1].Character.Name));
        var invitedPackets = players[1].Transport.ReadLegacyPackets();
        CheckInvitePacket(
            invitedPackets.Single(),
            players[0],
            players[1]);

        await InvokeHandlerAsync(
            memberHandler,
            PacketBuilder.PartyAction(
                Opcodes.PartyAccept,
                0x00001448,
                players[0].Character.Name,
                players[1].Character.Name));
        var leaderPackets = players[0].Transport.ReadLegacyPackets();
        var memberPackets = players[1].Transport.ReadLegacyPackets();
        CheckRefreshWireContract(
            leaderPackets.Single(),
            memberPackets.Last(),
            players);
        Check.True(
            registry.GetPartyMembership(players[0].Session) is
            {
                IsLeader: true,
                MemberCharacterIds.Count: 2
            },
            "native invite and accept ingress creates the party");

        var leaderBeforeMovement =
            players[0].Transport.ReadLegacyPackets().Count;
        var memberBeforeMovement =
            players[1].Transport.ReadLegacyPackets().Count;
        await InvokeHandlerAsync(
            memberHandler,
            CreateWalkPacket(77f, 31f));
        var leaderMovementRefresh = players[0].Transport
            .ReadLegacyPackets()
            .Skip(leaderBeforeMovement)
            .Single(IsPartyRefresh);
        var memberMovementRefresh = players[1].Transport
            .ReadLegacyPackets()
            .Skip(memberBeforeMovement)
            .Single(IsPartyRefresh);
        foreach (var refresh in new[]
                 {
                     leaderMovementRefresh,
                     memberMovementRefresh
                 })
        {
            Check.Equal(
                players[1].Character.CurrentMap,
                ReadMemberMapId(refresh, 1),
                "party movement refresh carries the current map");
            Check.Equal(
                77f,
                ReadMemberPositionX(refresh, 1),
                "party movement refresh carries the current X coordinate");
            Check.Equal(
                31f,
                ReadMemberPositionZ(refresh, 1),
                "party movement refresh carries the current Z coordinate");
        }
    }

    private static GameClientHandler CreateHandler(
        GameSessionRegistry registry,
        PartyCheckPlayer player,
        IWorldContentReader worldContent)
    {
        var handler = new GameClientHandler(
            player.Session,
            new PartyGameStore(),
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            worldContent);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(
                player.Character.AccountId,
                $"party-check-{player.Character.AccountId}"));
        SetHandlerField(handler, "_character", player.Character);
        return handler;
    }

    private static async Task InvokeHandlerAsync(
        GameClientHandler handler,
        byte[] clearPacket)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [new GamePacket(clearPacket), CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "Party handler did not return a task.");
        await task;
    }

    private static void SetHandlerField<T>(
        GameClientHandler handler,
        string name,
        T value) =>
        (typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
         throw new InvalidOperationException(
             $"GameClientHandler.{name} was not found."))
        .SetValue(handler, value);

    private static void CheckActionWireContract()
    {
        var packet = PacketBuilder.PartyAction(
            Opcodes.PartyInvite,
            700_001,
            "Leader",
            "Member");
        Check.Equal(
            PartyProtocol.ActionPacketBytes,
            packet.Length,
            "party action byte length");
        Check.True(
            PartyProtocol.TryReadAction(
                new GamePacket(packet),
                out var action) &&
            action.ClaimedObjectId == 700_001 &&
            action.FirstName == "Leader" &&
            action.SecondName == "Member",
            "party action parser uses both fixed native name fields");

        var malformed = packet[..^1];
        Check.True(
            !PartyProtocol.TryReadAction(
                new GamePacket(malformed),
                out _),
            "party actions require exact declared and actual length");
    }

    private static void CheckInvitePacket(
        byte[] packet,
        PartyCheckPlayer leader,
        PartyCheckPlayer target)
    {
        Check.Equal(
            Opcodes.PartyInvite,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "party invitation opcode");
        Check.Equal(
            leader.ObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
            "party invitation leader object ID");
        Check.Equal(
            leader.Character.Name,
            ReadName(packet.AsSpan(8, 64)),
            "party invitation leader name");
        Check.Equal(
            target.Character.Name,
            ReadName(packet.AsSpan(72, 64)),
            "party invitation target name");
    }

    private static void CheckRefreshWireContract(
        byte[] leaderPacket,
        byte[] memberPacket,
        IReadOnlyList<PartyCheckPlayer> players)
    {
        Check.Equal(
            PartyProtocol.RefreshPacketBytes,
            leaderPacket.Length,
            "party refresh byte length");
        Check.Equal(
            Opcodes.PartyRefresh,
            BinaryPrimitives.ReadUInt16LittleEndian(
                leaderPacket.AsSpan(2)),
            "party refresh opcode");
        Check.Equal(
            0x00001448u,
            ReadMemberObjectId(leaderPacket, 0),
            "leader sees itself in the native local namespace");
        Check.Equal(
            players[0].ObjectId,
            ReadMemberObjectId(memberPacket, 0),
            "member sees the leader's world object ID first");
        Check.Equal(
            0x00001448u,
            ReadMemberObjectId(memberPacket, 1),
            "member sees itself in the native local namespace");
        Check.Equal(
            players[0].Character.Name,
            ReadMemberName(leaderPacket, 0),
            "party refresh keeps leader in record zero");
        Check.Equal(
            uint.MaxValue,
            ReadMemberObjectId(leaderPacket, 2),
            "unused party records use the native empty sentinel");
    }

    private static void CheckFiveMemberLimit(
        GameSessionRegistry registry,
        IReadOnlyList<PartyCheckPlayer> players,
        DateTimeOffset now)
    {
        var leader = players[0];
        var sixth = players[5];
        var membership = registry.GetPartyMembership(leader.Session) ??
            throw new InvalidOperationException(
                "The five-member party was not retained.");
        Check.True(
            membership.IsLeader &&
            membership.MemberCharacterIds.SequenceEqual(
                players.Take(5).Select(player => player.Character.Id)),
            "party authority retains exactly five ordered members");

        var overflow = registry.InvitePartyMember(
            leader.Session,
            leader.Character.Name,
            sixth.Character.Name,
            now.AddMinutes(1));
        Check.True(
            overflow.Status == PartyOperationStatus.PartyFull,
            "sixth party member is rejected");
        Check.True(
            registry.GetPartyMembership(sixth.Session) is null,
            "rejected sixth player remains outside the party");

        var memberInvite = registry.InvitePartyMember(
            players[1].Session,
            players[1].Character.Name,
            sixth.Character.Name,
            now.AddMinutes(1));
        Check.True(
            memberInvite.Status == PartyOperationStatus.NotLeader,
            "non-leader cannot invite another member");
    }

    private static void CheckLeaderAuthority(
        GameSessionRegistry registry,
        IReadOnlyList<PartyCheckPlayer> players)
    {
        Check.True(
            registry.CanInitiateInstance(players[0].Session) &&
            !registry.CanInitiateInstance(players[1].Session) &&
            registry.CanInitiateInstance(players[5].Session),
            "only a party leader may initiate while solo players remain valid");

        var changed = registry.ChangePartyLeader(
            players[0].Session,
            players[0].Character.Name,
            players[1].Character.Name);
        Check.True(
            changed.Status == PartyOperationStatus.Applied,
            "leader transfer succeeds");
        Check.True(
            !registry.CanInitiateInstance(players[0].Session) &&
            registry.CanInitiateInstance(players[1].Session),
            "instance authority follows the transferred leadership");

        var formerLeaderDissolve = registry.DissolveParty(
            players[0].Session,
            players[0].Character.Name);
        Check.True(
            formerLeaderDissolve.Status == PartyOperationStatus.NotLeader,
            "former leader cannot dissolve the party");
        var dissolved = registry.DissolveParty(
            players[1].Session,
            players[1].Character.Name);
        Check.True(
            dissolved.Status == PartyOperationStatus.Applied &&
            dissolved.Deliveries.Count == 5 &&
            dissolved.Deliveries.All(delivery =>
                BinaryPrimitives.ReadUInt16LittleEndian(
                    delivery.Packet.AsSpan(2)) == Opcodes.PartyDestroy) &&
            players.Take(5).All(player =>
                registry.GetPartyMembership(player.Session) is null),
            "current leader dissolves and resets every member");
    }

    private static void CheckInvitationExpiry(
        GameSessionRegistry registry,
        IReadOnlyList<PartyCheckPlayer> players,
        DateTimeOffset now)
    {
        var invite = registry.InvitePartyMember(
            players[5].Session,
            players[5].Character.Name,
            players[0].Character.Name,
            now);
        Check.True(
            invite.Status == PartyOperationStatus.Applied,
            "solo invitation is issued");
        var expired = registry.AcceptPartyInvite(
            players[0].Session,
            players[5].Character.Name,
            players[0].Character.Name,
            now.AddMinutes(2));
        Check.True(
            expired.Status == PartyOperationStatus.InvitationMissing,
            "expired invitation cannot create a party");
    }

    private static PartyCheckPlayer CreatePlayer(int index)
    {
        var character = new GameCharacter
        {
            Id = 1_000 + index,
            AccountId = 2_000 + index,
            RealmId = RealmId.Tempest,
            Name = $"Party{index + 1}",
            CurrentMap = GameDefaults.AthensCapitalMap,
            CurrentHp = 1_200 + index,
            MaxHp = 1_500 + index,
            Level = 20 + index,
            Profession = checked((byte)(index % 4)),
            PositionX = 10 + index,
            PositionZ = 20 + index
        };
        return new PartyCheckPlayer(
            new FactionCrierCaptureTransport(),
            character,
            checked((uint)(700_001 + index)));
    }

    private static uint ReadMemberObjectId(byte[] packet, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(
                4 + index * PartyProtocol.RefreshMemberBytes,
                sizeof(uint)));

    private static short ReadMemberMapId(byte[] packet, int index) =>
        BinaryPrimitives.ReadInt16LittleEndian(
            packet.AsSpan(
                4 + index * PartyProtocol.RefreshMemberBytes + 82,
                sizeof(short)));

    private static float ReadMemberPositionX(byte[] packet, int index) =>
        BinaryPrimitives.ReadSingleLittleEndian(
            packet.AsSpan(
                4 + index * PartyProtocol.RefreshMemberBytes + 84,
                sizeof(float)));

    private static float ReadMemberPositionZ(byte[] packet, int index) =>
        BinaryPrimitives.ReadSingleLittleEndian(
            packet.AsSpan(
                4 + index * PartyProtocol.RefreshMemberBytes + 92,
                sizeof(float)));

    private static string ReadMemberName(byte[] packet, int index) =>
        ReadName(packet.AsSpan(
            4 + index * PartyProtocol.RefreshMemberBytes + 17,
            65));

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            bytes = bytes[..end];
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static bool IsPartyRefresh(byte[] packet) =>
        packet.Length == PartyProtocol.RefreshPacketBytes &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
            Opcodes.PartyRefresh;

    private static byte[] CreateWalkPacket(float x, float z)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Walk);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            0xA5A5_0001u);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16), 1f);
        return packet;
    }

    private sealed record PartyCheckPlayer(
        FactionCrierCaptureTransport Transport,
        GameCharacter Character,
        uint ObjectId)
    {
        public ClientSession Session { get; } = new(Transport);
    }

    private sealed class PartyGameStore : GameStoreTestStub;
}
