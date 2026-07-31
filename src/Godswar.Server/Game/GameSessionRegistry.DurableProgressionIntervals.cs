using Godswar.Server.Application.Characters;
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
        if (!TryGetCurrentWorldOwnership(
                session,
                accountId,
                characterId,
                out var ownership))
        {
            throw new PlayerOwnershipValidationException(
                PlayerOwnershipValidationStatus.OwnershipLost);
        }

        onlineFrom =
            ProgressionIntervalSettlementCommandEnvelope.CanonicalizeUtc(
                onlineFrom.ToUniversalTime());
        onlineUntil =
            ProgressionIntervalSettlementCommandEnvelope.CanonicalizeUtc(
                onlineUntil.ToUniversalTime());
        var state = GetOrCreateDurableProgressionSession(
            session,
            accountId,
            characterId,
            onlineFrom,
            ownership);

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            await RetryDurableProgressionForCharacterAsync(
                session,
                accountId,
                characterId,
                ownership,
                cancellationToken);
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
                ProgressionIntervalSettlementExecutionResult result;
                try
                {
                    result =
                        await ExecuteDurableProgressionIntervalAsync(
                            executor,
                            state.Pending.Envelope,
                            cancellationToken);
                }
                catch (PlayerOwnershipValidationException)
                {
                    state.Superseded = true;
                    session.Disconnect();
                    throw;
                }

                if (!IsCurrentWorldOwnership(
                        session,
                        accountId,
                        characterId,
                        ownership))
                {
                    state.Superseded = true;
                    throw new PlayerOwnershipValidationException(
                        PlayerOwnershipValidationStatus
                            .OwnershipLost);
                }

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

    private DurableProgressionOnlineSessionState
        GetOrCreateDurableProgressionSession(
            ClientSession session,
            int accountId,
            int characterId,
            DateTimeOffset onlineFrom,
            PlayerOwnershipFence ownership) =>
        _durableProgressionOnlineSessions.AddOrUpdate(
            session,
            _ => new DurableProgressionOnlineSessionState(
                accountId,
                characterId,
                onlineFrom,
                ownership),
            (_, existing) =>
                existing.AccountId == accountId &&
                existing.CharacterId == characterId &&
                existing.Ownership == ownership
                    ? existing
                    : new DurableProgressionOnlineSessionState(
                        accountId,
                        characterId,
                        onlineFrom,
                        ownership));

    private async Task<ZodiacLevelUpgradeResult?>
        UpgradeZodiacLevelWithDurableProgressionGateAsync(
            ClientSession session,
            int accountId,
            GameCharacter character,
            PlayerOwnershipFence ownership,
            ZodiacOnlineSessionState zodiacState,
            CancellationToken cancellationToken)
    {
        DateTimeOffset onlineAnchor;
        await zodiacState.Gate.WaitAsync(cancellationToken);
        try
        {
            onlineAnchor = zodiacState.LastAccountedAt;
        }
        finally
        {
            zodiacState.Gate.Release();
        }

        // Resolve an unknown prior interval outcome before a level mutation.
        // A zero-length request creates no new interval, but retries the exact
        // pending server operation if one exists.
        _ = await SettleDurableProgressionIntervalAsync(
            session,
            accountId,
            character.Id,
            onlineAnchor,
            onlineAnchor,
            sendNotification: false,
            cancellationToken);

        var durableState = GetOrCreateDurableProgressionSession(
            session,
            accountId,
            character.Id,
            onlineAnchor,
            ownership);
        // Online accrual takes the Zodiac gate before the durable progression
        // gate. Use the same order and hold both through projection refresh
        // and level mutation so an interval committed between the zero-length
        // retry and this section cannot be overwritten by an older snapshot.
        await zodiacState.Gate.WaitAsync(cancellationToken);
        try
        {
            await durableState.Gate.WaitAsync(cancellationToken);
            try
            {
                if (durableState.Superseded)
                {
                    throw new PlayerOwnershipValidationException(
                        PlayerOwnershipValidationStatus.OwnershipLost);
                }

                if (durableState.LastProjection is { } projection &&
                    projection.LastIntervalEndUtc >=
                        zodiacState.LastAccountedAt)
                {
                    zodiacState.LastAccountedAt =
                        projection.LastIntervalEndUtc;
                    ApplyDurableProgressionProjection(
                        zodiacState.Character,
                        projection);
                }

                RequireCurrentZodiacLevelOwner(
                    session,
                    accountId,
                    character.Id,
                    ownership);
                var focusedResult = await _zodiacLevelStore!.UpgradeAsync(
                    accountId,
                    character.Id,
                    ownership,
                    cancellationToken);
                var result = focusedResult is null
                    ? null
                    : FocusedGameplayProjectionCompatibility.ToLegacy(
                        focusedResult);
                RequireCurrentZodiacLevelOwner(
                    session,
                    accountId,
                    character.Id,
                    ownership);
                if (result is null)
                {
                    return null;
                }

                ApplyZodiacLevelUpgradeResult(
                    zodiacState.Character,
                    result);
                if (!ReferenceEquals(zodiacState.Character, character))
                {
                    ApplyZodiacLevelUpgradeResult(character, result);
                }

                return result;
            }
            finally
            {
                durableState.Gate.Release();
            }
        }
        finally
        {
            zodiacState.Gate.Release();
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
        catch (PlayerOwnershipValidationException)
        {
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
                    : CommandTransportKind.LegacyTcp) with
            {
                Ownership = state.Ownership
            };
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
        DateTimeOffset onlineStartedAt,
        PlayerOwnershipFence ownership)
    {
        public int AccountId { get; } = accountId;
        public int CharacterId { get; } = characterId;
        public Guid OnlineSessionId { get; } = Guid.NewGuid();
        public PlayerOwnershipFence Ownership { get; } = ownership;
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
