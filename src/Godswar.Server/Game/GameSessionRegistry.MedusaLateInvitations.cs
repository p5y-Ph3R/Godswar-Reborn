using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal MedusaPartyEntryStatus TryBeginLateMedusaInvitation(
        ClientSession inviteeSession,
        string inviterName,
        DateTimeOffset now,
        out MedusaInstanceInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(inviteeSession);
        invitation = null!;

        MedusaInstancePartySnapshot party;
        MedusaInstancePartyMember invitee;
        WorldInstanceId targetWorldInstanceId;
        lock (_gate)
        {
            if (!TryCaptureLateMedusaPartyLocked(
                    inviteeSession,
                    inviterName,
                    out party,
                    out invitee,
                    out targetWorldInstanceId))
            {
                return MedusaPartyEntryStatus.PartyUnavailable;
            }
        }

        if (!WorldInstances.TryFind(
                targetWorldInstanceId,
                out var runtime))
        {
            return MedusaPartyEntryStatus.RuntimeUnavailable;
        }
        var ownership = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                out var snapshot)
                ? snapshot
                : null);
        if (ownership is null ||
            ownership.Run.State != MedusaRunState.Active ||
            !ownership.Run.AdmittedCharacterIds.Contains(
                party.LeaderCharacterId) ||
            ownership.Run.AdmittedCharacterIds.Contains(
                invitee.CharacterId) ||
            ownership.Run.AdmittedCharacterIds.Count >=
                runtime.Descriptor.PlayerCapacity ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                ownership.ContentMapId.Value,
                out var clientSceneId))
        {
            return MedusaPartyEntryStatus.RuntimeUnavailable;
        }

        lock (_gate)
        {
            if (!TryCaptureLateMedusaPartyLocked(
                    inviteeSession,
                    inviterName,
                    out party,
                    out invitee,
                    out var currentTarget) ||
                currentTarget != targetWorldInstanceId)
            {
                return MedusaPartyEntryStatus.PartyUnavailable;
            }
            if (_medusaInvitationBySession.ContainsKey(inviteeSession))
            {
                return MedusaPartyEntryStatus.InvitationAlreadyPending;
            }

            var invitationId = NextMedusaInvitationIdLocked();
            invitation = new(
                clientSceneId,
                invitationId,
                ownership.Difficulty,
                party,
                invitee,
                targetWorldInstanceId,
                now + MedusaInvitationLifetime);
            _medusaInvitations.Add(invitationId, invitation);
            _medusaInvitationBySession.Add(
                inviteeSession,
                invitationId);
            return MedusaPartyEntryStatus.Ready;
        }
    }

    private bool TryCaptureLateMedusaPartyLocked(
        ClientSession inviteeSession,
        string inviterName,
        out MedusaInstancePartySnapshot partySnapshot,
        out MedusaInstancePartyMember inviteeSnapshot,
        out WorldInstanceId targetWorldInstanceId)
    {
        partySnapshot = null!;
        inviteeSnapshot = null!;
        targetWorldInstanceId = default;
        if (!_sessions.TryGetValue(inviteeSession, out var invitee) ||
            !IsEntryReady(invitee) ||
            invitee.Character.Level < MedusaIslandPolicy.MinimumLevel ||
            !_instanceTransitionSinks.ContainsKey(inviteeSession) ||
            !_partiesByCharacter.TryGetValue(
                invitee.CharacterId,
                out var party))
        {
            return false;
        }

        NormalizePartyLocked(party);
        if (!_partiesByCharacter.TryGetValue(
                invitee.CharacterId,
                out party) ||
            party.MemberCharacterIds.Count is < 2 or >
                MedusaIslandPolicy.MaximumPartySize ||
            !TryFindCurrentContextLocked(
                party.MemberCharacterIds[0],
                out var leader) ||
            leader.CharacterId == invitee.CharacterId ||
            !string.Equals(
                leader.CharacterName,
                inviterName,
                StringComparison.OrdinalIgnoreCase) ||
            !leader.WorldReady ||
            leader.Session.IsDisconnected ||
            leader.MapId is not (200 or 204) ||
            leader.RealmId != invitee.RealmId ||
            !leader.Ownership.IsValid ||
            !IsCurrentAccountSession(
                leader.AccountId,
                leader.Session,
                leader.Ownership))
        {
            return false;
        }

        var leaderSnapshot = ToMedusaPartyMember(leader);
        inviteeSnapshot = ToMedusaPartyMember(invitee);
        partySnapshot = new(
            party.Id,
            leader.RealmId,
            leader.CharacterId,
            [leaderSnapshot, inviteeSnapshot]);
        targetWorldInstanceId = leader.WorldInstanceId;
        return true;
    }

    private static MedusaInstancePartyMember ToMedusaPartyMember(
        GameSessionContext context) => new(
        context.Session,
        context.AccountId,
        context.CharacterId,
        context.CharacterName,
        context.Character.Level,
        context.RealmId,
        context.WorldInstanceId,
        context.MapId,
        context.Ownership);
}
