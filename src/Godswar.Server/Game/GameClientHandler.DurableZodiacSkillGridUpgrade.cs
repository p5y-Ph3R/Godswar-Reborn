using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const uint ZodiacUpgradeSucceededResultCode = 1;
    private const uint ZodiacUpgradeInvalidGridResultCode = 2;
    private const uint ZodiacUpgradeInactiveGridResultCode = 3;
    private const uint ZodiacUpgradeMaximumLevelResultCode = 4;
    private const uint ZodiacUpgradeLevelGateResultCode = 5;
    private const uint ZodiacUpgradeInsufficientEnergyResultCode = 6;
    private const uint ZodiacUpgradeInsufficientTalentResultCode = 7;
    private const uint ZodiacUpgradeWrongOwnerResultCode = 8;
    private const uint ZodiacUpgradeIdentityConflictResultCode = 0;

    private readonly IZodiacSkillGridUpgradeCommandExecutor?
        _zodiacSkillGridUpgradeCommands;

    private async Task HandleDurableZodiacSkillGridUpgradeAsync(
        ZodiacSyncRequest request,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }

        if (!_session.IsSecure)
        {
            CommandMetrics.Record(
                CommandFamily.ZodiacSkillGridUpgrade,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.InvalidIntent);
            Console.Error.WriteLine(
                "[zodiac] rejected operation marker without secure " +
                $"transport account={_account.Id} " +
                $"character={_character.Name}");
            return;
        }

        if (_zodiacSkillGridUpgradeCommands is null)
        {
            RecordDurableZodiacSkillGridUpgradeUnavailable(
                clientOperationId,
                "provider is not configured");
            return;
        }

        if (!ZodiacSkillGridUpgradeCommandEnvelope.TryCreateCommand(
                clientOperationId,
                request.Value1,
                out var command))
        {
            if (clientOperationId == Guid.Empty)
            {
                RecordDurableZodiacSkillGridUpgradeUnavailable(
                    clientOperationId,
                    "operation marker is empty");
                return;
            }

            CommandMetrics.Record(
                CommandFamily.ZodiacSkillGridUpgrade,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.InvalidIntent);
            await SendSecureZodiacSkillGridUpgradeResultAsync(
                clientOperationId,
                SecureLegacyCommandDisposition.Rejected,
                ZodiacUpgradeInvalidGridResultCode,
                authoritativeRevision: 0,
                cancellationToken);
            return;
        }

        var envelope = ZodiacSkillGridUpgradeCommandEnvelope.Create(
            new CommandSubject(_account.Id, _character.Id),
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);

        ZodiacSkillGridUpgradeExecutionResult execution;
        try
        {
            execution =
                await _registry
                    .ExecuteDurableZodiacSkillGridUpgradeAsync(
                        _session,
                        _account.Id,
                        _character,
                        _zodiacSkillGridUpgradeCommands,
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
        catch (Exception ex)
        {
            RecordDurableZodiacSkillGridUpgradeUnavailable(
                clientOperationId,
                ex.Message);
            return;
        }

        if (!TryResolveDurableZodiacSkillGridUpgradeTerminal(
                execution,
                out var secureDisposition,
                out var resultCode,
                out var authoritativeRevision,
                out var outcome))
        {
            RecordDurableZodiacSkillGridUpgradeUnavailable(
                clientOperationId,
                $"unknown execution disposition {execution.Disposition}");
            return;
        }

        CommandMetrics.Record(
            envelope.Family,
            envelope.IdentityStrength,
            outcome);

        if (execution.Disposition ==
                ZodiacSkillGridUpgradeExecutionDisposition.Committed &&
            execution.Receipt?.Succeeded == true)
        {
            // SID 101 increments the native grid view unconditionally. It is
            // therefore emitted only for the first committed success.
            await _session.SendAsync(
                PacketBuilder.ZodiacSkillGridUpgraded(
                    command.GridIndex),
                cancellationToken,
                "DurableZodiacSkillGridUpgraded");
        }

        if (execution.HasAuthoritativeProjection)
        {
            await SendDurableZodiacSkillGridUpgradeProjectionAsync(
                cancellationToken);
        }
        Console.WriteLine(
            "[zodiac] durable skill-grid upgrade " +
            $"account={_account.Id} character={_character.Name} " +
            $"grid={command.GridIndex} disposition={execution.Disposition} " +
            $"result={resultCode} level=" +
            $"{_character.ZodiacSkillGridLevels[command.GridIndex]} " +
            $"energy={_character.ZodiacEnergy}." +
            $"{_character.ZodiacEnergyRemainderX100:00} " +
            $"talent={_character.TalentPoints}");

        // The authenticated result is deliberately last. The shim must not
        // retire the UUID before the stock client receives its authoritative
        // status and full Zodiac projection.
        await SendSecureZodiacSkillGridUpgradeResultAsync(
            clientOperationId,
            secureDisposition,
            resultCode,
            authoritativeRevision,
            cancellationToken);
    }

    private async Task
        SendDurableZodiacSkillGridUpgradeProjectionAsync(
            CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "DurableZodiacSkillGridStatusRefresh");
        await SendZodiacFullSyncAsync(cancellationToken);
    }

    private ValueTask SendSecureZodiacSkillGridUpgradeResultAsync(
        Guid clientOperationId,
        SecureLegacyCommandDisposition disposition,
        uint resultCode,
        ulong authoritativeRevision,
        CancellationToken cancellationToken) =>
        _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)CommandFamily.ZodiacSkillGridUpgrade,
                resultCode,
                authoritativeRevision,
                clientOperationId),
            cancellationToken);

    private static bool
        TryResolveDurableZodiacSkillGridUpgradeTerminal(
            ZodiacSkillGridUpgradeExecutionResult execution,
            out SecureLegacyCommandDisposition secureDisposition,
            out uint resultCode,
            out ulong authoritativeRevision,
            out CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(execution);
        secureDisposition = default;
        resultCode = ZodiacUpgradeIdentityConflictResultCode;
        authoritativeRevision = 0;
        outcome = CommandOutcome.ProviderUnavailable;

        switch (execution.Disposition)
        {
            case ZodiacSkillGridUpgradeExecutionDisposition.Committed:
            case ZodiacSkillGridUpgradeExecutionDisposition.Duplicate:
            case ZodiacSkillGridUpgradeExecutionDisposition
                .TerminalRejected:
                if (execution.Receipt is not { } receipt)
                {
                    return false;
                }

                secureDisposition = execution.Disposition switch
                {
                    ZodiacSkillGridUpgradeExecutionDisposition.Committed =>
                        SecureLegacyCommandDisposition.Applied,
                    ZodiacSkillGridUpgradeExecutionDisposition.Duplicate =>
                        SecureLegacyCommandDisposition.Replayed,
                    _ => SecureLegacyCommandDisposition.Rejected
                };
                resultCode = ResultCode(receipt.Status);
                authoritativeRevision = receipt.AggregateRevision is
                    { } revision
                    ? checked((ulong)revision)
                    : 0;
                outcome = execution.Disposition switch
                {
                    ZodiacSkillGridUpgradeExecutionDisposition.Committed =>
                        CommandOutcome.Accepted,
                    ZodiacSkillGridUpgradeExecutionDisposition.Duplicate =>
                        CommandOutcome.Duplicate,
                    _ => CommandOutcome.PreconditionFailed
                };
                return true;

            case ZodiacSkillGridUpgradeExecutionDisposition
                .RequestHashConflict:
                secureDisposition =
                    SecureLegacyCommandDisposition.Conflict;
                outcome = CommandOutcome.RequestHashConflict;
                return true;

            case ZodiacSkillGridUpgradeExecutionDisposition.InvalidIntent:
                secureDisposition =
                    SecureLegacyCommandDisposition.Rejected;
                outcome = CommandOutcome.InvalidIntent;
                return true;

            case ZodiacSkillGridUpgradeExecutionDisposition
                .PreconditionFailed:
                secureDisposition =
                    SecureLegacyCommandDisposition.Rejected;
                resultCode = ZodiacUpgradeWrongOwnerResultCode;
                outcome = CommandOutcome.PreconditionFailed;
                return true;

            default:
                return false;
        }
    }

    private static uint ResultCode(
        ZodiacSkillGridUpgradeReceiptStatus status) =>
        status switch
        {
            ZodiacSkillGridUpgradeReceiptStatus.Succeeded =>
                ZodiacUpgradeSucceededResultCode,
            ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid =>
                ZodiacUpgradeInactiveGridResultCode,
            ZodiacSkillGridUpgradeReceiptStatus.MaximumLevelReached =>
                ZodiacUpgradeMaximumLevelResultCode,
            ZodiacSkillGridUpgradeReceiptStatus.ZodiacLevelTooLow =>
                ZodiacUpgradeLevelGateResultCode,
            ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy =>
                ZodiacUpgradeInsufficientEnergyResultCode,
            ZodiacSkillGridUpgradeReceiptStatus
                .InsufficientTalentPoints =>
                ZodiacUpgradeInsufficientTalentResultCode,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private void RecordDurableZodiacSkillGridUpgradeUnavailable(
        Guid clientOperationId,
        string reason)
    {
        CommandMetrics.Record(
            CommandFamily.ZodiacSkillGridUpgrade,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[zodiac] durable skill-grid upgrade unresolved; operation " +
            $"remains pending account={_account?.Id} " +
            $"character={_character?.Name ?? "<none>"} " +
            $"operationId={clientOperationId}: {reason}");
    }
}
