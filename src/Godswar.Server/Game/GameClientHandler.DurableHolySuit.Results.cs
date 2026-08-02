using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendMalformedHolySuitResultAsync(
        uint npcId,
        int subId,
        Guid? clientOperationId,
        CancellationToken cancellationToken)
    {
        if (!HolySuitDesignProtocol.TryResolveOperation(
                subId,
                out var wireOperation))
        {
            return;
        }

        var operation = ToCommandOperation(wireOperation);
        var nativeResult = wireOperation switch
        {
            HolySuitWireOperation.StoreExperience =>
                HolySuitDesignProtocol.StoreAmountRequiredResultSubId,
            HolySuitWireOperation.TransformExperience =>
                HolySuitDesignProtocol.PrismAmountRequiredResultSubId,
            _ => HolySuitDesignProtocol
                .WrongTransferSelectionResultSubId
        };
        await _session.SendAsync(
            HolySuitDesignProtocol.BuildResultResponse(
                npcId,
                nativeResult),
            cancellationToken,
            "HolySuitMalformedResult");

        if (_session.IsSecure && clientOperationId.HasValue)
        {
            await SendSecureHolySuitResultAsync(
                clientOperationId.Value,
                operation,
                nativeResult,
                SecureLegacyCommandDisposition.Rejected,
                inventoryRevision: 0,
                cancellationToken);
        }
    }

    private async Task SendNonDurableHolySuitOutcomeAsync(
        uint npcId,
        HolySuitCommandOperation operation,
        HolySuitOperationIdentity identity,
        HolySuitExecutionDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (disposition == HolySuitExecutionDisposition.ReplayNotFound)
        {
            RecordHolySuitProviderUnavailable(
                operation,
                "durable replay remained unresolved");
            return;
        }

        var nativeResult = operation is
            HolySuitCommandOperation.StoreExperience or
            HolySuitCommandOperation.TransformExperience
                ? HolySuitNativeResults.WrongSelectionSubId
                : HolySuitNativeResults.TransferWrongSelectionSubId;
        await _session.SendAsync(
            HolySuitDesignProtocol.BuildResultResponse(
                npcId,
                nativeResult),
            cancellationToken,
            "HolySuitRejected");
        if (identity.IsSecureClient)
        {
            await SendSecureHolySuitResultAsync(
                identity.OperationId,
                operation,
                nativeResult,
                disposition ==
                    HolySuitExecutionDisposition.RequestHashConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected,
                inventoryRevision: 0,
                cancellationToken);
        }
    }

    private async Task SendDurableHolySuitReceiptAsync(
        uint responseNpcId,
        HolySuitOperationIdentity identity,
        HolySuitExecutionReceipt receipt,
        HolySuitExecutionDisposition disposition,
        long authoritativeInventoryRevision,
        string kitBagBefore,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            HolySuitDesignProtocol.BuildResultResponse(
                responseNpcId,
                receipt.NativeResultSubId),
            cancellationToken,
            "HolySuitResult");

        foreach (var acknowledgement in
            PacketBuilder.KitBagMutationDeletionAcknowledgements(
                kitBagBefore,
                _character!.KitBag))
        {
            await _session.SendAsync(
                acknowledgement,
                cancellationToken,
                "HolySuitKitBagDeleteAck");
        }

        await SendHolySuitAuthoritativeProjectionAsync(
            receipt.Committed,
            cancellationToken);
        if (identity.IsSecureClient)
        {
            await SendSecureHolySuitResultAsync(
                identity.OperationId,
                receipt.Operation,
                receipt.NativeResultSubId,
                disposition switch
                {
                    HolySuitExecutionDisposition.Committed =>
                        SecureLegacyCommandDisposition.Applied,
                    HolySuitExecutionDisposition.Duplicate =>
                        SecureLegacyCommandDisposition.Replayed,
                    _ => SecureLegacyCommandDisposition.Rejected
                },
                authoritativeInventoryRevision,
                cancellationToken);
        }

        Console.WriteLine(
            "[holy-suit] durable operation completed " +
            $"account={_account!.Id} character={_character!.Id} " +
            $"operation={receipt.Operation} status={receipt.Status} " +
            $"outcome={disposition} exp=" +
            $"{receipt.CharacterExperienceBefore}->" +
            $"{receipt.CharacterExperienceAfter} " +
            $"prismsCreated={receipt.PrismsCreated} " +
            $"prismsConsumed={receipt.PrismsConsumed} " +
            $"receiptInventoryRevision={receipt.InventoryRevision} " +
            $"projectionInventoryRevision=" +
            $"{authoritativeInventoryRevision}");
    }

    private ValueTask SendSecureHolySuitResultAsync(
        Guid clientOperationId,
        HolySuitCommandOperation operation,
        int nativeResultSubId,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            throw new InvalidOperationException(
                "Secure Holy Suit results require secure transport.");
        }

        return _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)HolySuitCommandEnvelope.Family(operation),
                checked((uint)nativeResultSubId),
                checked((ulong)inventoryRevision),
                clientOperationId),
            cancellationToken);
    }

    private static CommandOutcome MapHolySuitOutcome(
        HolySuitExecutionDisposition disposition) =>
        disposition switch
        {
            HolySuitExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            HolySuitExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            HolySuitExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            HolySuitExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            HolySuitExecutionDisposition.TerminalRejected or
                HolySuitExecutionDisposition.ReplayNotFound or
                HolySuitExecutionDisposition.PreconditionFailed =>
                CommandOutcome.PreconditionFailed,
            _ => CommandOutcome.ProviderUnavailable
        };

    private static void RecordHolySuitMetric(
        HolySuitCommandOperation operation,
        CommandIdentityStrength identity,
        CommandOutcome outcome) =>
        CommandMetrics.Record(
            HolySuitCommandEnvelope.Family(operation),
            identity,
            outcome);

    private void RecordHolySuitProviderUnavailable(
        HolySuitCommandOperation operation,
        string reason)
    {
        CommandMetrics.Record(
            HolySuitCommandEnvelope.Family(operation),
            _session.IsSecure
                ? CommandIdentityStrength.ClientOperationId
                : CommandIdentityStrength.ServerOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[holy-suit] durable outcome unresolved " +
            $"account={_account?.Id.ToString() ?? "<none>"} " +
            $"character={_character?.Id.ToString() ?? "<none>"} " +
            $"operation={operation} reason={reason}");
    }
}
