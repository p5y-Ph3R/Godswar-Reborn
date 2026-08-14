using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private GameSessionRegistry.PveElementalCommitAuthority?
        CapturePveElementalCommitAuthority(GameCharacter character) =>
        _registry.CapturePveElementalCommitAuthority(
            _session,
            character,
            allowUnownedCompatibility: !_accountSessionRegistered);

    private PveElementalCommitResult CommitPveElementalHit(
        GameSessionRegistry.PveElementalCommitAuthority? authority,
        CombatEventProvenance provenance,
        in CombatResolution resolution,
        MonsterDamageResult damageResult) =>
        authority is null
            ? PveElementalCommitResult.Empty
            : _registry.CommitPveElementalHits(
            authority,
            provenance,
            [new PveElementalCommittedHit(
                resolution.EventId,
                resolution.TargetOrder,
                damageResult)],
            DateTimeOffset.UtcNow);

    private PveElementalCommitResult CommitPveElementalHits(
        GameSessionRegistry.PveElementalCommitAuthority? authority,
        CombatEventProvenance provenance,
        IReadOnlyList<PveElementalCommittedHit> hits) =>
        authority is null
            ? PveElementalCommitResult.Empty
            : _registry.CommitPveElementalHits(
            authority,
            provenance,
            hits,
            DateTimeOffset.UtcNow);

    private Task PublishPveElementalCommitAsync(
        GameSessionRegistry.PveElementalCommitAuthority? authority,
        PveElementalCommitResult result,
        IReadOnlyList<PreparedPveMonsterKillReward> preparedRewards,
        CancellationToken cancellationToken) =>
        authority is null
            ? Task.CompletedTask
            : _registry.PublishPveElementalCommitResultAsync(
                authority,
                result,
                preparedRewards,
                cancellationToken);

    private Task<IReadOnlyList<PreparedPveMonsterKillReward>>
        PreparePveElementalKillRewardsAsync(
            GameSessionRegistry.PveElementalCommitAuthority? authority,
            PveElementalCommitResult result) =>
        authority is null
            ? Task.FromResult<IReadOnlyList<
                PreparedPveMonsterKillReward>>([])
            : _registry.PreparePveElementalKillRewardsAsync(
                authority,
                result);

    private static PveElementalCommittedHit[]
        CreatePveElementalCommittedHits(
            PlayerCombatEcsDecision decision)
    {
        if (decision.Hits.IsEmpty)
        {
            return [];
        }

        var committed = new PveElementalCommittedHit[
            decision.Hits.Length];
        for (var index = 0; index < decision.Hits.Length; index++)
        {
            var hit = decision.Hits[index];
            var matches = decision.Resolutions
                .Where(value =>
                    value.TargetObjectId == hit.Result.ObjectId &&
                    value.SpawnGeneration ==
                        hit.Result.Monster.SpawnGeneration &&
                    value.Resolution.Hit)
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    "An ECS elemental hit must match exactly one committed " +
                    "combat resolution.");
            }

            committed[index] = new(
                matches[0].Resolution.EventId,
                matches[0].Resolution.TargetOrder,
                hit.Result);
        }

        return committed;
    }
}
