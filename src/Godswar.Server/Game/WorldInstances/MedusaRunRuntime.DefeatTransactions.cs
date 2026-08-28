using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaRunRuntime
{
    internal sealed class PreparedDefeatClaim
    {
        internal readonly MedusaRunRuntime _owner;
        internal readonly SpawnState _spawn;

        internal PreparedDefeatClaim(
            MedusaRunRuntime owner,
            SpawnState spawn,
            DateTimeOffset occurredAt,
            int beforeTeamScore,
            MedusaDefeatClaimResult result,
            MedusaRunCompletionMarker? completionMarker)
        {
            _owner = owner;
            _spawn = spawn;
            OccurredAt = occurredAt;
            BeforeTeamScore = beforeTeamScore;
            Result = result;
            CompletionMarker = completionMarker;
        }

        internal DateTimeOffset OccurredAt { get; }

        internal int BeforeTeamScore { get; }

        internal MedusaDefeatClaimResult Result { get; }

        internal MedusaRunCompletionMarker? CompletionMarker { get; }

        internal bool CompletesRun =>
            Result.Outcome == MedusaDefeatClaimOutcome.Completed;

        internal bool Completed { get; set; }

#if DEBUG
        internal bool ProtocolCheckInvalid { get; set; }
#endif

    }

    internal bool TryPrepareDefeatClaim(
        int defeatedByCharacterId,
        uint objectId,
        uint spawnGeneration,
        DateTimeOffset occurredAt,
        out PreparedDefeatClaim? prepared,
        out MedusaDefeatClaimResult rejection)
    {
        var authoritativeAt = occurredAt.ToUniversalTime();
        var preview = PreviewDefeatClaim(
            defeatedByCharacterId,
            objectId,
            spawnGeneration,
            authoritativeAt);
        if (preview != MedusaDefeatClaimPreviewOutcome.Eligible)
        {
            prepared = null;
            rejection = new(ToClaimOutcome(preview), 0, _teamScore);
            return false;
        }

        var spawn = _spawnsByObjectId[objectId];
        var afterScore = CappedScoreAfter(_teamScore, spawn.ScoreValue);
        var scoreAwarded = afterScore - _teamScore;
        MedusaRunCompletionMarker? completion = null;
        var outcome = MedusaDefeatClaimOutcome.Applied;
        if (FinalBossesDefeatedAfter(spawn))
        {
            var elapsed = authoritativeAt - StartedAt;
            MedusaEncounterTitleAward? title = null;
            if (MedusaIslandEncounterPolicy.TryResolveBestCompletionTitle(
                    Difficulty,
                    afterScore,
                    elapsed,
                    out var resolvedTitle))
            {
                title = resolvedTitle;
            }

            completion = new(
                authoritativeAt,
                elapsed,
                afterScore,
                title);
            outcome = MedusaDefeatClaimOutcome.Completed;
        }

        rejection = default;
        prepared = new PreparedDefeatClaim(
            this,
            spawn,
            authoritativeAt,
            _teamScore,
            new(outcome, scoreAwarded, afterScore),
            completion);
        return true;
    }

    internal bool CanCompletePreparedDefeat(
        PreparedDefeatClaim? prepared) =>
        prepared is not null &&
        ReferenceEquals(prepared._owner, this) &&
        !prepared.Completed &&
#if DEBUG
        !prepared.ProtocolCheckInvalid &&
#endif
        _state == MedusaRunState.Active &&
        _lastObservedAt == prepared.OccurredAt &&
        _teamScore == prepared.BeforeTeamScore &&
        _completionMarker is null &&
        !prepared._spawn.Defeated &&
        _spawnsByObjectId.TryGetValue(
            prepared._spawn.Definition.ObjectId,
            out var current) &&
        ReferenceEquals(current, prepared._spawn);

    internal MedusaDefeatClaimResult CompletePreparedDefeat(
        PreparedDefeatClaim prepared)
    {
        prepared.Completed = true;
        prepared._spawn.Defeated = true;
        _teamScore = prepared.Result.TeamScore;
        if (prepared.CompletesRun)
        {
            _completionMarker = prepared.CompletionMarker;
            _state = MedusaRunState.Completed;
        }

        return prepared.Result;
    }

#if DEBUG
    internal static void InvalidatePreparedDefeatForProtocolCheck(
        PreparedDefeatClaim prepared) =>
        prepared.ProtocolCheckInvalid = true;
#endif
}
