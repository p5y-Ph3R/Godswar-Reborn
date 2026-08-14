using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal sealed class PveElementalCommitAuthority : IDisposable
    {
        private int _closed;

        internal readonly ElementalCombatSessionState SourceState;

        internal PveElementalCommitAuthority(
            GameSessionContext source,
            ElementalCombatSessionState sourceState,
            ElementalEquipmentProfile sourceProfile,
            int sourceMaximumHealth,
            int sourceMaximumMana)
        {
            Source = source;
            SourceState = sourceState;
            SourceProfile = sourceProfile;
            SourceMaximumHealth = sourceMaximumHealth;
            SourceMaximumMana = sourceMaximumMana;
        }

        internal GameSessionContext Source { get; }
        internal ElementalEquipmentProfile SourceProfile { get; }
        internal int SourceMaximumHealth { get; }
        internal int SourceMaximumMana { get; }

        internal bool TryConsume() =>
            Interlocked.CompareExchange(ref _closed, 1, 0) == 0;

        internal void ReleaseLease()
        {
            lock (SourceState.Gate)
            {
                SourceState.ReleaseCommitLease();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _closed, 1, 0) == 0)
            {
                ReleaseLease();
            }
        }
    }

    internal PveElementalCommitAuthority?
        CapturePveElementalCommitAuthority(
            ClientSession sourceSession,
            GameCharacter sourceCharacter,
            bool allowUnownedCompatibility = false)
    {
        ArgumentNullException.ThrowIfNull(sourceSession);
        ArgumentNullException.ThrowIfNull(sourceCharacter);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sourceSession, out var source) ||
                !source.WorldReady ||
                !ReferenceEquals(source.Character, sourceCharacter) ||
                source.CharacterId != sourceCharacter.Id)
            {
                return null;
            }


            ElementalCombatSessionState sourceState;
            var hasCurrentOwnership =
                source.Ownership.IsValid &&
                IsCurrentAccountSession(
                    source.AccountId,
                    sourceSession,
                    source.Ownership);
            if (hasCurrentOwnership)
            {
                if (!TryGetElementalCombatSession(
                        sourceSession,
                        new ElementalCombatSessionFence(
                            source.CharacterId,
                            source.MapId,
                            source.Ownership),
                        out sourceState))
                {
                    return null;
                }
            }
            else if (allowUnownedCompatibility &&
                     !source.Ownership.IsValid)
            {
                sourceState = GetOrCreateElementalCombatSession(
                    sourceSession,
                    new ElementalCombatSessionIdentity(
                        source.CharacterId,
                        source.MapId,
                        source.WorldInstanceId,
                        source.Ownership));
            }
            else
            {
                return null;
            }

            int maximumHealth;
            int maximumMana;
            ElementalEquipmentProfile sourceProfile;
            lock (sourceCharacter.VitalsSync)
            {
                if (sourceCharacter.CurrentHp <= 0 ||
                    sourceCharacter.MaxHp <= 0 ||
                    sourceCharacter.MaxMp < 0)
                {
                    return null;
                }

                maximumHealth = sourceCharacter.MaxHp;
                maximumMana = sourceCharacter.MaxMp;
                sourceProfile = sourceCharacter.ElementalEquipment;
            }

            lock (sourceState.Gate)
            {
                sourceState.AcquireCommitLease();
            }

            return new PveElementalCommitAuthority(
                source,
                sourceState,
                sourceProfile,
                maximumHealth,
                maximumMana);
        }
    }

    internal PveElementalCommitResult CommitPveElementalHits(
        PveElementalCommitAuthority authority,
        CombatEventProvenance provenance,
        IReadOnlyList<PveElementalCommittedHit> committedHits,
        DateTimeOffset committedAt)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(committedHits);
        if (!authority.TryConsume())
        {
            return PveElementalCommitResult.Empty;
        }

        try
        {
            if (committedHits.Count == 0 ||
                provenance is not (
                    CombatEventProvenance.DirectBasicAttack or
                    CombatEventProvenance.DirectSkill) ||
                committedAt < DateTimeOffset.UnixEpoch ||
                !WorldInstances.TryFind(
                    authority.Source.WorldInstanceId,
                    out var runtime) ||
                runtime.MapId != authority.Source.MapId)
            {
                return PveElementalCommitResult.Empty;
            }

            var monsterSnapshot = InvokeWorldOwner(
                runtime,
                static map => map.SnapshotMonsters());
            lock (_pveElementalCommitGate)
            lock (authority.Source.Character.VitalsSync)
            lock (authority.SourceState.Gate)
            {
                return CommitPveElementalHitsLocked(
                    authority.Source,
                    authority.SourceState,
                    authority.SourceProfile,
                    authority.SourceMaximumHealth,
                    authority.SourceMaximumMana,
                    provenance,
                    committedHits,
                    monsterSnapshot,
                    committedAt);
            }
        }
        finally
        {
            authority.ReleaseLease();
        }
    }

    internal Task PublishPveElementalCommitResultAsync(
        PveElementalCommitAuthority authority,
        PveElementalCommitResult result,
        IReadOnlyList<PreparedPveMonsterKillReward> preparedRewards,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(preparedRewards);
        return PublishPveElementalCommitAsync(
            authority.Source.Session,
            result,
            cancellationToken,
            authority.Source,
            preparedRewards);
    }

    internal Task<IReadOnlyList<PreparedPveMonsterKillReward>>
        PreparePveElementalKillRewardsAsync(
            PveElementalCommitAuthority authority,
            PveElementalCommitResult result)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(result);
        return PreparePveElementalKillRewardsAsync(
            authority.Source,
            result);
    }

    internal async Task<PveElementalCommitResult>
        CommitAndPublishPveElementalHitsAsync(
            ClientSession sourceSession,
            GameCharacter sourceCharacter,
            CombatEventProvenance provenance,
            IReadOnlyList<PveElementalCommittedHit> committedHits,
            DateTimeOffset committedAt,
            CancellationToken cancellationToken)
    {
        var authority = CapturePveElementalCommitAuthority(
            sourceSession,
            sourceCharacter);
        if (authority is null)
        {
            return PveElementalCommitResult.Empty;
        }

        var result = CommitPveElementalHits(
            authority,
            provenance,
            committedHits,
            committedAt);
        var preparedRewards =
            await PreparePveElementalKillRewardsAsync(authority, result);
        await PublishPveElementalCommitResultAsync(
            authority,
            result,
            preparedRewards,
            cancellationToken);
        return result;
    }

    private bool TryGetPveElementalMonsterSnapshot(
        GameSessionContext source,
        uint objectId,
        out MonsterRuntimeSnapshot snapshot)
    {
        if (!WorldInstances.TryFind(source.WorldInstanceId, out var runtime) ||
            runtime.MapId != source.MapId)
        {
            snapshot = default!;
            return false;
        }

        return TryGetMonsterSnapshotCore(runtime, objectId, out snapshot);
    }

    private bool TryApplyPveElementalMonsterDamage(
        GameSessionContext source,
        uint objectId,
        uint damage,
        int attackerCharacterId,
        uint expectedSpawnGeneration,
        ulong expectedHealthRevision,
        DateTimeOffset committedAt,
        out MonsterDamageResult result)
    {
        if (!WorldInstances.TryFind(source.WorldInstanceId, out var runtime) ||
            runtime.MapId != source.MapId)
        {
            result = default!;
            return false;
        }

        var attempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                var applied = map.TryApplyMonsterDamageGuarded(
                    objectId,
                    damage,
                    attackerCharacterId,
                    expectedSpawnGeneration,
                    expectedHealthRevision,
                    committedAt,
                    out var value);
                return (Applied: applied, Value: value);
            });
        result = attempt.Value;
        return attempt.Applied;
    }

    private bool TryApplyPveElementalMonsterStun(
        GameSessionContext source,
        uint objectId,
        TimeSpan duration,
        uint expectedSpawnGeneration,
        DateTimeOffset committedAt,
        out MonsterStunResult result)
    {
        if (!WorldInstances.TryFind(source.WorldInstanceId, out var runtime) ||
            runtime.MapId != source.MapId)
        {
            result = default!;
            return false;
        }

        var attempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                var applied = map.TryApplyMonsterStun(
                    objectId,
                    source.CharacterId,
                    duration,
                    expectedSpawnGeneration,
                    committedAt,
                    out var value);
                return (Applied: applied, Value: value);
            });
        result = attempt.Value;
        return attempt.Applied;
    }
}
