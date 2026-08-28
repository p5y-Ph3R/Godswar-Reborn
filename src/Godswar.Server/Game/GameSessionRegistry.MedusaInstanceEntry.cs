using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<ClientSession, Func<
        MedusaInstanceTransitionCommand,
        CancellationToken,
        Task<bool>>> _instanceTransitionSinks = [];

    private readonly Dictionary<(int RealmId, DateOnly Day, int CharacterId),
        Guid> _localMedusaDailyEntries = [];

    internal void RegisterInstanceTransitionSink(
        ClientSession session,
        Func<MedusaInstanceTransitionCommand, CancellationToken, Task<bool>>
            sink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sink);
        if (!_instanceTransitionSinks.TryAdd(session, sink))
        {
            throw new InvalidOperationException(
                "The session already has an instance-transition sink.");
        }
    }

    internal void UnregisterInstanceTransitionSink(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _instanceTransitionSinks.TryRemove(session, out _);
    }

    internal async Task<bool> TransitionPartyMemberToInstanceAsync(
        ClientSession session,
        MedusaInstanceTransitionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _instanceTransitionSinks.TryGetValue(session, out var sink) &&
            await sink(command, cancellationToken);
    }

    internal MedusaPartyEntryStatus TryCaptureMedusaParty(
        ClientSession requestingSession,
        out MedusaInstancePartySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(requestingSession);
        lock (_gate)
        {
            snapshot = null!;
            if (!_sessions.TryGetValue(
                    requestingSession,
                    out var leader) ||
                !IsEntryReady(leader))
            {
                return MedusaPartyEntryStatus.PartyUnavailable;
            }

            PartyState? party = null;
            if (_partiesByCharacter.TryGetValue(
                    leader.CharacterId,
                    out party))
            {
                NormalizePartyLocked(party);
                if (!_partiesByCharacter.TryGetValue(
                        leader.CharacterId,
                        out party))
                {
                    party = null;
                }
                else if (party.MemberCharacterIds[0] != leader.CharacterId)
                {
                    return MedusaPartyEntryStatus.LeaderRequired;
                }
            }

            var characterIds = party?.MemberCharacterIds.ToArray() ??
                [leader.CharacterId];
            if (characterIds.Length is <
                    MedusaIslandPolicy.MinimumPartySize or >
                    MedusaIslandPolicy.MaximumPartySize)
            {
                return MedusaPartyEntryStatus.PartyUnavailable;
            }

            var members = new List<MedusaInstancePartyMember>(
                characterIds.Length);
            foreach (var characterId in characterIds)
            {
                if (!TryFindCurrentContextLocked(
                        characterId,
                        out var member) ||
                    !IsEntryReady(member) ||
                    member.RealmId != leader.RealmId ||
                    member.Session != requestingSession &&
                    !_instanceTransitionSinks.ContainsKey(member.Session))
                {
                    return MedusaPartyEntryStatus.PartyUnavailable;
                }
                if (member.Character.Level < MedusaIslandPolicy.MinimumLevel)
                {
                    return MedusaPartyEntryStatus.LevelRequirementNotMet;
                }

                members.Add(new(
                    member.Session,
                    member.AccountId,
                    member.CharacterId,
                    member.CharacterName,
                    member.Character.Level,
                    member.RealmId,
                    member.WorldInstanceId,
                    member.MapId,
                    member.Ownership));
            }

            snapshot = new(
                party?.Id,
                leader.RealmId,
                leader.CharacterId,
                members.ToArray());
            return MedusaPartyEntryStatus.Ready;
        }
    }

    internal bool TryReserveLocalMedusaDailyEntry(
        Guid reservationId,
        RealmId realmId,
        DateOnly day,
        IReadOnlyCollection<int> characterIds)
    {
        if (reservationId == Guid.Empty || !realmId.IsValid)
        {
            throw new ArgumentException(
                "A local Medusa reservation requires valid identities.");
        }
        ArgumentNullException.ThrowIfNull(characterIds);
        lock (_gate)
        {
            foreach (var stale in _localMedusaDailyEntries.Keys
                         .Where(key => key.RealmId == realmId.Value &&
                             key.Day != day)
                         .ToArray())
            {
                _localMedusaDailyEntries.Remove(stale);
            }

            var keys = characterIds.Distinct()
                .Select(characterId =>
                    (realmId.Value, day, characterId))
                .ToArray();
            if (keys.Length != characterIds.Count ||
                keys.Any(_localMedusaDailyEntries.ContainsKey))
            {
                return false;
            }
            foreach (var key in keys)
            {
                _localMedusaDailyEntries.Add(key, reservationId);
            }
            return true;
        }
    }

    internal IReadOnlySet<int> FindUsedLocalMedusaDailyEntryCharacters(
        RealmId realmId,
        DateOnly day,
        IReadOnlyCollection<int> characterIds)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        ArgumentNullException.ThrowIfNull(characterIds);
        lock (_gate)
        {
            foreach (var stale in _localMedusaDailyEntries.Keys
                         .Where(key => key.RealmId == realmId.Value &&
                             key.Day != day)
                         .ToArray())
            {
                _localMedusaDailyEntries.Remove(stale);
            }

            return characterIds
                .Where(characterId =>
                    _localMedusaDailyEntries.ContainsKey(
                        (realmId.Value, day, characterId)))
                .ToHashSet();
        }
    }

    internal void ReleaseLocalMedusaDailyEntry(Guid reservationId)
    {
        if (reservationId == Guid.Empty)
        {
            return;
        }
        lock (_gate)
        {
            foreach (var key in _localMedusaDailyEntries
                         .Where(pair => pair.Value == reservationId)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                _localMedusaDailyEntries.Remove(key);
            }
        }
    }

    private bool TryFindCurrentContextLocked(
        int characterId,
        out GameSessionContext context)
    {
        context = null!;
        foreach (var candidate in _sessions.Values)
        {
            if (candidate.CharacterId != characterId ||
                candidate.Session.IsDisconnected)
            {
                continue;
            }
            if (context is not null)
            {
                context = null!;
                return false;
            }
            context = candidate;
        }
        return context is not null;
    }

    private bool IsEntryReady(GameSessionContext context) =>
        context.WorldReady &&
        !context.Session.IsDisconnected &&
        context.Character.CurrentMap == context.MapId &&
        context.MapId is not (200 or 204) &&
        context.Ownership.IsValid &&
        IsCurrentAccountSession(
            context.AccountId,
            context.Session,
            context.Ownership);
}
