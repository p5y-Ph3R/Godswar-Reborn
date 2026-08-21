using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IEquipmentForgeCommandExecutor?
        _equipmentForgeCommands;

    private async Task HandleDurableForgeStartAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }

        var hasSelections = TryCaptureForgeRequest(out var request);
        // Clear before the first await. A staged command is submitted through
        // the executor so its canonical request hash is compared with any
        // permanent inbox entry. A selectionless retry can only replay an
        // already committed receipt and can never mutate inventory.
        ClearForgeSelection();

        if (_equipmentForgeCommands is null)
        {
            RecordDurableForgeUnavailable(
                clientOperationId,
                "provider is not configured");
            return;
        }

        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        EquipmentForgeExecutionResult execution;
        try
        {
            execution = hasSelections
                ? await ExecuteDurableForgeAsync(
                    subject,
                    clientOperationId,
                    request!,
                    ownership,
                    cancellationToken)
                : await _equipmentForgeCommands.TryReplayAsync(
                    subject,
                    ownership,
                    clientOperationId,
                    cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.EquipmentForge,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception ex)
        {
            RecordDurableForgeUnavailable(
                clientOperationId,
                ex.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        if (!execution.IsDurable)
        {
            await HandleNonDurableForgeOutcomeAsync(
                clientOperationId,
                execution.Disposition,
                cancellationToken);
            return;
        }

        EquipmentForgeExecutionReceipt receipt;
        try
        {
            receipt = execution.Receipt ??
                throw new InvalidDataException(
                    "A durable Forge outcome has no receipt.");
            ValidateDurableForgeReceipt(receipt);
            // Do not acknowledge a durable outcome until the live session has
            // reloaded both authoritative bag contents and Silver. A read
            // failure leaves the UUID pending so the client can retry it.
            await ReloadDurableForgeProjectionAsync(
                ownership,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.EquipmentForge,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableForgeUnavailable(
                clientOperationId,
                $"projection reload failed: {ex.Message}");
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        CommandMetrics.Record(
            CommandFamily.EquipmentForge,
            CommandIdentityStrength.ClientOperationId,
            MapDurableForgeOutcome(execution.Disposition));
        await SendDurableForgeReceiptAsync(
            clientOperationId,
            receipt,
            execution.Disposition,
            cancellationToken);

        Console.WriteLine(
            "[forge] durable command completed " +
            $"account={_account.Id} character={_character!.Name} " +
            $"status={receipt.Status} outcome={execution.Disposition} " +
            $"roll={receipt.Roll} chance={receipt.Probability} " +
            $"silver={receipt.SilverSpent} " +
            $"walletRevision={receipt.WalletRevision} " +
            $"inventoryRevision={receipt.InventoryRevision}");
    }

    private async Task<EquipmentForgeExecutionResult>
        ExecuteDurableForgeAsync(
            CommandSubject subject,
            Guid clientOperationId,
            ForgeTransactionRequest request,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken)
    {
        var oddsMaterials = request.OddsMaterials
            .Select(static selection =>
                CreateDurableForgeSelection(
                    EquipmentForgeCommandItemRole.OddsMaterial,
                    selection))
            .ToArray();
        if (!EquipmentForgeCommandEnvelope.TryCreateCommand(
                clientOperationId,
                CreateDurableForgeSelection(
                    EquipmentForgeCommandItemRole.Equipment,
                    request.Equipment),
                CreateDurableForgeSelection(
                    EquipmentForgeCommandItemRole.PrimaryMaterial,
                    request.PrimaryMaterial),
                oddsMaterials,
                out var command))
        {
            return EquipmentForgeExecutionResult.InvalidIntent();
        }

        var envelope = EquipmentForgeCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command) with
        {
            Ownership = ownership
        };
        return await _equipmentForgeCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private async Task HandleNonDurableForgeOutcomeAsync(
        Guid clientOperationId,
        EquipmentForgeExecutionDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (disposition ==
            EquipmentForgeExecutionDisposition.ReplayNotFound)
        {
            CommandMetrics.Record(
                CommandFamily.EquipmentForge,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.PreconditionFailed);
            Console.WriteLine(
                "[forge] durable replay not found; operation remains " +
                $"pending account={_account!.Id} " +
                $"character={_character!.Name} " +
                $"operationId={clientOperationId}");
            return;
        }

        if (disposition is not (
                EquipmentForgeExecutionDisposition.RequestHashConflict or
                EquipmentForgeExecutionDisposition.InvalidIntent or
                EquipmentForgeExecutionDisposition.PreconditionFailed))
        {
            RecordDurableForgeUnavailable(
                clientOperationId,
                $"unknown execution disposition {disposition}");
            return;
        }

        var conflict = disposition ==
            EquipmentForgeExecutionDisposition.RequestHashConflict;
        CommandMetrics.Record(
            CommandFamily.EquipmentForge,
            CommandIdentityStrength.ClientOperationId,
            MapDurableForgeOutcome(disposition));
        await _session.SendAsync(
            PacketBuilder.ForgeResult(success: false, resultKind: 0),
            cancellationToken,
            "ForgeRejected");
        await SendSecureForgeResultAsync(
            clientOperationId,
            resultCode: 0,
            conflict
                ? SecureLegacyCommandDisposition.Conflict
                : SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            cancellationToken);
    }

    private async Task SendDurableForgeReceiptAsync(
        Guid clientOperationId,
        EquipmentForgeExecutionReceipt receipt,
        EquipmentForgeExecutionDisposition executionDisposition,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.ForgeResult(
                receipt.Succeeded,
                resultKind: receipt.Committed ? 1 : 0),
            cancellationToken,
            receipt.Committed ? "ForgeResult" : "ForgeRejected");
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "ForgePlayerStatus");
        await SendKitBagRefreshAsync(cancellationToken);
        await SendSecureForgeResultAsync(
            clientOperationId,
            checked((uint)receipt.Status),
            executionDisposition switch
            {
                EquipmentForgeExecutionDisposition.Committed =>
                    SecureLegacyCommandDisposition.Applied,
                EquipmentForgeExecutionDisposition.Duplicate =>
                    SecureLegacyCommandDisposition.Replayed,
                _ => SecureLegacyCommandDisposition.Rejected
            },
            receipt.InventoryRevision,
            cancellationToken);
    }

    private ValueTask SendSecureForgeResultAsync(
        Guid clientOperationId,
        uint resultCode,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            throw new InvalidOperationException(
                "Durable Forge identity requires the secure transport.");
        }

        return _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)CommandFamily.EquipmentForge,
                resultCode,
                checked((ulong)inventoryRevision),
                clientOperationId),
            cancellationToken);
    }

    private async Task ReloadDurableForgeProjectionAsync(
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            _processRealmId,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The Forge owner changed during projection reload.");
        }

        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
        if (hydrated is null ||
            hydrated.Character.Id != _character!.Id)
        {
            throw new InvalidDataException(
                "The durable Forge character could not be reloaded.");
        }

        ApplyDurableForgeProjection(
            _character,
            hydrated.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
    }

    internal static void ApplyDurableForgeProjection(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        if (liveCharacter.Id != persistedCharacter.Id ||
            liveCharacter.AccountId != persistedCharacter.AccountId)
        {
            throw new InvalidDataException(
                "A Forge projection cannot change character identity.");
        }

        // Forge owns only the bag projection and Silver wallet field.
        // Position, vitals, Gold, and other live runtime state can be newer
        // than the asynchronously read persistence snapshot.
        liveCharacter.KitBag = persistedCharacter.KitBag;
        liveCharacter.Silver = persistedCharacter.Silver;
    }

    private void ValidateDurableForgeReceipt(
        EquipmentForgeExecutionReceipt receipt)
    {
        if (receipt.CharacterId != _character!.Id ||
            receipt.Committed !=
                (receipt.Status is
                    EquipmentForgeCommandResultStatus.Succeeded or
                    EquipmentForgeCommandResultStatus.FailedRoll))
        {
            throw new InvalidDataException(
                "The Forge receipt identity or status is inconsistent.");
        }
    }

    private void RecordDurableForgeUnavailable(
        Guid clientOperationId,
        string reason)
    {
        CommandMetrics.Record(
            CommandFamily.EquipmentForge,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[forge] durable outcome unresolved; operation remains " +
            $"pending account={_account?.Id} " +
            $"character={_character?.Name ?? "<none>"} " +
            $"operationId={clientOperationId}: {reason}");
    }

    private static EquipmentForgeCommandSelection
        CreateDurableForgeSelection(
            EquipmentForgeCommandItemRole role,
            ForgeSlotSelection selection) =>
        new(
            role,
            selection.KitBagSlot,
            selection.Quantity,
            selection.ExpectedItem.ToCompactString());

    private static CommandOutcome MapDurableForgeOutcome(
        EquipmentForgeExecutionDisposition disposition) =>
        disposition switch
        {
            EquipmentForgeExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            EquipmentForgeExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            EquipmentForgeExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            EquipmentForgeExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            EquipmentForgeExecutionDisposition.ReplayNotFound or
                EquipmentForgeExecutionDisposition.PreconditionFailed or
                EquipmentForgeExecutionDisposition.TerminalRejected =>
                CommandOutcome.PreconditionFailed,
            _ => CommandOutcome.ProviderUnavailable
        };
}
