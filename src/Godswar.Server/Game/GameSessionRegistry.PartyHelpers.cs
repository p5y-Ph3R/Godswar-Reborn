using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal IReadOnlyList<PartyDelivery> GetPartyRefreshDeliveries(
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
                return [];
            }

            NormalizePartyLocked(party);
            return _partiesByCharacter.TryGetValue(
                    actor.CharacterId,
                    out party)
                ? BuildPartyRefreshDeliveriesLocked(party)
                : [];
        }
    }

    private bool TryGetPartyActorLocked(
        ClientSession session,
        string claimedName,
        out GameSessionContext actor,
        out PartyOperationStatus status)
    {
        if (!_sessions.TryGetValue(session, out actor!) ||
            !actor.WorldReady ||
            session.IsDisconnected)
        {
            status = PartyOperationStatus.ActorUnavailable;
            return false;
        }
        if (!NamesEqual(actor.CharacterName, claimedName))
        {
            status = PartyOperationStatus.InvalidActorName;
            return false;
        }

        status = PartyOperationStatus.Applied;
        return true;
    }

    private bool TryGetLedPartyLocked(
        GameSessionContext actor,
        out PartyState party,
        out PartyOperationStatus status)
    {
        if (!_partiesByCharacter.TryGetValue(
                actor.CharacterId,
                out party!))
        {
            status = PartyOperationStatus.InvalidTarget;
            return false;
        }

        NormalizePartyLocked(party);
        if (!_partiesByCharacter.TryGetValue(
                actor.CharacterId,
                out party!) ||
            party.MemberCharacterIds[0] != actor.CharacterId)
        {
            status = PartyOperationStatus.NotLeader;
            return false;
        }

        status = PartyOperationStatus.Applied;
        return true;
    }

    private bool TryFindOnlinePartyMemberLocked(
        string name,
        Domain.World.Instances.RealmId realmId,
        out GameSessionContext context)
    {
        context = null!;
        foreach (var candidate in _sessions.Values)
        {
            if (candidate.WorldReady &&
                !candidate.Session.IsDisconnected &&
                candidate.RealmId == realmId &&
                NamesEqual(candidate.CharacterName, name))
            {
                if (context is not null)
                {
                    context = null!;
                    return false;
                }

                context = candidate;
            }
        }

        return context is not null;
    }

    private bool TryFindPartyMemberLocked(
        PartyState party,
        string name,
        out GameSessionContext context)
    {
        context = null!;
        foreach (var characterId in party.MemberCharacterIds)
        {
            if (TryFindOnlinePartyMemberByIdLocked(
                    characterId,
                    out var candidate) &&
                NamesEqual(candidate.CharacterName, name))
            {
                context = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryFindOnlinePartyMemberByIdLocked(
        int characterId,
        out GameSessionContext context)
    {
        foreach (var candidate in _sessions.Values)
        {
            if (candidate.CharacterId == characterId &&
                !candidate.Session.IsDisconnected)
            {
                context = candidate;
                return true;
            }
        }

        context = null!;
        return false;
    }

    private PartyOperationResult RemoveMemberLocked(
        PartyState party,
        GameSessionContext removed,
        bool notifyRemovedMember)
    {
        party.MemberCharacterIds.Remove(removed.CharacterId);
        _partiesByCharacter.Remove(removed.CharacterId);
        RemovePartyInvitationsForLocked(removed.CharacterId);

        var deliveries = new List<PartyDelivery>();
        if (notifyRemovedMember)
        {
            deliveries.Add(new PartyDelivery(
                removed.Session,
                PacketBuilder.PartyAction(
                    Opcodes.PartyRemove,
                    removed.ObjectId,
                    removed.CharacterName,
                    removed.CharacterName),
                "PartyRemove"));
        }

        if (party.MemberCharacterIds.Count < 2)
        {
            deliveries.AddRange(BuildPartyResetDeliveriesLocked(
                party,
                Opcodes.PartyDestroy,
                removed));
            RemovePartyLocked(party);
        }
        else
        {
            deliveries.AddRange(BuildPartyRefreshDeliveriesLocked(party));
        }

        return Applied(deliveries);
    }

    private List<PartyDelivery> BuildPartyRefreshDeliveriesLocked(
        PartyState party)
    {
        var members = party.MemberCharacterIds
            .Select(characterId =>
                TryFindOnlinePartyMemberByIdLocked(
                    characterId,
                    out var context)
                    ? context
                    : null)
            .Where(static context => context is not null)
            .Cast<GameSessionContext>()
            .ToArray();
        var snapshots = members
            .Select(static member => new PartyMemberSnapshot(
                member.CharacterId,
                member.ObjectId,
                member.Character.CurrentHp,
                member.Character.MaxHp,
                member.Character.Level,
                member.Character.Profession,
                member.CharacterName,
                member.MapId,
                member.Character.PositionX,
                member.Character.PositionZ))
            .ToArray();
        return members
            .Select(member => new PartyDelivery(
                member.Session,
                PacketBuilder.PartyRefresh(
                    snapshots,
                    member.CharacterId),
                "PartyRefresh"))
            .ToList();
    }

    private List<PartyDelivery> BuildPartyResetDeliveriesLocked(
        PartyState party,
        ushort opcode,
        GameSessionContext actor)
    {
        var deliveries = new List<PartyDelivery>();
        foreach (var characterId in party.MemberCharacterIds)
        {
            if (TryFindOnlinePartyMemberByIdLocked(
                    characterId,
                    out var member))
            {
                deliveries.Add(new PartyDelivery(
                    member.Session,
                    PacketBuilder.PartyAction(
                        opcode,
                        actor.ObjectId,
                        actor.CharacterName,
                        member.CharacterName),
                    "PartyDestroy"));
            }
        }

        return deliveries;
    }

    private void NormalizePartyLocked(PartyState party)
    {
        foreach (var characterId in party.MemberCharacterIds.ToArray())
        {
            if (!TryFindOnlinePartyMemberByIdLocked(characterId, out _))
            {
                party.MemberCharacterIds.Remove(characterId);
                _partiesByCharacter.Remove(characterId);
                RemovePartyInvitationsForLocked(characterId);
            }
        }

        if (party.MemberCharacterIds.Count < 2)
        {
            RemovePartyLocked(party);
        }
    }

    private void RemovePartyLocked(PartyState party)
    {
        foreach (var characterId in party.MemberCharacterIds)
        {
            _partiesByCharacter.Remove(characterId);
            RemovePartyInvitationsForLocked(characterId);
        }

        party.MemberCharacterIds.Clear();
    }

    private void RemovePartySessionLocked(int characterId)
    {
        RemoveMedusaInvitationForCharacterLocked(characterId);
        RemovePartyInvitationsForLocked(characterId);
        if (!_partiesByCharacter.TryGetValue(
                characterId,
                out var party))
        {
            return;
        }

        party.MemberCharacterIds.Remove(characterId);
        _partiesByCharacter.Remove(characterId);
        if (party.MemberCharacterIds.Count < 2)
        {
            RemovePartyLocked(party);
        }
    }

    private void RemovePartyInvitationsForLocked(int characterId)
    {
        foreach (var key in _partyInvitations.Keys
                     .Where(key =>
                         key.InviterCharacterId == characterId ||
                         key.TargetCharacterId == characterId)
                     .ToArray())
        {
            _partyInvitations.Remove(key);
        }
    }

    private void PrunePartyInvitationsLocked(DateTimeOffset now)
    {
        foreach (var key in _partyInvitations
                     .Where(pair => pair.Value <= now)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _partyInvitations.Remove(key);
        }
    }

    private static bool NamesEqual(string left, string right) =>
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static PartyOperationResult Applied(
        params PartyDelivery[] deliveries) =>
        new(PartyOperationStatus.Applied, deliveries);

    private static PartyOperationResult Applied(
        IReadOnlyList<PartyDelivery> deliveries) =>
        new(PartyOperationStatus.Applied, deliveries);
}
