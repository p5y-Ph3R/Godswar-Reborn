using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task<DurableProgressionSettlementOutcome>
        SettleDurableProgressionIntervalAsync(
            ClientSession session,
            int accountId,
            int characterId,
            DateTimeOffset onlineFrom,
            DateTimeOffset onlineUntil,
            bool sendNotification,
            CancellationToken cancellationToken)
    {
        var executor = _progressionIntervalSettlementCommands ??
            throw new InvalidOperationException(
                "Progression interval settlement is not configured.");
        onlineFrom =
            ProgressionIntervalSettlementCommandEnvelope.CanonicalizeUtc(
                onlineFrom.ToUniversalTime());
        onlineUntil =
            ProgressionIntervalSettlementCommandEnvelope.CanonicalizeUtc(
                onlineUntil.ToUniversalTime());
        await RetryDurableProgressionForCharacterAsync(
            characterId,
            cancellationToken);
        var state = _durableProgressionOnlineSessions.AddOrUpdate(
            session,
            _ => new DurableProgressionOnlineSessionState(
                accountId,
                characterId,
                onlineFrom),
            (_, existing) =>
                existing.AccountId == accountId &&
                existing.CharacterId == characterId
                    ? existing
                    : new DurableProgressionOnlineSessionState(
                        accountId,
                        characterId,
                        onlineFrom));

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Superseded)
            {
                return new DurableProgressionSettlementOutcome(
                    state.LastProjection,
                    0,
                    false);
            }

            CreatePendingIntervalIfNeeded(
                session,
                state,
                accountId,
                characterId,
                onlineUntil);
            if (state.Pending is not null)
            {
                var result =
                    await ExecuteDurableProgressionIntervalAsync(
                        executor,
                        state.Pending.Envelope,
                        cancellationToken);
                if (!result.IsSuccess ||
                    result.Receipt is null ||
                    result.Projection is null)
                {
                    return HandleRejectedInterval(state, result);
                }

                ApplyCommittedInterval(
                    session,
                    state,
                    accountId,
                    characterId,
                    result.Receipt,
                    result.Projection);
            }

            return TakeNotification(state, sendNotification);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static async Task<
        ProgressionIntervalSettlementExecutionResult>
        ExecuteDurableProgressionIntervalAsync(
            IProgressionIntervalSettlementCommandExecutor executor,
            CommandEnvelope<ProgressionIntervalSettlementCommand> envelope,
            CancellationToken cancellationToken)
    {
        try
        {
            var result = await executor.ExecuteAsync(
                envelope,
                cancellationToken);
            var outcome = result.Disposition switch
            {
                ProgressionIntervalSettlementDisposition.Committed =>
                    CommandOutcome.Accepted,
                ProgressionIntervalSettlementDisposition.Duplicate =>
                    CommandOutcome.Duplicate,
                ProgressionIntervalSettlementDisposition
                    .RequestHashConflict =>
                    CommandOutcome.RequestHashConflict,
                ProgressionIntervalSettlementDisposition.InvalidIntent =>
                    CommandOutcome.InvalidIntent,
                ProgressionIntervalSettlementDisposition
                    .CharacterNotFound or
                ProgressionIntervalSettlementDisposition
                    .IntervalConflict =>
                    CommandOutcome.PreconditionFailed,
                _ => throw new InvalidOperationException(
                    "Unknown progression interval disposition.")
            };
            CommandMetrics.Record(
                CommandFamily.ProgressionIntervalSettlement,
                CommandIdentityStrength.ServerOperationId,
                outcome);
            return result;
        }
        catch (OperationCanceledException)
        {
            CommandMetrics.Record(
                CommandFamily.ProgressionIntervalSettlement,
                CommandIdentityStrength.ServerOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch
        {
            CommandMetrics.Record(
                CommandFamily.ProgressionIntervalSettlement,
                CommandIdentityStrength.ServerOperationId,
                CommandOutcome.ProviderUnavailable);
            throw;
        }
    }

    private static void CreatePendingIntervalIfNeeded(
        ClientSession session,
        DurableProgressionOnlineSessionState state,
        int accountId,
        int characterId,
        DateTimeOffset onlineUntil)
    {
        if (state.Pending is not null ||
            onlineUntil <= state.LastAccountedAt)
        {
            return;
        }

        var maximumUntil =
            state.LastAccountedAt +
            ProgressionIntervalSettlementCommandEnvelope.MaximumInterval;
        var boundedUntil = onlineUntil > maximumUntil
            ? maximumUntil
            : onlineUntil;
        var envelope =
            ProgressionIntervalSettlementCommandEnvelope.Create(
                new CommandSubject(accountId, characterId),
                state.OnlineSessionId,
                state.NextSequence,
                state.LastAccountedAt,
                boundedUntil,
                session.IsSecure
                    ? CommandTransportKind.SecureTlsLegacy
                    : CommandTransportKind.LegacyTcp);
        state.Pending =
            new DurableProgressionPendingInterval(envelope);
    }

    private static DurableProgressionSettlementOutcome
        HandleRejectedInterval(
            DurableProgressionOnlineSessionState state,
            ProgressionIntervalSettlementExecutionResult result)
    {
        if (result.Disposition !=
            ProgressionIntervalSettlementDisposition.IntervalConflict)
        {
            throw new InvalidOperationException(
                "The durable progression interval was rejected: " +
                result.Disposition);
        }

        state.Pending = null;
        state.LastProjection = result.Projection;
        if (result.Projection is not null &&
            result.Projection.OnlineSessionId ==
                state.OnlineSessionId)
        {
            state.LastAccountedAt =
                result.Projection.LastIntervalEndUtc;
            state.NextSequence = checked(
                result.Projection.LastIntervalSequence + 1);
        }
        else
        {
            state.Superseded = true;
        }

        return new DurableProgressionSettlementOutcome(
            result.Projection,
            0,
            false);
    }

    private void ApplyCommittedInterval(
        ClientSession session,
        DurableProgressionOnlineSessionState state,
        int accountId,
        int characterId,
        ProgressionIntervalSettlementReceipt receipt,
        ProgressionIntervalProjection projection)
    {
        state.Pending = null;
        state.LastProjection = projection;
        state.LastAccountedAt = projection.LastIntervalEndUtc;
        state.NextSequence = checked(receipt.IntervalSequence + 1);
        state.UnnotifiedEnergyX100 = checked(
            state.UnnotifiedEnergyX100 +
            receipt.GainedZodiacEnergyX100);
        state.UnnotifiedCompensation |=
            receipt.ZodiacCompensationApplied;
        ObserveCommittedOnlineDurationEcs(
            session,
            accountId,
            characterId,
            Godswar.Server.World.Components.Players
                .PlayerOnlineDurationTarget.ProgressionBoosts,
            receipt.OnlineFromUtc,
            receipt.OnlineUntilUtc);
        ObserveCommittedOnlineDurationEcs(
            session,
            accountId,
            characterId,
            Godswar.Server.World.Components.Players
                .PlayerOnlineDurationTarget.Zodiac,
            receipt.OnlineFromUtc,
            receipt.OnlineUntilUtc);
        if (_zodiacOnlineSessions.TryGetValue(
                session,
                out var zodiacState) &&
            zodiacState.CharacterId == characterId)
        {
            ApplyDurableProgressionProjection(
                zodiacState.Character,
                projection);
        }
    }

    private static DurableProgressionSettlementOutcome TakeNotification(
        DurableProgressionOnlineSessionState state,
        bool sendNotification)
    {
        var gain = sendNotification
            ? state.UnnotifiedEnergyX100
            : 0;
        var compensation = sendNotification &&
            state.UnnotifiedCompensation;
        if (sendNotification)
        {
            state.UnnotifiedEnergyX100 = 0;
            state.UnnotifiedCompensation = false;
        }

        return new DurableProgressionSettlementOutcome(
            state.LastProjection,
            gain,
            compensation);
    }

    private static void ApplyDurableProgressionProjection(
        GameCharacter character,
        ProgressionIntervalProjection projection)
    {
        lock (character.ZodiacSync)
        {
            character.ZodiacEnergy = projection.ZodiacEnergy;
            character.ZodiacEnergyRemainderX100 =
                projection.ZodiacEnergyRemainderX100;
            character.ZodiacOnlineDay =
                projection.ZodiacOnlineDay;
            character.ZodiacOnlineDurationTicksToday =
                projection.ZodiacOnlineDurationTicksToday;
            character.ZodiacLastOnlineAt =
                projection.LastIntervalEndUtc;
            character.ZodiacLastCompensationDay =
                projection.ZodiacLastCompensationDay;
        }
    }

    private sealed class DurableProgressionOnlineSessionState(
        int accountId,
        int characterId,
        DateTimeOffset onlineStartedAt)
    {
        public int AccountId { get; } = accountId;
        public int CharacterId { get; } = characterId;
        public Guid OnlineSessionId { get; } = Guid.NewGuid();
        public DateTimeOffset LastAccountedAt { get; set; } =
            ProgressionIntervalSettlementCommandEnvelope.CanonicalizeUtc(
                onlineStartedAt.ToUniversalTime());
        public long NextSequence { get; set; } = 1;
        public DurableProgressionPendingInterval? Pending { get; set; }
        public ProgressionIntervalProjection? LastProjection { get; set; }
        public int UnnotifiedEnergyX100 { get; set; }
        public bool UnnotifiedCompensation { get; set; }
        public bool Superseded { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed record DurableProgressionPendingInterval(
        CommandEnvelope<ProgressionIntervalSettlementCommand> Envelope);

    private readonly record struct DurableProgressionSettlementOutcome(
        ProgressionIntervalProjection? Projection,
        int NotificationGainX100,
        bool NotificationIncludedCompensation);
}
