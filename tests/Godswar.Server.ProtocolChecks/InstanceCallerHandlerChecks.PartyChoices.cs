using System.Buffers.Binary;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task
        CheckDecliningMemberLeavesLeaderInsideAsync()
    {
        await using var fixture = await CreatePartyChoiceFixtureAsync();
        var sourceInstanceId = GetSourceInstanceId(fixture.Leader);
        await OpenMedusaPageAsync(fixture.Leader);
        var leaderBefore = fixture.Leader.ReadPackets().Count;
        var memberBefore = fixture.Transport.ReadLegacyPackets().Count;

        await InvokeAsync(
            fixture.Leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));

        var notice = fixture.Transport.ReadLegacyPackets()
            .Skip(memberBefore)
            .Single(IsRepetitionInvitation);
        var (repetitionId, invitationId) =
            ReadRepetitionInvitation(notice);
        var targetInstanceId = GetSourceInstanceId(fixture.Leader);
        await InvokeAsync(
            fixture.MemberHandler,
            CreateRepetitionResponse(
                repetitionId,
                invitationId,
                accepted: false));

        Check.True(
            fixture.Leader.Character.CurrentMap == 200 &&
            targetInstanceId != sourceInstanceId &&
            fixture.Leader.ReadPackets()
                .Skip(leaderBefore)
                .Count(packet =>
                    packet.SequenceEqual(EnhancedSceneChange())) == 1 &&
            fixture.Member.CurrentMap == fixture.Leader.SourceMapId &&
            GetMemberInstanceId(fixture) == sourceInstanceId &&
            fixture.Transport.ReadLegacyPackets()
                .Skip(memberBefore)
                .Last()
                .SequenceEqual(PacketBuilder.RepetitionReset()),
            "a declining member remains outside without canceling or " +
            "moving the leader");
    }

    private static async Task CheckDailyEntryEligibilityAsync()
    {
        await CheckUsedMemberReceivesNoConfirmationAsync();
        await CheckUsedLeaderCannotInitiateAsync();
    }

    private static async Task CheckTimedOutMemberLeavesLeaderInsideAsync()
    {
        await using var fixture = await CreatePartyChoiceFixtureAsync();
        var sourceInstanceId = GetSourceInstanceId(fixture.Leader);
        await OpenMedusaPageAsync(fixture.Leader);
        var memberBefore = fixture.Transport.ReadLegacyPackets().Count;

        await InvokeAsync(
            fixture.Leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));

        var notice = fixture.Transport.ReadLegacyPackets()
            .Skip(memberBefore)
            .Single(IsRepetitionInvitation);
        var (repetitionId, invitationId) =
            ReadRepetitionInvitation(notice);
        var targetInstanceId = GetSourceInstanceId(fixture.Leader);
        var expired = fixture.Leader.Registry.ExpireMedusaInvitation(
            invitationId,
            DateTimeOffset.UtcNow.AddMinutes(2));
        var memberBeforeStaleResponse =
            fixture.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            fixture.MemberHandler,
            CreateRepetitionResponse(
                repetitionId,
                invitationId,
                accepted: true));

        Check.True(
            expired?.Invitee.CharacterId == fixture.Member.Id &&
            fixture.Leader.Character.CurrentMap == 200 &&
            GetSourceInstanceId(fixture.Leader) == targetInstanceId &&
            targetInstanceId != sourceInstanceId &&
            fixture.Member.CurrentMap == fixture.Leader.SourceMapId &&
            GetMemberInstanceId(fixture) == sourceInstanceId &&
            fixture.Transport.ReadLegacyPackets().Count ==
                memberBeforeStaleResponse,
            "a timed-out member remains outside and cannot cancel or join " +
            "the leader's active instance");
    }

    private static async Task CheckUsedMemberReceivesNoConfirmationAsync()
    {
        await using var fixture = await CreatePartyChoiceFixtureAsync();
        var sourceInstanceId = GetSourceInstanceId(fixture.Leader);
        Check.True(
            fixture.Leader.Registry.TryReserveLocalMedusaDailyEntry(
                Guid.NewGuid(),
                RealmId.Tempest,
                CurrentTestRealmDay(),
                [fixture.Member.Id]),
            "test precondition consumes the member's daily entry");
        await OpenMedusaPageAsync(fixture.Leader);
        var memberBefore = fixture.Transport.ReadLegacyPackets().Count;

        await InvokeAsync(
            fixture.Leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));

        Check.True(
            fixture.Leader.Character.CurrentMap == 200 &&
            GetSourceInstanceId(fixture.Leader) != sourceInstanceId &&
            fixture.Member.CurrentMap == fixture.Leader.SourceMapId &&
            GetMemberInstanceId(fixture) == sourceInstanceId &&
            !fixture.Transport.ReadLegacyPackets()
                .Skip(memberBefore)
                .Any(IsRepetitionInvitation),
            "a member whose daily entry is used receives no confirmation " +
            "while the eligible leader enters");
    }

    private static async Task CheckUsedLeaderCannotInitiateAsync()
    {
        await using var leader = await CreateFixtureAsync(
            level: 90,
            transitionReady: true);
        var sourceInstanceId = GetSourceInstanceId(leader);
        Check.True(
            leader.Registry.TryReserveLocalMedusaDailyEntry(
                Guid.NewGuid(),
                RealmId.Tempest,
                CurrentTestRealmDay(),
                [leader.Character.Id]),
            "test precondition consumes the leader's daily entry");
        await OpenMedusaPageAsync(leader);
        var before = leader.ReadPackets().Count;

        await InvokeAsync(
            leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));

        Check.True(
            leader.Character.CurrentMap == leader.SourceMapId &&
            GetSourceInstanceId(leader) == sourceInstanceId &&
            leader.ReadPackets().Skip(before).Single().SequenceEqual(
                PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol.QueueUnavailableResultSubId)),
            "a leader whose daily entry is used cannot initiate Medusa");
    }

    private static async Task<PartyChoiceFixture>
        CreatePartyChoiceFixtureAsync()
    {
        var leader = await CreateFixtureAsync(
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
        member.Name = "InstanceCallerChoiceMember";
        member.Level = 90;
        var transport = new FactionCrierCaptureTransport();
        var session = new ClientSession(transport);
        try
        {
            GameHandlerOwnershipTestFences.Bind(
                leader.Registry,
                session,
                member.AccountId,
                member);
            leader.Registry.JoinMap(
                session,
                member.AccountId,
                member,
                objectId: 700021);
            var worldContent = PinnedWorldContentReader.Create(
                "instance-caller-party-choice-v1",
                [(short)member.CurrentMap, 200, 204],
                [],
                [],
                [],
                new DateTimeOffset(
                    2026,
                    8,
                    24,
                    0,
                    0,
                    0,
                    TimeSpan.Zero));
            var handler = new GameClientHandler(
                session,
                new InstanceCallerGameStore(),
                leader.Registry,
                CharacterSnapshotReaderTestFixtures.Unused,
                worldContent);
            SetHandlerField(
                handler,
                "_account",
                new AccountIdentity(
                    member.AccountId,
                    "instance-caller-choice-member"));
            SetHandlerField(handler, "_character", member);
            SetHandlerField(handler, "_registered", true);
            SetHandlerField(handler, "_worldPresenceAnnounced", true);
            leader.Registry.RegisterInstanceTransitionSink(
                session,
                (command, cancellationToken) =>
                    InvokePartyTransitionAsync(
                        handler,
                        command,
                        cancellationToken));

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
                "choice-test member joins the leader's party");
            return new(
                leader,
                member,
                session,
                transport,
                handler);
        }
        catch
        {
            leader.Registry.Remove(session);
            await session.DisposeAsync();
            await leader.DisposeAsync();
            throw;
        }
    }

    private static WorldInstanceId GetMemberInstanceId(
        PartyChoiceFixture fixture)
    {
        Check.True(
            fixture.Leader.Registry.TryGetSessionWorldInstanceId(
                fixture.MemberSession,
                out var instanceId),
            "party member has a world instance");
        return instanceId;
    }

    private static bool IsRepetitionInvitation(byte[] packet) =>
        packet.Length >= 4 &&
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2)) == Opcodes.RepetitionInvitation;

    private static byte[] EnhancedSceneChange() =>
            PacketBuilder.SceneChange(0x1448, 212f, 0f, -217f, 200);

    private static DateOnly CurrentTestRealmDay() =>
        RealmCalendar.CreateForTesting(
                RealmId.Tempest,
                "Asia/Manila")
            .GetDay(DateTimeOffset.UtcNow);

    private sealed record PartyChoiceFixture(
        InstanceCallerFixture Leader,
        GameCharacter Member,
        ClientSession MemberSession,
        FactionCrierCaptureTransport Transport,
        GameClientHandler MemberHandler) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Leader.Registry.UnregisterInstanceTransitionSink(MemberSession);
            Leader.Registry.Remove(MemberSession);
            await MemberSession.DisposeAsync();
            await Leader.DisposeAsync();
        }
    }
}
