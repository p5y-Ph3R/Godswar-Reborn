using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<MedusaPartyEntryStatus> BeginMedusaLeaderEntryAsync(
        MedusaInstancePartySnapshot party,
        MedusaEncounterDifficulty difficulty,
        int targetMapId,
        CancellationToken cancellationToken)
    {
        if (!MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "first-entry",
                out var entrance))
        {
            return MedusaPartyEntryStatus.RuntimeUnavailable;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var realmDay = _realmCalendar.GetDay(startedAt);
        var partyCharacterIds = party.Members
            .Select(static member => member.CharacterId)
            .ToArray();
        var usedCharacterIds =
            await TryFindUsedMedusaDailyEntryCharactersAsync(
                party.RealmId,
                realmDay,
                partyCharacterIds,
                cancellationToken);
        if (usedCharacterIds is null)
        {
            return MedusaPartyEntryStatus.RuntimeUnavailable;
        }
        if (usedCharacterIds.Contains(party.LeaderCharacterId))
        {
            return MedusaPartyEntryStatus.DailyEntryAlreadyUsed;
        }

        var eligibleMembers = party.Members
            .Where(member =>
                member.CharacterId == party.LeaderCharacterId ||
                !usedCharacterIds.Contains(member.CharacterId))
            .ToArray();
        var leaderReservationId = Guid.NewGuid();
        var dailyEntryLimit = await TryClaimMedusaDailyEntryAsync(
                leaderReservationId,
                party.RealmId,
                realmDay,
                difficulty,
                [party.LeaderCharacterId],
                startedAt,
                cancellationToken);
        if (dailyEntryLimit is null)
        {
            return MedusaPartyEntryStatus.DailyEntryAlreadyUsed;
        }

        PreparedWorldInstanceCreationResult creation;
        try
        {
            creation = await _registry.CreatePreparedLocalWorldInstanceAsync(
                party.RealmId,
                new WorldMapId(checked((short)targetMapId)),
                InstanceKind.Dungeon,
                MedusaIslandPolicy.MaximumPartySize,
                new MedusaWorldInstanceEntryPreparation(
                    difficulty,
                    eligibleMembers.Select(static member =>
                        member.CharacterId)),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseMedusaDailyEntryAsync(leaderReservationId);
            throw;
        }
        catch (Exception error)
        {
            await ReleaseMedusaDailyEntryAsync(leaderReservationId);
            Console.Error.WriteLine(
                "[instance-caller] Medusa runtime creation failed: " +
                error.Message);
            return MedusaPartyEntryStatus.RuntimeUnavailable;
        }

        var targetInstanceId = creation.InstanceId;
        if (creation.Status !=
                WorldInstanceRuntimeDirectoryStatus.Created ||
            targetInstanceId is null)
        {
            await ReleaseMedusaDailyEntryAsync(leaderReservationId);
            return MedusaPartyEntryStatus.RuntimeUnavailable;
        }

        var invitations = PrepareMedusaMemberInvitations(
            party,
            eligibleMembers,
            difficulty,
            targetInstanceId.Value,
            startedAt);
        var leader = party.Members[0];
        var leaderCommand = new MedusaInstanceTransitionCommand(
            leader.CharacterId,
            leader.SourceWorldInstanceId,
            leader.SourceMapId,
            leader.Ownership,
            targetInstanceId.Value,
            checked((byte)targetMapId),
            entrance.X,
            entrance.Z);
        bool leaderMoved;
        try
        {
            leaderMoved = await TryBeginMedusaInstanceTransitionAsync(
                leaderCommand,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CancelPreparedMedusaInvitations(invitations);
            await ReleaseMedusaDailyEntryAsync(leaderReservationId);
            throw;
        }
        catch (Exception error)
        {
            CancelPreparedMedusaInvitations(invitations);
            await ReleaseMedusaDailyEntryAsync(leaderReservationId);
            Console.Error.WriteLine(
                "[instance-caller] Medusa leader transfer fault: " +
                error.Message);
            return MedusaPartyEntryStatus.TransferFailed;
        }
        if (!leaderMoved)
        {
            CancelPreparedMedusaInvitations(invitations);
            await ReleaseMedusaDailyEntryAsync(leaderReservationId);
            return MedusaPartyEntryStatus.TransferFailed;
        }
        if (!_registry.TryRegisterMedusaLeaderUi(
                targetInstanceId.Value,
                _session,
                leader.CharacterId,
                dailyEntryLimit.Value))
        {
            Console.Error.WriteLine(
                "[instance-caller] Medusa leader UI registration failed " +
                $"character={leader.CharacterName} instance=" +
                targetInstanceId.Value);
        }

        var publishedInvitations = 0;
        foreach (var invitation in invitations)
        {
            if (!await PublishMedusaInvitationNoticeAsync(
                    invitation,
                    CancellationToken.None))
            {
                _registry.CancelMedusaInvitation(
                    invitation.InvitationId);
                await PublishMedusaInvitationResetAsync(
                    invitation,
                    CancellationToken.None);
                continue;
            }

            publishedInvitations++;
            _ = MonitorMedusaInvitationTimeoutAsync(
                invitation,
                CancellationToken.None);
        }

        Console.WriteLine(
            "[instance-caller] Medusa leader admitted " +
            $"leader={leader.CharacterName} difficulty={difficulty} " +
            $"instance={targetInstanceId.Value} " +
            $"member-confirmations={publishedInvitations} " +
            $"daily-ineligible={party.Members.Count - eligibleMembers.Length}");
        return MedusaPartyEntryStatus.Ready;
    }

    private List<MedusaInstanceInvitation> PrepareMedusaMemberInvitations(
        MedusaInstancePartySnapshot party,
        IReadOnlyCollection<MedusaInstancePartyMember> eligibleMembers,
        MedusaEncounterDifficulty difficulty,
        WorldInstanceId targetInstanceId,
        DateTimeOffset now)
    {
        var invitations = new List<MedusaInstanceInvitation>();
        foreach (var member in eligibleMembers)
        {
            if (member.CharacterId == party.LeaderCharacterId)
            {
                continue;
            }

            var status = _registry.TryBeginMedusaInvitation(
                _session,
                party,
                member,
                difficulty,
                targetInstanceId,
                now,
                out var invitation);
            if (status == MedusaPartyEntryStatus.Ready)
            {
                invitations.Add(invitation);
            }
            else
            {
                Console.WriteLine(
                    "[instance-caller] skipped Medusa member " +
                    $"confirmation character={member.CharacterName} " +
                    $"status={status}");
            }
        }
        return invitations;
    }

    private void CancelPreparedMedusaInvitations(
        IEnumerable<MedusaInstanceInvitation> invitations)
    {
        foreach (var invitation in invitations)
        {
            _registry.CancelMedusaInvitation(invitation.InvitationId);
        }
    }
}
