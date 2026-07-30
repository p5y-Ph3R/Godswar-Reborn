using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Rewards;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IMonsterDeathRewardCommandExecutor?
        _monsterDeathRewardCommands;
    private readonly bool _requiresDurableMonsterRewardCommands;

    private async Task<MonsterRewardSettlement?>
        SettleMonsterRewardWithImmediateRetryAsync(
            MonsterDamageResult damageResult,
            int awardedExperience,
            int awardedTalentExperience,
            DateTimeOffset receivedAt)
        =>
        await MonsterDeathRewardCommitBoundary.ExecuteAsync(
            cancellationToken => SettleMonsterRewardAsync(
                damageResult,
                awardedExperience,
                awardedTalentExperience,
                receivedAt,
                cancellationToken),
            allowImmediateReplay:
                _monsterDeathRewardCommands is not null,
            firstFailure =>
                // A database commit can succeed while its acknowledgement is
                // lost. Replay the same server-derived death identity once.
                Console.Error.WriteLine(
                    $"[reward] immediate durable replay death={damageResult.ObjectId} reason={firstFailure.GetType().Name}"));

    private async Task<MonsterRewardSettlement?> SettleMonsterRewardAsync(
        MonsterDamageResult damageResult,
        int awardedExperience,
        int awardedTalentExperience,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        if (_account is null ||
            _character is null ||
            !damageResult.Killed ||
            damageResult.AfterHealth != 0 ||
            damageResult.HealthMutation is not { } mutation ||
            mutation.AfterHealthRevision !=
                damageResult.Monster.HealthRevision ||
            mutation.SpawnGeneration !=
                damageResult.Monster.SpawnGeneration ||
            damageResult.Monster.Definition.MapId !=
                _character.CurrentMap ||
            !MonsterDeathRewardCommandEnvelope.TryCreateCommand(
                damageResult.Monster.RuntimeInstanceId,
                _character.CurrentMap,
                damageResult.ObjectId,
                mutation.SpawnGeneration,
                mutation.AfterHealthRevision,
                awardedExperience,
                awardedTalentExperience,
                out var command))
        {
            Console.Error.WriteLine(
                "[reward] rejected invalid server-derived monster death");
            return null;
        }

        if (_monsterDeathRewardCommands is not null)
        {
            var transport = _session.IsSecure
                ? CommandTransportKind.SecureTlsLegacy
                : CommandTransportKind.LegacyTcp;
            var envelope = MonsterDeathRewardCommandEnvelope.Create(
                new CommandSubject(_account.Id, _character.Id),
                new CommandConnectionCorrelation(
                    _commandConnectionId,
                    transport),
                receivedAt,
                command);
            var execution =
                await _monsterDeathRewardCommands.ExecuteAsync(
                    envelope,
                    cancellationToken);
            if (execution.Receipt is null ||
                execution.Projection is null)
            {
                Console.Error.WriteLine(
                    $"[reward] durable settlement rejected death={command.DeathEventId:N} disposition={execution.Disposition}");
                return null;
            }

            return new MonsterRewardSettlement(
                command.DeathEventId,
                ToLegacyProgressionResult(execution.Receipt),
                execution.Projection,
                execution.Disposition ==
                    MonsterDeathRewardExecutionDisposition.Committed,
                IsDurable: true);
        }

        if (_requiresDurableMonsterRewardCommands)
        {
            // PostgreSQL production composition must never bypass the global
            // death claim and exactly-once reward transaction.
            Console.Error.WriteLine(
                $"[reward] durable reward provider unavailable death={command.DeathEventId:N}");
            return null;
        }

        return await SettleLegacyMonsterRewardAsync(
            command.DeathEventId,
            awardedExperience,
            awardedTalentExperience,
            cancellationToken);
    }

    private static CharacterProgressionResult ToLegacyProgressionResult(
        MonsterDeathRewardExecutionReceipt receipt) =>
        new(
            receipt.ExperienceGained,
            receipt.PreviousLevel,
            receipt.CurrentLevel,
            receipt.CurrentExperience,
            receipt.NextLevelExperience,
            receipt.LevelUps
                .Select(static levelUp =>
                    new PlayerLevelUpProgression(
                        levelUp.Level,
                        levelUp.CurrentExperience,
                        levelUp.NextLevelExperience))
                .ToArray(),
            receipt.TalentExperienceGained,
            receipt.CurrentTalentExperience,
            receipt.TalentPointsGained,
            receipt.CurrentTalentPoints);

    private sealed record MonsterRewardSettlement(
        Guid DeathEventId,
        CharacterProgressionResult Progression,
        MonsterDeathRewardProjection? Projection,
        bool IsFirstCommit,
        bool IsDurable);
}
