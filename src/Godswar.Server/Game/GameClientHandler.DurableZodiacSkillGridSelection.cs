using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const uint ZodiacSelectionSucceededCode = 1;
    private const uint ZodiacSelectionInvalidIntentCode = 2;
    private const uint ZodiacSelectionInactiveGridCode = 3;
    private const uint ZodiacSelectionWrongRowCode = 4;
    private const uint ZodiacSelectionWrongClassCode = 5;
    private const uint ZodiacSelectionNotLearnedCode = 6;
    private const uint ZodiacSelectionDuplicateRowCode = 7;
    private const uint ZodiacSelectionAlreadySelectedCode = 8;
    private const uint ZodiacSelectionWrongOwnerCode = 9;
    private readonly IZodiacSkillGridSelectionCommandExecutor?
        _zodiacSkillGridSelectionCommands;

    private async Task HandleZodiacSkillGridSelectionAsync(
        GamePacket packet,
        ZodiacSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }
        if (packet.ClientOperationId is { } operationId)
        {
            await HandleDurableZodiacSkillGridSelectionAsync(
                request,
                operationId,
                cancellationToken);
            return;
        }

        if (_session.IsSecure)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.ZodiacSkillGridSelection);
            CommandMetrics.Record(
                CommandFamily.ZodiacSkillGridSelection,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.InvalidIntent);
            Console.Error.WriteLine(
                "[zodiac] rejected secure skill selection without " +
                $"operation identity character={_character.Name}");
            return;
        }

        await HandleCompatibilityZodiacSkillGridSelectionAsync(
            request,
            cancellationToken);
    }

    private async Task HandleCompatibilityZodiacSkillGridSelectionAsync(
        ZodiacSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null ||
            !ZodiacSkillGridSelectionCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                request.Value1,
                request.Value2,
                out _))
        {
            return;
        }

        if (!AllowLegacyPlayerMutationFallback(
                "zodiac_skill_grid_selection"))
        {
            return;
        }

        var result = await _registry.SelectZodiacSkillGridAsync(
            _session,
            _account.Id,
            _character,
            request.Value1,
            request.Value2,
            cancellationToken);
        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.ZodiacSkillGridSelection);
        CommandMetrics.Record(
            CommandFamily.ZodiacSkillGridSelection,
            CommandIdentityStrength.UnsupportedLegacyRetry,
            result?.Committed == true
                ? CommandOutcome.Accepted
                : CommandOutcome.PreconditionFailed);
        if (result?.Committed == true)
        {
            await _session.SendAsync(
                PacketBuilder.ZodiacSkillGridSelected(
                    result.GridIndex,
                    result.SelectedSkillKind),
                cancellationToken,
                "ZodiacSkillGridSelected");
        }

        if (result is not null)
        {
            _registry.UpdateCharacter(
                _session,
                _character,
                advanceWorldRevision: false);
            await SendZodiacFullSyncAsync(cancellationToken);
        }
    }

    private async Task HandleDurableZodiacSkillGridSelectionAsync(
        ZodiacSyncRequest request,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }

        if (!_session.IsSecure ||
            _zodiacSkillGridSelectionCommands is null)
        {
            RecordZodiacSelectionUnavailable(
                operationId,
                "secure transport/provider is unavailable");
            return;
        }

        if (!ZodiacSkillGridSelectionCommandEnvelope.TryCreateCommand(
                operationId,
                request.Value1,
                request.Value2,
                out var command))
        {
            if (operationId == Guid.Empty)
            {
                RecordZodiacSelectionUnavailable(
                    operationId,
                    "operation marker is empty");
                return;
            }

            CommandMetrics.Record(
                CommandFamily.ZodiacSkillGridSelection,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.InvalidIntent);
            await SendZodiacSelectionResultAsync(
                operationId,
                SecureLegacyCommandDisposition.Rejected,
                ZodiacSelectionInvalidIntentCode,
                0,
                cancellationToken);
            return;
        }

        var envelope = ZodiacSkillGridSelectionCommandEnvelope.Create(
            new CommandSubject(_account.Id, _character.Id),
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command) with
        {
            Ownership = ownership
        };
        ZodiacSkillGridSelectionExecutionResult result;
        try
        {
            result = await _registry
                .ExecuteDurableZodiacSkillGridSelectionAsync(
                    _session,
                    _account.Id,
                    _character,
                    _zodiacSkillGridSelectionCommands,
                    envelope,
                    cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception exception)
        {
            RecordZodiacSelectionUnavailable(
                operationId,
                exception.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        if (!TryResolveZodiacSelectionTerminal(
                result,
                out var disposition,
                out var resultCode,
                out var revision,
                out var outcome))
        {
            RecordZodiacSelectionUnavailable(
                operationId,
                $"unknown disposition {result.Disposition}");
            return;
        }

        CommandMetrics.Record(
            envelope.Family,
            envelope.IdentityStrength,
            outcome);
        if (result.Disposition ==
                ZodiacSkillGridSelectionExecutionDisposition.Committed &&
            result.Receipt?.Succeeded == true)
        {
            // The native handler writes v2 without a failure branch. Only the
            // first committed success may receive SID 102.
            await _session.SendAsync(
                PacketBuilder.ZodiacSkillGridSelected(
                    command.GridIndex,
                    command.SelectedSkillKind),
                cancellationToken,
                "DurableZodiacSkillGridSelected");
        }

        if (result.HasAuthoritativeProjection)
        {
            await SendZodiacFullSyncAsync(cancellationToken);
        }

        await SendZodiacSelectionResultAsync(
            operationId,
            disposition,
            resultCode,
            revision,
            cancellationToken);
    }

    private ValueTask SendZodiacSelectionResultAsync(
        Guid operationId,
        SecureLegacyCommandDisposition disposition,
        uint resultCode,
        ulong revision,
        CancellationToken cancellationToken) =>
        _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)CommandFamily.ZodiacSkillGridSelection,
                resultCode,
                revision,
                operationId),
            cancellationToken);

    private static bool TryResolveZodiacSelectionTerminal(
        ZodiacSkillGridSelectionExecutionResult result,
        out SecureLegacyCommandDisposition disposition,
        out uint resultCode,
        out ulong revision,
        out CommandOutcome outcome)
    {
        disposition = default;
        resultCode = 0;
        revision = 0;
        outcome = CommandOutcome.ProviderUnavailable;
        switch (result.Disposition)
        {
            case ZodiacSkillGridSelectionExecutionDisposition.Committed:
            case ZodiacSkillGridSelectionExecutionDisposition.Duplicate:
            case ZodiacSkillGridSelectionExecutionDisposition
                .TerminalRejected:
                if (result.Receipt is not { } receipt)
                {
                    return false;
                }

                disposition = result.Disposition switch
                {
                    ZodiacSkillGridSelectionExecutionDisposition.Committed =>
                        SecureLegacyCommandDisposition.Applied,
                    ZodiacSkillGridSelectionExecutionDisposition.Duplicate =>
                        SecureLegacyCommandDisposition.Replayed,
                    _ => SecureLegacyCommandDisposition.Rejected
                };
                resultCode = SelectionResultCode(receipt.Status);
                revision = checked((ulong)result.CurrentRevision);
                outcome = result.Disposition switch
                {
                    ZodiacSkillGridSelectionExecutionDisposition.Committed =>
                        CommandOutcome.Accepted,
                    ZodiacSkillGridSelectionExecutionDisposition.Duplicate =>
                        CommandOutcome.Duplicate,
                    _ => CommandOutcome.PreconditionFailed
                };
                return true;
            case ZodiacSkillGridSelectionExecutionDisposition
                .RequestHashConflict:
                disposition = SecureLegacyCommandDisposition.Conflict;
                outcome = CommandOutcome.RequestHashConflict;
                return true;
            case ZodiacSkillGridSelectionExecutionDisposition.InvalidIntent:
                disposition = SecureLegacyCommandDisposition.Rejected;
                resultCode = ZodiacSelectionInvalidIntentCode;
                outcome = CommandOutcome.InvalidIntent;
                return true;
            case ZodiacSkillGridSelectionExecutionDisposition
                .PreconditionFailed:
                disposition = SecureLegacyCommandDisposition.Rejected;
                resultCode = ZodiacSelectionWrongOwnerCode;
                outcome = CommandOutcome.PreconditionFailed;
                return true;
            default:
                return false;
        }
    }

    private static uint SelectionResultCode(
        ZodiacSkillGridSelectionReceiptStatus status) =>
        status switch
        {
            ZodiacSkillGridSelectionReceiptStatus.Succeeded =>
                ZodiacSelectionSucceededCode,
            ZodiacSkillGridSelectionReceiptStatus.InactiveGrid =>
                ZodiacSelectionInactiveGridCode,
            ZodiacSkillGridSelectionReceiptStatus
                .SkillKindNotAllowedForGrid =>
                ZodiacSelectionWrongRowCode,
            ZodiacSkillGridSelectionReceiptStatus
                .SkillKindNotAllowedForClass =>
                ZodiacSelectionWrongClassCode,
            ZodiacSkillGridSelectionReceiptStatus.SkillNotLearned =>
                ZodiacSelectionNotLearnedCode,
            ZodiacSkillGridSelectionReceiptStatus.DuplicateSkillInRow =>
                ZodiacSelectionDuplicateRowCode,
            ZodiacSkillGridSelectionReceiptStatus.AlreadySelected =>
                ZodiacSelectionAlreadySelectedCode,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private void RecordZodiacSelectionUnavailable(
        Guid operationId,
        string reason)
    {
        CommandMetrics.Record(
            CommandFamily.ZodiacSkillGridSelection,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[zodiac] durable skill selection unresolved; operation " +
            $"remains pending character={_character?.Name ?? "<none>"} " +
            $"operationId={operationId}: {reason}");
    }
}
