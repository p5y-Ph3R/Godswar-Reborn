using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan PartyInvitationLifetime =
        TimeSpan.FromMinutes(1);
    private readonly Dictionary<int, PartyState> _partiesByCharacter = [];
    private readonly Dictionary<PartyInvitationKey, DateTimeOffset>
        _partyInvitations = [];
    private long _nextPartyId;

    internal PartyOperationResult InvitePartyMember(
        ClientSession actorSession,
        string actorName,
        string targetName,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            PrunePartyInvitationsLocked(now);
            if (!TryGetPartyActorLocked(
                    actorSession,
                    actorName,
                    out var actor,
                    out var status))
            {
                return PartyOperationResult.Rejected(status);
            }

            if (!TryFindOnlinePartyMemberLocked(
                    targetName,
                    actor.RealmId,
                    out var target))
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.TargetUnavailable);
            }
            if (target.CharacterId == actor.CharacterId)
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.InvalidTarget);
            }
            if (_partiesByCharacter.ContainsKey(target.CharacterId))
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.AlreadyInParty);
            }

            if (_partiesByCharacter.TryGetValue(
                    actor.CharacterId,
                    out var party))
            {
                NormalizePartyLocked(party);
                if (!_partiesByCharacter.TryGetValue(
                        actor.CharacterId,
                        out party))
                {
                    party = null;
                }
                else if (party.MemberCharacterIds[0] != actor.CharacterId)
                {
                    return PartyOperationResult.Rejected(
                        PartyOperationStatus.NotLeader);
                }
                else if (party.MemberCharacterIds.Count >=
                         PartyProtocol.MaximumMembers)
                {
                    return PartyOperationResult.Rejected(
                        PartyOperationStatus.PartyFull);
                }
            }

            _partyInvitations[new PartyInvitationKey(
                actor.CharacterId,
                target.CharacterId)] = now + PartyInvitationLifetime;
            return Applied(
                new PartyDelivery(
                    target.Session,
                    PacketBuilder.PartyAction(
                        Opcodes.PartyInvite,
                        actor.ObjectId,
                        actor.CharacterName,
                        target.CharacterName),
                    "PartyInvite"));
        }
    }

    internal PartyOperationResult AcceptPartyInvite(
        ClientSession actorSession,
        string inviterName,
        string actorName,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            PrunePartyInvitationsLocked(now);
            if (!TryGetPartyActorLocked(
                    actorSession,
                    actorName,
                    out var actor,
                    out var status))
            {
                return PartyOperationResult.Rejected(status);
            }
            if (_partiesByCharacter.ContainsKey(actor.CharacterId))
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.AlreadyInParty);
            }
            if (!TryFindOnlinePartyMemberLocked(
                    inviterName,
                    actor.RealmId,
                    out var inviter))
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.TargetUnavailable);
            }

            var invitationKey = new PartyInvitationKey(
                inviter.CharacterId,
                actor.CharacterId);
            if (!_partyInvitations.Remove(invitationKey))
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.InvitationMissing);
            }

            PartyState party;
            if (_partiesByCharacter.TryGetValue(
                    inviter.CharacterId,
                    out var existingParty))
            {
                NormalizePartyLocked(existingParty);
                if (!_partiesByCharacter.TryGetValue(
                        inviter.CharacterId,
                        out party!))
                {
                    return PartyOperationResult.Rejected(
                        PartyOperationStatus.InvitationMissing);
                }
                if (party.MemberCharacterIds[0] != inviter.CharacterId)
                {
                    return PartyOperationResult.Rejected(
                        PartyOperationStatus.NotLeader);
                }
                if (party.MemberCharacterIds.Count >=
                    PartyProtocol.MaximumMembers)
                {
                    return PartyOperationResult.Rejected(
                        PartyOperationStatus.PartyFull);
                }
            }
            else
            {
                party = new PartyState(
                    checked(++_nextPartyId),
                    [inviter.CharacterId]);
                _partiesByCharacter.Add(inviter.CharacterId, party);
            }

            party.MemberCharacterIds.Add(actor.CharacterId);
            _partiesByCharacter.Add(actor.CharacterId, party);
            RemovePartyInvitationsForLocked(actor.CharacterId);
            return Applied(BuildPartyRefreshDeliveriesLocked(party));
        }
    }

    internal PartyOperationResult RejectPartyInvite(
        ClientSession actorSession,
        string inviterName,
        string actorName,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            PrunePartyInvitationsLocked(now);
            if (!TryGetPartyActorLocked(
                    actorSession,
                    actorName,
                    out var actor,
                    out var status))
            {
                return PartyOperationResult.Rejected(status);
            }
            if (!TryFindOnlinePartyMemberLocked(
                    inviterName,
                    actor.RealmId,
                    out var inviter) ||
                !_partyInvitations.Remove(new PartyInvitationKey(
                    inviter.CharacterId,
                    actor.CharacterId)))
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.InvitationMissing);
            }

            return Applied();
        }
    }

    internal PartyOperationResult RemovePartyMember(
        ClientSession actorSession,
        string actorName,
        string targetName)
    {
        lock (_gate)
        {
            if (!TryGetPartyActorLocked(
                    actorSession,
                    actorName,
                    out var actor,
                    out var status))
            {
                return PartyOperationResult.Rejected(status);
            }
            if (!TryGetLedPartyLocked(actor, out var party, out status))
            {
                return PartyOperationResult.Rejected(status);
            }
            if (!TryFindPartyMemberLocked(
                    party,
                    targetName,
                    out var target) ||
                target.CharacterId == actor.CharacterId)
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.InvalidTarget);
            }

            return RemoveMemberLocked(
                party,
                target,
                notifyRemovedMember: true);
        }
    }

    internal PartyOperationResult ChangePartyLeader(
        ClientSession actorSession,
        string actorName,
        string targetName)
    {
        lock (_gate)
        {
            if (!TryGetPartyActorLocked(
                    actorSession,
                    actorName,
                    out var actor,
                    out var status))
            {
                return PartyOperationResult.Rejected(status);
            }
            if (!TryGetLedPartyLocked(actor, out var party, out status))
            {
                return PartyOperationResult.Rejected(status);
            }
            if (!TryFindPartyMemberLocked(
                    party,
                    targetName,
                    out var target) ||
                target.CharacterId == actor.CharacterId)
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.InvalidTarget);
            }

            party.MemberCharacterIds.Remove(target.CharacterId);
            party.MemberCharacterIds.Insert(0, target.CharacterId);
            return Applied(BuildPartyRefreshDeliveriesLocked(party));
        }
    }

    internal PartyOperationResult DissolveParty(
        ClientSession actorSession,
        string actorName)
    {
        lock (_gate)
        {
            if (!TryGetPartyActorLocked(
                    actorSession,
                    actorName,
                    out var actor,
                    out var status))
            {
                return PartyOperationResult.Rejected(status);
            }
            if (!TryGetLedPartyLocked(actor, out var party, out status))
            {
                return PartyOperationResult.Rejected(status);
            }

            var deliveries = BuildPartyResetDeliveriesLocked(
                party,
                Opcodes.PartyDestroy,
                actor);
            RemovePartyLocked(party);
            return Applied(deliveries);
        }
    }

    internal PartyOperationResult LeaveParty(
        ClientSession actorSession,
        string actorName)
    {
        lock (_gate)
        {
            if (!TryGetPartyActorLocked(
                    actorSession,
                    actorName,
                    out var actor,
                    out var status))
            {
                return PartyOperationResult.Rejected(status);
            }
            if (!_partiesByCharacter.TryGetValue(
                    actor.CharacterId,
                    out var party))
            {
                return PartyOperationResult.Rejected(
                    PartyOperationStatus.InvalidTarget);
            }

            return RemoveMemberLocked(
                party,
                actor,
                notifyRemovedMember: true);
        }
    }

    internal PartyOperationResult RemovePartySession(
        ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                !_partiesByCharacter.TryGetValue(
                    context.CharacterId,
                    out var party))
            {
                return Applied();
            }

            return RemoveMemberLocked(
                party,
                context,
                notifyRemovedMember: false);
        }
    }

    internal bool CanInitiateInstance(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var actor))
            {
                return false;
            }
            if (!_partiesByCharacter.TryGetValue(
                    actor.CharacterId,
                    out var party))
            {
                return true;
            }

            NormalizePartyLocked(party);
            return !_partiesByCharacter.TryGetValue(
                       actor.CharacterId,
                       out party) ||
                   party.MemberCharacterIds[0] == actor.CharacterId;
        }
    }

    internal PartyMembershipSnapshot? GetPartyMembership(
        ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var actor) ||
                !_partiesByCharacter.TryGetValue(
                    actor.CharacterId,
                    out var party))
            {
                return null;
            }

            NormalizePartyLocked(party);
            return _partiesByCharacter.TryGetValue(
                actor.CharacterId,
                out party)
                ? new PartyMembershipSnapshot(
                    party.Id,
                    party.MemberCharacterIds[0] == actor.CharacterId,
                    party.MemberCharacterIds.ToArray())
                : null;
        }
    }

    private sealed class PartyState(
        long id,
        List<int> memberCharacterIds)
    {
        public long Id { get; } = id;

        public List<int> MemberCharacterIds { get; } =
            memberCharacterIds;
    }

    private readonly record struct PartyInvitationKey(
        int InviterCharacterId,
        int TargetCharacterId);
}
