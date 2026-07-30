using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendPetLegacyResultAsync(
        Guid operationId,
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition executionDisposition,
        CancellationToken cancellationToken)
    {
        var disposition = receipt.Succeeded
            ? executionDisposition switch
            {
                PetDurableExecutionDisposition.Committed =>
                    SecureLegacyCommandDisposition.Applied,
                PetDurableExecutionDisposition.Duplicate =>
                    SecureLegacyCommandDisposition.Replayed,
                _ => SecureLegacyCommandDisposition.Rejected
            }
            : SecureLegacyCommandDisposition.Rejected;
        CommandMetrics.Record(
            receipt.Family,
            CommandIdentityStrength.ClientOperationId,
            executionDisposition switch
            {
                PetDurableExecutionDisposition.Committed when
                    receipt.Succeeded => CommandOutcome.Accepted,
                PetDurableExecutionDisposition.Duplicate =>
                    CommandOutcome.Duplicate,
                _ => CommandOutcome.PreconditionFailed
            });
        await _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)receipt.Family,
                (uint)receipt.Status,
                checked((ulong)receipt.AggregateRevision),
                operationId),
            cancellationToken);
    }

    internal static PetOperationResultCode
        ResolveAuthoritativePresenceResult(
            PetDurableReceipt receipt,
            bool currentPetExists,
            bool currentIsCarried,
            bool currentIsSummoned)
    {
        if (!receipt.Succeeded)
        {
            return receipt.PresenceOperation switch
            {
                1 => PetOperationResultCode.TakeFailed,
                2 => PetOperationResultCode.CallOutFailed,
                _ => PetOperationResultCode.RecallFailed
            };
        }
        if (currentPetExists &&
            currentIsCarried == receipt.IsCarried &&
            currentIsSummoned == receipt.IsSummoned)
        {
            return HistoricalPresenceResultCode(receipt);
        }
        if (currentPetExists &&
            currentIsCarried &&
            currentIsSummoned)
        {
            return PetOperationResultCode.CallOutSucceeded;
        }

        // A target that is no longer summoned must finish in the recalled
        // presentation even when this packet completes an older CallOut.
        return PetOperationResultCode.RecallSucceeded;
    }

    private static PetOperationResultCode HistoricalPresenceResultCode(
        PetDurableReceipt receipt)
    {
        if (receipt.PresenceOperation == 1)
        {
            return PetOperationResultCode.TakeSucceeded;
        }
        if (receipt.PresenceOperation == 2)
        {
            return PetOperationResultCode.CallOutSucceeded;
        }
        return PetOperationResultCode.RecallSucceeded;
    }

    private bool TryCreatePetSubject(out CommandSubject subject)
    {
        subject = default;
        if (!_session.IsSecure ||
            _account is null ||
            _character is null)
        {
            return false;
        }

        subject = new CommandSubject(_account.Id, _character.Id);
        return true;
    }

    private CommandConnectionCorrelation SecurePetCorrelation() =>
        new(
            _commandConnectionId,
            CommandTransportKind.SecureTlsLegacy);

    private void RecordPetProviderUnavailable(
        CommandFamily family,
        Guid operationId,
        string reason)
    {
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[pet] durable command unresolved; operation remains " +
            $"pending family={(ushort)family} operation={operationId}: " +
            reason);
    }
}
