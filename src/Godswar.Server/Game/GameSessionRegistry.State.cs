using System.Collections.Concurrent;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private void AddToMap(GameSessionContext context)
    {
        var runtime = GetRequiredWorldInstance(context);
        InvokeWorldOwner(
            runtime,
            map => map.AddOrUpdate(context));
    }

    private WorldInstancePlayerTransfer StageMapTransfer(
        GameSessionContext context) =>
        StageMapTransferCore(
            context,
            transformOverride: null);

    private WorldInstancePlayerTransfer StageMapTransfer(
        GameSessionContext context,
        byte targetMapId,
        float targetX,
        float targetZ) =>
        StageMapTransferCore(
            context,
            new PlayerTransformOverride(
                targetMapId,
                targetX,
                targetZ));

    private WorldInstancePlayerTransfer StageMapTransferCore(
        GameSessionContext context,
        PlayerTransformOverride? transformOverride)
    {
        var runtime = GetRequiredWorldInstance(context);
        var transfer = InvokeWorldOwner(
            runtime,
            map => map.StagePlayerTransfer(
                context,
                transformOverride));
        return new WorldInstancePlayerTransfer(
            this,
            runtime,
            transfer);
    }

    private void EnsureMapObjectIdAvailable(GameSessionContext context)
    {
        if (!TryGetWorldInstance(context, out var runtime))
        {
            return;
        }

        var collision = InvokeWorldOwner(
            runtime,
            map => map.Snapshot()
                .FirstOrDefault(candidate =>
                    !ReferenceEquals(
                        candidate.Session,
                        context.Session) &&
                    candidate.ObjectId == context.ObjectId));
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"World object ID {context.ObjectId} is already assigned " +
                $"to character {collision.CharacterName} in world instance " +
                $"{context.WorldInstanceId}.");
        }
    }

    private void RemoveFromMap(GameSessionContext context)
    {
        if (TryGetWorldInstance(context, out var runtime))
        {
            var removedAt = DateTimeOffset.UtcNow;
            var lifeRevision = _playerLifeRevisions.TryGetValue(
                context.Session,
                out var currentLifeRevision)
                ? currentLifeRevision
                : -1;
            InvokeWorldOwner(
                runtime,
                map =>
                {
                    map.ClearMedusaCharacterEffectsForLifeGuarded(
                        context,
                        lifeRevision,
                        removedAt);
                    map.Remove(context.Session, out _);
                    map.ClearMonsterAggroForCharacter(
                        context.CharacterId,
                        removedAt);
                });
        }
    }

    private sealed class WorldInstancePlayerTransfer :
        IDisposable
    {
        private readonly GameSessionRegistry _registry;
        private readonly WorldInstanceRuntime _runtime;
        private MapInstance.PlayerTransfer? _transfer;

        public WorldInstancePlayerTransfer(
            GameSessionRegistry registry,
            WorldInstanceRuntime runtime,
            MapInstance.PlayerTransfer transfer)
        {
            _registry = registry;
            _runtime = runtime;
            _transfer = transfer;
        }

        public void Commit(Action publishRegistryContext)
        {
            ArgumentNullException.ThrowIfNull(
                publishRegistryContext);
            var transfer = _transfer ??
                throw new ObjectDisposedException(
                    nameof(WorldInstancePlayerTransfer));
            _registry.InvokeWorldOwner(
                _runtime,
                _ => transfer.Commit(
                    publishRegistryContext));
            _transfer = null;
        }

        public void Dispose()
        {
            var transfer = Interlocked.Exchange(
                ref _transfer,
                null);
            if (transfer is not null)
            {
                _registry.InvokeWorldOwner(
                    _runtime,
                    _ => transfer.Dispose());
            }
        }
    }

    private sealed class PlayerStatusState
    {
        private string _lastPublishedElementalFingerprint =
            ElementalClientStatusProjection.EmptyFingerprint;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public object CharacterUiStatsGate { get; } = new();

        public Dictionary<int, ActiveRuntimeStatus> RuntimeStatuses { get; } = [];

        public ActiveRuntimeStatus[] SkillCastControlStatuses = [];

        public ExperienceBoostState ExperienceBoosts { get; set; } = ExperienceBoostState.Empty;

        public string? LastFingerprint { get; set; }

        public string LastPublishedElementalFingerprint
        {
            get => Volatile.Read(
                ref _lastPublishedElementalFingerprint);
            set => Volatile.Write(
                ref _lastPublishedElementalFingerprint,
                value);
        }

        public ClientStatusAggregate LastPublishedAggregate { get; set; } =
            ClientStatusAggregate.Empty;

        public long Revision { get; set; }

        public bool CharacterUiStatsV1Enabled { get; set; }

        public DateTimeOffset? LastCharacterUiStatsV1ProbeAt { get; set; }

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
