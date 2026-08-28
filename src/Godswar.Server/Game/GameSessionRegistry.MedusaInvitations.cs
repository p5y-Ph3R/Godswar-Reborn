using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal static readonly TimeSpan MedusaInvitationLifetime =
        TimeSpan.FromSeconds(60);

    private readonly Dictionary<int, MedusaInstanceInvitation>
        _medusaInvitations = [];
    private readonly Dictionary<ClientSession, int>
        _medusaInvitationBySession = [];
    private int _nextMedusaInvitationId;

    internal MedusaPartyEntryStatus TryBeginMedusaInvitation(
        ClientSession leaderSession,
        MedusaInstancePartySnapshot party,
        MedusaInstancePartyMember invitee,
        MedusaEncounterDifficulty difficulty,
        WorldInstanceId targetWorldInstanceId,
        DateTimeOffset now,
        out MedusaInstanceInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(leaderSession);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(invitee);
        lock (_gate)
        {
            invitation = null!;
            var status = ValidateMedusaPartyLocked(
                party,
                leaderSession);
            if (status != MedusaPartyEntryStatus.Ready)
            {
                return status;
            }
            if (!targetWorldInstanceId.IsValid ||
                invitee.CharacterId == party.LeaderCharacterId ||
                !party.Members.Contains(invitee) ||
                !MedusaIslandEncounterPolicy.TryGetDifficulty(
                    difficulty,
                    out var encounter) ||
                !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                    encounter.ContentMapId.Value,
                    out var clientSceneId))
            {
                return MedusaPartyEntryStatus.RuntimeUnavailable;
            }
            if (_medusaInvitationBySession.ContainsKey(invitee.Session))
            {
                return MedusaPartyEntryStatus.InvitationAlreadyPending;
            }

            var invitationId = NextMedusaInvitationIdLocked();
            invitation = new(
                clientSceneId,
                invitationId,
                difficulty,
                party,
                invitee,
                targetWorldInstanceId,
                now + MedusaInvitationLifetime);
            _medusaInvitations.Add(invitationId, invitation);
            _medusaInvitationBySession.Add(
                invitee.Session,
                invitationId);
            return MedusaPartyEntryStatus.Ready;
        }
    }

    internal MedusaInvitationResponseResult
        RecordMedusaInvitationResponse(
            ClientSession session,
            int clientSceneId,
            int invitationId,
            bool accepted,
            DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_medusaInvitationBySession.TryGetValue(
                    session,
                    out var expectedInvitationId) ||
                expectedInvitationId != invitationId ||
                !_medusaInvitations.TryGetValue(
                    invitationId,
                    out var invitation) ||
                invitation.ClientSceneId != clientSceneId ||
                !ReferenceEquals(invitation.Invitee.Session, session))
            {
                return new(
                    MedusaInvitationResponseStatus.Missing,
                    Invitation: null);
            }

            RemoveMedusaInvitationLocked(invitationId);
            if (invitation.ExpiresAt <= now)
            {
                return new(
                    MedusaInvitationResponseStatus.Expired,
                    invitation);
            }
            if (!ValidateMedusaInviteeLocked(invitation))
            {
                return new(
                    MedusaInvitationResponseStatus.PartyChanged,
                    invitation);
            }
            return new(
                accepted
                    ? MedusaInvitationResponseStatus.Ready
                    : MedusaInvitationResponseStatus.Declined,
                invitation);
        }
    }

    internal MedusaInstanceInvitation? ExpireMedusaInvitation(
        int invitationId,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_medusaInvitations.TryGetValue(
                    invitationId,
                    out var invitation) ||
                invitation.ExpiresAt > now)
            {
                return null;
            }

            RemoveMedusaInvitationLocked(invitationId);
            return invitation;
        }
    }

    internal MedusaInstanceInvitation? CancelMedusaInvitation(
        int invitationId)
    {
        lock (_gate)
        {
            if (!_medusaInvitations.TryGetValue(
                    invitationId,
                    out var invitation))
            {
                return null;
            }

            RemoveMedusaInvitationLocked(invitationId);
            return invitation;
        }
    }

    internal MedusaInstanceInvitation? CancelMedusaInvitationForSession(
        ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_medusaInvitationBySession.TryGetValue(
                    session,
                    out var invitationId) ||
                !_medusaInvitations.TryGetValue(
                    invitationId,
                    out var invitation))
            {
                return null;
            }

            RemoveMedusaInvitationLocked(invitationId);
            return invitation;
        }
    }

    private MedusaPartyEntryStatus ValidateMedusaPartyLocked(
        MedusaInstancePartySnapshot snapshot,
        ClientSession leaderSession)
    {
        if (snapshot.Members.Count is <
                MedusaIslandPolicy.MinimumPartySize or >
                MedusaIslandPolicy.MaximumPartySize ||
            snapshot.Members[0].CharacterId !=
                snapshot.LeaderCharacterId ||
            !ReferenceEquals(
                snapshot.Members[0].Session,
                leaderSession))
        {
            return MedusaPartyEntryStatus.PartyUnavailable;
        }

        if (snapshot.PartyId is { } partyId)
        {
            if (!_partiesByCharacter.TryGetValue(
                    snapshot.LeaderCharacterId,
                    out var party))
            {
                return MedusaPartyEntryStatus.PartyUnavailable;
            }
            NormalizePartyLocked(party);
            if (!_partiesByCharacter.TryGetValue(
                    snapshot.LeaderCharacterId,
                    out party) ||
                party.Id != partyId ||
                party.MemberCharacterIds[0] !=
                    snapshot.LeaderCharacterId ||
                !party.MemberCharacterIds.SequenceEqual(
                    snapshot.Members.Select(
                        static member => member.CharacterId)))
            {
                return MedusaPartyEntryStatus.PartyUnavailable;
            }
        }
        else if (snapshot.Members.Count != 1 ||
                 _partiesByCharacter.ContainsKey(
                     snapshot.LeaderCharacterId))
        {
            return MedusaPartyEntryStatus.PartyUnavailable;
        }

        foreach (var member in snapshot.Members)
        {
            if (!_sessions.TryGetValue(
                    member.Session,
                    out var current) ||
                !IsEntryReady(current) ||
                current.AccountId != member.AccountId ||
                current.CharacterId != member.CharacterId ||
                current.RealmId != snapshot.RealmId ||
                current.RealmId != member.RealmId ||
                current.WorldInstanceId !=
                    member.SourceWorldInstanceId ||
                current.MapId != member.SourceMapId ||
                current.Ownership != member.Ownership ||
                current.Character.Level <
                    MedusaIslandPolicy.MinimumLevel ||
                !ReferenceEquals(member.Session, leaderSession) &&
                !_instanceTransitionSinks.ContainsKey(member.Session))
            {
                return MedusaPartyEntryStatus.PartyUnavailable;
            }
        }

        return MedusaPartyEntryStatus.Ready;
    }

    private bool ValidateMedusaInviteeLocked(
        MedusaInstanceInvitation invitation)
    {
        var member = invitation.Invitee;
        if (invitation.Party.PartyId is not { } partyId ||
            !_partiesByCharacter.TryGetValue(
                member.CharacterId,
                out var party))
        {
            return false;
        }
        NormalizePartyLocked(party);
        if (!_partiesByCharacter.TryGetValue(
                member.CharacterId,
                out party) ||
            party.Id != partyId ||
            party.MemberCharacterIds.Count < 2 ||
            party.MemberCharacterIds[0] !=
                invitation.Party.LeaderCharacterId ||
            !party.MemberCharacterIds.Contains(member.CharacterId))
        {
            return false;
        }

        return TryFindCurrentContextLocked(
                invitation.Party.LeaderCharacterId,
                out var leader) &&
            !leader.Session.IsDisconnected &&
            leader.WorldInstanceId == invitation.TargetWorldInstanceId &&
            leader.MapId is 200 or 204 &&
            _sessions.TryGetValue(member.Session, out var current) &&
            IsEntryReady(current) &&
            current.AccountId == member.AccountId &&
            current.CharacterId == member.CharacterId &&
            current.RealmId == invitation.Party.RealmId &&
            current.RealmId == member.RealmId &&
            current.WorldInstanceId == member.SourceWorldInstanceId &&
            current.MapId == member.SourceMapId &&
            current.Ownership == member.Ownership &&
            current.Character.Level >= MedusaIslandPolicy.MinimumLevel &&
            _instanceTransitionSinks.ContainsKey(member.Session);
    }

    private int NextMedusaInvitationIdLocked()
    {
        for (var attempt = 0;
             attempt <= _medusaInvitations.Count;
             attempt++)
        {
            _nextMedusaInvitationId =
                _nextMedusaInvitationId == int.MaxValue
                    ? 1
                    : _nextMedusaInvitationId + 1;
            if (!_medusaInvitations.ContainsKey(
                    _nextMedusaInvitationId))
            {
                return _nextMedusaInvitationId;
            }
        }

        throw new InvalidOperationException(
            "No Medusa invitation identity is available.");
    }

    private void RemoveMedusaInvitationForCharacterLocked(
        int characterId)
    {
        var invitationId = _medusaInvitations
            .Where(pair =>
                pair.Value.Invitee.CharacterId == characterId)
            .Select(static pair => (int?)pair.Key)
            .FirstOrDefault();
        if (invitationId.HasValue)
        {
            RemoveMedusaInvitationLocked(invitationId.Value);
        }
    }

    private void RemoveMedusaInvitationLocked(int invitationId)
    {
        if (!_medusaInvitations.Remove(
                invitationId,
                out var invitation))
        {
            return;
        }
        if (_medusaInvitationBySession.TryGetValue(
                invitation.Invitee.Session,
                out var currentId) &&
            currentId == invitationId)
        {
            _medusaInvitationBySession.Remove(
                invitation.Invitee.Session);
        }
    }
}
