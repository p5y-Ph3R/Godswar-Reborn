using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private void AddToMap(GameSessionContext context)
    {
        GetOrCreateMap(context.MapId).AddOrUpdate(context);
    }

    private MapInstance.PlayerTransfer StageMapTransfer(
        GameSessionContext context) =>
        GetOrCreateMap(context.MapId).StagePlayerTransfer(context);

    private MapInstance.PlayerTransfer StageMapTransfer(
        GameSessionContext context,
        byte targetMapId,
        float targetX,
        float targetZ) =>
        GetOrCreateMap(context.MapId).StagePlayerTransfer(
            context,
            new PlayerTransformOverride(
                targetMapId,
                targetX,
                targetZ));

    private MapInstance GetOrCreateMap(byte mapId) =>
        _maps.GetOrAdd(
            mapId,
            mapId => new MapInstance(
                mapId,
                _monsterRuntimeMode,
                _playerRuntimeMode));

    private void EnsureMapObjectIdAvailable(GameSessionContext context)
    {
        if (!_maps.TryGetValue(context.MapId, out var map))
        {
            return;
        }

        var collision = map.Snapshot()
            .FirstOrDefault(candidate =>
                !ReferenceEquals(candidate.Session, context.Session) &&
                candidate.ObjectId == context.ObjectId);
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"World object ID {context.ObjectId} is already assigned to character {collision.CharacterName} on map {context.MapId}.");
        }
    }

    private void RemoveFromMap(GameSessionContext context)
    {
        if (_maps.TryGetValue(context.MapId, out var map))
        {
            map.Remove(context.Session, out _);
            map.ClearMonsterAggroForCharacter(context.CharacterId, DateTimeOffset.UtcNow);
        }
    }

    private sealed class PlayerStatusState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Dictionary<int, ActiveRuntimeStatus> RuntimeStatuses { get; } = [];

        public ExperienceBoostState ExperienceBoosts { get; set; } = ExperienceBoostState.Empty;

        public string? LastFingerprint { get; set; }

        public long Revision { get; set; }

        public CancellationTokenSource Lifetime { get; } = new();
    }

    private sealed class ZodiacOnlineSessionState(
        int accountId,
        int characterId,
        GameCharacter character,
        DateTimeOffset lastAccountedAt)
    {
        public int AccountId { get; } = accountId;

        public int CharacterId { get; } = characterId;

        public GameCharacter Character { get; set; } = character;

        public DateTimeOffset LastAccountedAt { get; set; } = lastAccountedAt;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class ProgressionBoostOnlineSessionState(
        int accountId,
        int characterId,
        DateTimeOffset lastAccountedAt)
    {
        public int AccountId { get; } = accountId;

        public int CharacterId { get; } = characterId;

        public DateTimeOffset LastAccountedAt { get; set; } = lastAccountedAt;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

internal readonly record struct MonsterAreaDamageBroadcastHit(
    MonsterHealthMutation HealthMutation,
    uint ReportedDamage);
