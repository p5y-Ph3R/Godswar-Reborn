using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const float MonsterLootPickupRadius = 12f;

    private readonly ConcurrentDictionary<
        MonsterLootRuntimeKey,
        MonsterLootRuntimeState> _monsterLoot = [];

    internal bool TryResolveMedusaMonsterRule(
        ClientSession session,
        MonsterDamageResult damage,
        out MedusaMonsterRule rule)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(damage);
        rule = default;
        if (!_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != damage.Monster.Definition.MapId ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        var resolved = InvokeWorldOwner(
            runtime,
            map =>
            {
                var found = map.TryResolveMedusaMonsterRule(
                    damage.ObjectId,
                    damage.Monster.SpawnGeneration,
                    out var value);
                return (Found: found, Rule: value);
            });
        rule = resolved.Rule;
        return resolved.Found;
    }

    internal MonsterLootPresentation? PrepareMedusaMonsterLoot(
        ClientSession session,
        MonsterDamageResult damage,
        Guid deathEventId,
        DateTimeOffset diedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(damage);
        if (deathEventId == Guid.Empty || !damage.Killed ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != damage.Monster.Definition.MapId ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return null;
        }

        var prepared = InvokeWorldOwner(
            runtime,
            map =>
            {
                if (!map.TryResolveMedusaMonsterRule(
                        damage.ObjectId,
                        damage.Monster.SpawnGeneration,
                        out var rule))
                {
                    return default(PreparedMonsterLoot?);
                }

                var rolled = MedusaMonsterContentCatalog.Current.RollLoot(
                    rule.Difficulty,
                    rule.TemplateAlias,
                    deathEventId);
                var entries = rolled.Select((drop, index) =>
                        new MonsterLootEntry(
                            index,
                            drop.LootIndex,
                            drop.ItemId,
                            drop.Quantity))
                    .ToArray();
                var delayMilliseconds = entries.Length == 0
                    ? rule.CorpseWithoutLootMilliseconds
                    : rule.CorpseWithLootMilliseconds;
                DateTimeOffset? expiresAt = delayMilliseconds.HasValue
                    ? diedAt + TimeSpan.FromMilliseconds(
                        delayMilliseconds.Value)
                    : null;
                if (!map.TrySetMonsterCorpseDespawnAt(
                        damage.ObjectId,
                        damage.Monster.SpawnGeneration,
                        expiresAt))
                {
                    return default(PreparedMonsterLoot?);
                }
                return new PreparedMonsterLoot(rule, entries, expiresAt);
            });
        if (prepared is null)
        {
            return null;
        }

        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            damage.ObjectId);
        if (prepared.Entries.Count == 0)
        {
            _monsterLoot.TryRemove(key, out _);
            return new(
                damage.ObjectId,
                damage.Monster.SpawnGeneration,
                deathEventId,
                []);
        }

        var state = new MonsterLootRuntimeState(
            context.CharacterId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            prepared.ExpiresAt,
            prepared.Entries);
        _monsterLoot[key] = state;
        return new(
            damage.ObjectId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            prepared.Entries);
    }

    internal bool TryReserveMonsterLootPickup(
        ClientSession session,
        uint monsterObjectId,
        int pickupIndex,
        DateTimeOffset now,
        out MonsterLootPickupReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(session);
        reservation = default!;
        if (monsterObjectId == 0 || pickupIndex is < 0 or >= 32 ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            monsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state))
        {
            return false;
        }

        var monsterAttempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                var found = map.TryGetMonsterSnapshot(
                    monsterObjectId,
                    out var monster);
                return (Found: found, Monster: monster);
            });
        var target = monsterAttempt.Monster;
        if (!monsterAttempt.Found || target.IsAlive || !target.IsSpawned ||
            target.SpawnGeneration != state.SpawnGeneration ||
            DistanceSquared(
                context.Character.PositionX,
                context.Character.PositionZ,
                target.X,
                target.Z) >
                MonsterLootPickupRadius * MonsterLootPickupRadius)
        {
            return false;
        }

        lock (state.Gate)
        {
            if (state.ClaimantCharacterId != context.CharacterId ||
                state.ExpiresAt is { } expiresAt && now >= expiresAt ||
                !state.Entries.TryGetValue(pickupIndex, out var entry) ||
                state.Pending.ContainsKey(pickupIndex))
            {
                if (state.ExpiresAt is { } expired && now >= expired)
                {
                    _monsterLoot.TryRemove(
                        new KeyValuePair<
                            MonsterLootRuntimeKey,
                            MonsterLootRuntimeState>(key, state));
                }
                return false;
            }

            var attemptId = Guid.NewGuid();
            state.Pending.Add(pickupIndex, attemptId);
            reservation = new(
                key.WorldInstanceId,
                monsterObjectId,
                state.SpawnGeneration,
                state.DeathEventId,
                pickupIndex,
                entry.RuleLootIndex,
                entry.ItemId,
                entry.Quantity,
                attemptId);
            return true;
        }
    }

    internal void CompleteMonsterLootPickup(
        MonsterLootPickupReservation reservation)
    {
        var key = new MonsterLootRuntimeKey(
            reservation.WorldInstanceId,
            reservation.MonsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            if (!state.Pending.TryGetValue(
                    reservation.PickupIndex,
                    out var attemptId) ||
                attemptId != reservation.AttemptId)
            {
                return;
            }
            state.Pending.Remove(reservation.PickupIndex);
            state.Entries.Remove(reservation.PickupIndex);
            if (state.Entries.Count == 0)
            {
                _monsterLoot.TryRemove(
                    new KeyValuePair<
                        MonsterLootRuntimeKey,
                        MonsterLootRuntimeState>(key, state));
            }
        }
    }

    internal void ReleaseMonsterLootPickup(
        MonsterLootPickupReservation reservation)
    {
        var key = new MonsterLootRuntimeKey(
            reservation.WorldInstanceId,
            reservation.MonsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state))
        {
            return;
        }
        lock (state.Gate)
        {
            if (state.Pending.TryGetValue(
                    reservation.PickupIndex,
                    out var attemptId) &&
                attemptId == reservation.AttemptId)
            {
                state.Pending.Remove(reservation.PickupIndex);
            }
        }
    }

    private static float DistanceSquared(
        float leftX,
        float leftZ,
        float rightX,
        float rightZ)
    {
        var deltaX = leftX - rightX;
        var deltaZ = leftZ - rightZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private readonly record struct MonsterLootRuntimeKey(
        WorldInstanceId WorldInstanceId,
        uint MonsterObjectId);

    private sealed class MonsterLootRuntimeState
    {
        public MonsterLootRuntimeState(
            int claimantCharacterId,
            uint spawnGeneration,
            Guid deathEventId,
            DateTimeOffset? expiresAt,
            IReadOnlyList<MonsterLootEntry> entries)
        {
            ClaimantCharacterId = claimantCharacterId;
            SpawnGeneration = spawnGeneration;
            DeathEventId = deathEventId;
            ExpiresAt = expiresAt;
            Entries = entries.ToDictionary(
                static entry => entry.PickupIndex);
        }

        public object Gate { get; } = new();
        public int ClaimantCharacterId { get; }
        public uint SpawnGeneration { get; }
        public Guid DeathEventId { get; }
        public DateTimeOffset? ExpiresAt { get; }
        public Dictionary<int, MonsterLootEntry> Entries { get; }
        public Dictionary<int, Guid> Pending { get; } = [];
    }

    private sealed record PreparedMonsterLoot(
        MedusaMonsterRule Rule,
        IReadOnlyList<MonsterLootEntry> Entries,
        DateTimeOffset? ExpiresAt);
}

internal readonly record struct MonsterLootEntry(
    int PickupIndex,
    int RuleLootIndex,
    uint ItemId,
    int Quantity);

internal sealed record MonsterLootPresentation(
    uint MonsterObjectId,
    uint SpawnGeneration,
    Guid DeathEventId,
    IReadOnlyList<MonsterLootEntry> Entries);

internal sealed record MonsterLootPickupReservation(
    WorldInstanceId WorldInstanceId,
    uint MonsterObjectId,
    uint SpawnGeneration,
    Guid DeathEventId,
    int PickupIndex,
    int RuleLootIndex,
    uint ItemId,
    int Quantity,
    Guid AttemptId);
