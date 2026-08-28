using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static readonly MethodInfo PartyInstanceTransitionMethod =
        FindHandlerMethod("HandlePartyInstanceTransitionAsync");

    private static async Task CheckSuccessfulPartyEntryAsync()
    {
        await using var leader = await CreateFixtureAsync(
            level: 90,
            transitionReady: true);
        var memberSnapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
            memberSnapshot) ?? throw new InvalidOperationException(
                "Instance Caller party member did not hydrate.");
        var member = hydrated.Character;
        member.Id++;
        member.AccountId++;
        member.Name = "InstanceCallerMember";
        member.Level = 90;
        var transport = new FactionCrierCaptureTransport();
        var session = new ClientSession(transport);
        GameHandlerOwnershipTestFences.Bind(
            leader.Registry,
            session,
            member.AccountId,
            member);
        leader.Registry.JoinMap(
            session,
            member.AccountId,
            member,
            objectId: 700020);

        var worldContent = PinnedWorldContentReader.Create(
            "instance-caller-party-member-v1",
            [(short)member.CurrentMap, 200, 204],
            [],
            [],
            [],
            new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero));
        var handler = new GameClientHandler(
            session,
            new InstanceCallerGameStore(),
            leader.Registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            worldContent);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(member.AccountId, "instance-caller-member"));
        SetHandlerField(handler, "_character", member);
        SetHandlerField(handler, "_registered", true);
        SetHandlerField(handler, "_worldPresenceAnnounced", true);
        leader.Registry.RegisterInstanceTransitionSink(
            session,
            (command, cancellationToken) => InvokePartyTransitionAsync(
                handler,
                command,
                cancellationToken));

        try
        {
            var now = DateTimeOffset.UtcNow;
            var invited = leader.Registry.InvitePartyMember(
                leader.Session,
                leader.Character.Name,
                member.Name,
                now);
            var accepted = leader.Registry.AcceptPartyInvite(
                session,
                leader.Character.Name,
                member.Name,
                now.AddSeconds(1));
            Check.True(
                invited.Status == PartyOperationStatus.Applied &&
                accepted.Status == PartyOperationStatus.Applied,
                "eligible member joins the leader's party");

            var sourceInstanceId = GetSourceInstanceId(leader);
            await OpenMedusaPageAsync(leader);
            var leaderBefore = leader.ReadPackets().Count;
            var memberBefore = transport.ReadLegacyPackets().Count;

            await InvokeAsync(
                leader.Handler,
                CreateActionPacket(
                    InstanceCallerProtocol.MedusaRootSubId,
                    InstanceCallerProtocol.AdvancedDifficultySubId));

            var initialLeaderPackets = leader.ReadPackets()
                .Skip(leaderBefore)
                .ToArray();
            var initialMemberPackets = transport.ReadLegacyPackets()
                .Skip(memberBefore)
                .ToArray();
            var memberNotice = initialMemberPackets
                .Single(packet =>
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        packet.AsSpan(2)) == Opcodes.RepetitionInvitation);
            var (repetitionId, invitationId) =
                ReadRepetitionInvitation(memberNotice);
            Check.Equal(
                (int)MedusaIslandRosterPolicy.EnhancedClientSceneId,
                repetitionId,
                "Medusa invitation uses the client scene identity instead " +
                "of the content map identity");
            Check.Equal(
                leader.Character.Name,
                PacketText.ReadFixedAscii(
                    memberNotice,
                    12,
                    CharacterSnapshotLimits.CharacterNameLength),
                "Medusa invitation names the party leader");
            var expectedScene = PacketBuilder.SceneChange(
                0x1448,
                212f,
                0f,
                -217f,
                200);
            var leaderRefresh = initialLeaderPackets
                .Single(IsPartyRefresh);
            var memberRefresh = initialMemberPackets
                .Single(IsPartyRefresh);
            Check.True(
                initialLeaderPackets.Count(packet =>
                    packet.SequenceEqual(expectedScene)) == 1 &&
                ReadPartyMap(leaderRefresh, 0) == 200 &&
                ReadPartyMap(leaderRefresh, 1) ==
                    leader.SourceMapId &&
                ReadPartyMap(memberRefresh, 0) == 200 &&
                ReadPartyMap(memberRefresh, 1) ==
                    leader.SourceMapId &&
                leader.Registry.TryGetSessionWorldInstanceId(
                    leader.Session,
                    out var leaderInstanceId) &&
                leader.Registry.TryGetSessionWorldInstanceId(
                    session,
                    out var waitingMemberInstanceId) &&
                leaderInstanceId != sourceInstanceId &&
                waitingMemberInstanceId == sourceInstanceId &&
                leader.Character.CurrentMap == 200 &&
                member.CurrentMap == leader.SourceMapId,
                "the leader enters immediately while the member receives " +
                "the native confirmation");
            var targetInstanceId = GetSourceInstanceId(leader);
            var leaderBeforeReadinessPump = leader.ReadPackets().Count;
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            Check.True(
                !leader.ReadPackets()
                    .Skip(leaderBeforeReadinessPump)
                    .Any(packet => ReadOpcode(packet) is
                        Opcodes.RepetitionSync or
                        Opcodes.RepetitionFightInfo),
                "the leader panel waits for destination readiness without losing its registration");

            var memberBeforeContentMapResponse =
                transport.ReadLegacyPackets().Count;
            await InvokeAsync(
                handler,
                CreateRepetitionResponse(
                    200,
                    invitationId,
                    accepted: true));
            Check.True(
                transport.ReadLegacyPackets().Count ==
                    memberBeforeContentMapResponse &&
                member.CurrentMap == leader.SourceMapId,
                "a content-map echo cannot satisfy a client-scene " +
                "invitation");

            var leaderBeforeWrongResponse = leader.ReadPackets().Count;
            var memberBeforeWrongResponse =
                transport.ReadLegacyPackets().Count;
            await InvokeAsync(
                handler,
                CreateRepetitionResponse(
                    repetitionId,
                    invitationId + 1,
                    accepted: true));
            Check.True(
                leader.ReadPackets().Count ==
                    leaderBeforeWrongResponse &&
                transport.ReadLegacyPackets().Count ==
                    memberBeforeWrongResponse &&
                leader.Character.CurrentMap == 200 &&
                member.CurrentMap == leader.SourceMapId,
                "a response for another invitation cannot move the member");

            await InvokeAsync(
                handler,
                CreateRepetitionResponse(
                    repetitionId,
                    invitationId,
                    accepted: true));

            Check.True(
                leader.Registry.TryGetSessionWorldInstanceId(
                    leader.Session,
                    out var acceptedLeaderInstanceId) &&
                leader.Registry.TryGetSessionWorldInstanceId(
                    session,
                    out var memberInstanceId) &&
                acceptedLeaderInstanceId == targetInstanceId &&
                targetInstanceId == memberInstanceId &&
                leader.Character.CurrentMap == 200 &&
                member.CurrentMap == 200,
                "an accepting member joins the leader's Medusa dungeon");

            var leaderAdmissionPackets = leader.ReadPackets()
                .Skip(leaderBefore)
                .ToArray();
            var memberAdmissionPackets = transport.ReadLegacyPackets()
                .Skip(memberBefore)
                .ToArray();
            var leaderRefreshes = leaderAdmissionPackets
                .Where(IsPartyRefresh)
                .ToArray();
            var memberRefreshes = memberAdmissionPackets
                .Where(IsPartyRefresh)
                .ToArray();
            var noticeIndex = Array.FindIndex(
                memberAdmissionPackets,
                packet => packet.SequenceEqual(memberNotice));
            var resetIndex = Array.FindIndex(
                memberAdmissionPackets,
                packet => packet.SequenceEqual(
                    PacketBuilder.RepetitionReset()));
            var sceneIndex = Array.FindIndex(
                memberAdmissionPackets,
                packet => packet.SequenceEqual(expectedScene));
            Check.True(
                leaderAdmissionPackets.Count(packet =>
                    packet.SequenceEqual(expectedScene)) == 1 &&
                memberAdmissionPackets.Count(packet =>
                    packet.SequenceEqual(expectedScene)) == 1 &&
                leaderRefreshes.Length == 2 &&
                memberRefreshes.Length == 2 &&
                noticeIndex >= 0 &&
                noticeIndex < resetIndex &&
                resetIndex < sceneIndex &&
                ReadPartyMap(leaderRefreshes[^1], 0) == 200 &&
                ReadPartyMap(leaderRefreshes[^1], 1) == 200 &&
                ReadPartyMap(memberRefreshes[^1], 0) == 200 &&
                ReadPartyMap(memberRefreshes[^1], 1) == 200,
                "member confirmation closes before scene change and party " +
                "locations refresh after each accepted transfer");

            await InvokeAsync(
                handler,
                CreateControlPacket(Opcodes.ClientReady));
            await InvokeAsync(handler, CreatePlayerDetailRequest());
            await InvokeAsync(
                leader.Handler,
                CreateControlPacket(Opcodes.ClientReady));
            await InvokeAsync(
                leader.Handler,
                CreatePlayerDetailRequest());
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            var leaderPackets = leader.ReadPackets()
                .Skip(leaderBefore)
                .ToArray();
            var memberPackets = transport.ReadLegacyPackets()
                .Skip(memberBefore)
                .ToArray();
            Check.True(
                !leader.Session.IsDisconnected &&
                !session.IsDisconnected &&
                leaderPackets.Any(packet =>
                    IsPlayerAppearance(packet, 700020, 200)) &&
                memberPackets.Any(packet =>
                    IsPlayerAppearance(packet, 700019, 200)) &&
                leaderPackets.Any(packet =>
                    ReadOpcode(packet) == Opcodes.RepetitionSync) &&
                leaderPackets.Any(packet =>
                    ReadOpcode(packet) == Opcodes.RepetitionFightInfo) &&
                leaderPackets.Any(packet =>
                    ReadOpcode(packet) ==
                        Opcodes.RepetitionInstanceMembers &&
                    BinaryPrimitives.ReadInt32LittleEndian(
                        packet.AsSpan(4)) == 2) &&
                memberPackets.Any(packet =>
                    ReadOpcode(packet) ==
                        Opcodes.RepetitionInstanceMembers &&
                    BinaryPrimitives.ReadInt32LittleEndian(
                        packet.AsSpan(4)) == 2) &&
                !memberPackets.Any(packet =>
                    ReadOpcode(packet) is Opcodes.RepetitionSync or
                        Opcodes.RepetitionFightInfo) &&
                leader.Registry.GetWorldInstanceSessions(
                    targetInstanceId).Count == 2,
                "Medusa party entry publishes both players and sends the " +
                "run timer only to the leader and the instance roster to both");

            var leaderBeforeMonsterAoi = leader.ReadPackets().Count;
            await InvokeAsync(
                leader.Handler,
                CreateInstanceWalkPacket(119f, -197f));
            var monsterAppearances = leader.ReadPackets()
                .Skip(leaderBeforeMonsterAoi)
                .Where(IsMonsterAppearance)
                .ToArray();
            Check.True(
                monsterAppearances.Length > 0 &&
                monsterAppearances.All(packet =>
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        packet.AsSpan(6)) == 200),
                "walking into the first Medusa lane publishes monsters " +
                "tagged for the active client map");

            var leaderBeforeEnd = leader.ReadPackets().Count;
            var memberBeforeEnd = transport.ReadLegacyPackets().Count;
            leader.Registry.RegisterInstanceTransitionSink(
                leader.Session,
                (command, cancellationToken) =>
                    InvokePartyTransitionAsync(
                        leader.Handler,
                        command,
                        cancellationToken));
            await InvokeAsync(
                leader.Handler,
                CreateRepetitionPanelAction(
                    action: 0,
                    trailingByte: 0xED));
            Check.True(
                leader.ReadPackets()
                    .Skip(leaderBeforeEnd)
                    .Single()
                    .SequenceEqual(PacketBuilder.RepetitionReset()),
                "the native leader control authoritatively ends and closes the Medusa panel");

            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            Check.True(
                leader.Character.CurrentMap != 200 &&
                member.CurrentMap != 200 &&
                GetSourceInstanceId(leader) != targetInstanceId &&
                leader.ReadPackets().Skip(leaderBeforeEnd).Any(packet =>
                    ReadOpcode(packet) == Opcodes.SceneChange) &&
                transport.ReadLegacyPackets().Skip(memberBeforeEnd)
                    .Any(packet =>
                        ReadOpcode(packet) == Opcodes.SceneChange),
                "leader terminate exits every online member from the active Medusa run");
        }
        finally
        {
            leader.Registry.UnregisterInstanceTransitionSink(
                leader.Session);
            leader.Registry.UnregisterInstanceTransitionSink(session);
            leader.Registry.Remove(session);
            await session.DisposeAsync();
        }
    }

    private static async Task<bool> InvokePartyTransitionAsync(
        GameClientHandler handler,
        MedusaInstanceTransitionCommand command,
        CancellationToken cancellationToken)
    {
        var task = PartyInstanceTransitionMethod.Invoke(
            handler,
            [command, cancellationToken]) as Task<bool> ??
            throw new InvalidOperationException(
                "Party instance transition did not return a task.");
        return await task;
    }

    private static bool IsMonsterAppearance(byte[] packet) =>
        packet.Length >= 4 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10020;

    private static ushort ReadOpcode(byte[] packet) =>
        packet.Length >= 4
            ? BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2))
            : (ushort)0;

    private static GamePacket CreateRepetitionLeave(
        int repetitionId,
        int repetitionIndex)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionLeave);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            repetitionIndex);
        return new GamePacket(packet);
    }

    private static GamePacket CreateRepetitionPanelAction(
        byte action,
        byte trailingByte)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionPanelAction);
        packet[4] = action;
        packet[5] = trailingByte;
        return new GamePacket(packet);
    }

    private static bool IsPartyRefresh(byte[] packet) =>
        packet.Length == PartyProtocol.RefreshPacketBytes &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
            Opcodes.PartyRefresh;

    private static short ReadPartyMap(byte[] packet, int memberIndex) =>
        BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(
            4 + memberIndex * PartyProtocol.RefreshMemberBytes + 82));

    private static bool IsPlayerAppearance(
        byte[] packet,
        uint objectId,
        ushort mapId) =>
        packet.Length >= 178 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x2725 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == objectId &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(176)) == mapId;

    private static GamePacket CreateInstanceWalkPacket(float x, float z)
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
        return new GamePacket(packet);
    }
}
