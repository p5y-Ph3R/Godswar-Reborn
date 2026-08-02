using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IHolySuitCommandExecutor? _holySuitCommands;

    private async Task HandleHolySuitDesignAsync(
        GamePacket packet,
        uint npcId,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        if (HolySuitDesignProtocol.IsExactNavigation(packet, subId) &&
            HolySuitDesignProtocol.TryResolveOperation(
                subId,
                out var pageOperation))
        {
            HolySuitWireMetrics.RecordNavigation(pageOperation);
            if (pageOperation == HolySuitWireOperation.StoreExperience)
            {
                await SendHolySuitStorePageAsync(
                    npcId,
                    cancellationToken);
            }
            else
            {
                await _session.SendAsync(
                    HolySuitDesignProtocol.BuildOperationPageResponse(
                        npcId,
                        pageOperation),
                    cancellationToken,
                    "HolySuitOperationPage");
            }
            return;
        }

        if (!HolySuitDesignProtocol.TryReadMutation(
                packet,
                out var exactNpcId,
                out var exactDialogIndex,
                out var intent,
                out var rejection) ||
            exactNpcId != npcId ||
            exactDialogIndex != dialogIndex)
        {
            if (HolySuitDesignProtocol.IsMenuSubId(subId))
            {
                HolySuitDesignProtocol.TryResolveOperation(
                    subId,
                    out var rejectedOperation);
                HolySuitWireMetrics.RecordRejected(
                    rejectedOperation,
                    rejection);
                RecordHolySuitMetric(
                    ToCommandOperation(rejectedOperation),
                    _session.IsSecure
                        ? CommandIdentityStrength.ClientOperationId
                        : CommandIdentityStrength.ServerOperationId,
                    CommandOutcome.Malformed);
                await SendMalformedHolySuitResultAsync(
                    npcId,
                    subId,
                    packet.ClientOperationId,
                    cancellationToken);
            }
            return;
        }

        HolySuitWireMetrics.RecordMutation(intent.Operation);

        if (_session.IsSecure && !packet.ClientOperationId.HasValue)
        {
            Console.Error.WriteLine(
                "[holy-suit] rejected secure mutation without operation " +
                $"identity account={_account.Id} character={_character.Id}");
            return;
        }
        if (!_session.IsSecure &&
            !AllowLegacyPlayerMutationFallback("holy_suit"))
        {
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }
        if (_holySuitCommands is null)
        {
            RecordHolySuitProviderUnavailable(
                ToCommandOperation(intent.Operation),
                "provider is not configured");
            return;
        }

        var identity = _session.IsSecure
            ? HolySuitOperationIdentity.SecureClient(
                packet.ClientOperationId!.Value)
            : HolySuitOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
        var operation = ToCommandOperation(intent.Operation);
        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        var kitBagBefore = _character.KitBag;

        HolySuitExecutionResult execution;
        HolySuitProjectionRevisions projection;
        try
        {
            execution = identity.IsSecureClient
                ? await _holySuitCommands.TryReplayAsync(
                    subject,
                    ownership,
                    operation,
                    identity,
                    cancellationToken)
                : HolySuitExecutionResult.ReplayNotFound();
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return;
            }

            if (execution.Disposition ==
                HolySuitExecutionDisposition.ReplayNotFound)
            {
                execution = await ExecuteHolySuitAsync(
                    subject,
                    ownership,
                    identity,
                    intent,
                    npcId,
                    dialogIndex,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            RecordHolySuitMetric(
                operation,
                identity.Strength,
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
            RecordHolySuitProviderUnavailable(
                operation,
                exception.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        if (!execution.IsDurable)
        {
            RecordHolySuitMetric(
                operation,
                identity.Strength,
                MapHolySuitOutcome(execution.Disposition));
            await SendNonDurableHolySuitOutcomeAsync(
                npcId,
                operation,
                identity,
                execution.Disposition,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable Holy Suit result has no receipt.");
        try
        {
            ValidateHolySuitReceipt(
                npcId,
                dialogIndex,
                operation,
                receipt);
            projection = await ReloadDurableHolySuitProjectionAsync(
                ownership,
                receipt,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            RecordHolySuitMetric(
                operation,
                identity.Strength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            RecordHolySuitProviderUnavailable(
                operation,
                $"projection reload failed: {exception.Message}");
            return;
        }

        RecordHolySuitMetric(
            operation,
            identity.Strength,
            MapHolySuitOutcome(execution.Disposition));
        await SendDurableHolySuitReceiptAsync(
            npcId,
            identity,
            receipt,
            execution.Disposition,
            projection.InventoryRevision,
            kitBagBefore,
            cancellationToken);
    }

    private async Task<HolySuitExecutionResult> ExecuteHolySuitAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        HolySuitOperationIdentity identity,
        HolySuitWireIntent intent,
        uint npcId,
        int dialogIndex,
        CancellationToken cancellationToken)
    {
        var operation = ToCommandOperation(intent.Operation);
        var primarySlot = operation switch
        {
            HolySuitCommandOperation.StoreExperience =>
                intent.HolyBoxKitBagSlot,
            HolySuitCommandOperation.TransferExperience or
                HolySuitCommandOperation.ConsumeWare =>
                intent.EquipmentKitBagSlot,
            _ => HolySuitCommandEnvelope.NoKitBagSlot
        };
        var secondarySlot = operation switch
        {
            HolySuitCommandOperation.TransferExperience =>
                intent.HolyBoxKitBagSlot,
            HolySuitCommandOperation.ConsumeWare =>
                intent.WareKitBagSlot,
            _ => HolySuitCommandEnvelope.NoKitBagSlot
        };
        var primaryState = GetHolySuitExpectedBagState(primarySlot);
        var secondaryState = GetHolySuitExpectedBagState(secondarySlot);
        if (!HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                operation,
                checked((int)npcId),
                dialogIndex,
                primarySlot,
                primaryState,
                secondarySlot,
                secondaryState,
                operation == HolySuitCommandOperation.StoreExperience
                    ? intent.Amount
                    : 0,
                operation == HolySuitCommandOperation.TransformExperience
                    ? checked((int)intent.Amount)
                    : 0,
                out var command))
        {
            return HolySuitExecutionResult.InvalidIntent();
        }

        var correlation = new CommandConnectionCorrelation(
            _commandConnectionId,
            _session.IsSecure
                ? CommandTransportKind.SecureTlsLegacy
                : CommandTransportKind.LegacyTcp);
        var envelope = identity.IsSecureClient
            ? HolySuitCommandEnvelope.CreateSecure(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : HolySuitCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        return await _holySuitCommands!.ExecuteAsync(
            envelope with { Ownership = ownership },
            cancellationToken);
    }

    private string GetHolySuitExpectedBagState(int slot) =>
        slot == HolySuitCommandEnvelope.NoKitBagSlot
            ? CompactItemEntry.Empty.ToCompactString()
            : KitBagSlots.GetItem(
                _character!.KitBag,
                slot).ToCompactString();

    private static HolySuitCommandOperation ToCommandOperation(
        HolySuitWireOperation operation) =>
        operation switch
        {
            HolySuitWireOperation.StoreExperience =>
                HolySuitCommandOperation.StoreExperience,
            HolySuitWireOperation.TransferExperience =>
                HolySuitCommandOperation.TransferExperience,
            HolySuitWireOperation.ConsumeWare =>
                HolySuitCommandOperation.ConsumeWare,
            HolySuitWireOperation.TransformExperience =>
                HolySuitCommandOperation.TransformExperience,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private async Task SendHolySuitStorePageAsync(
        uint npcId,
        CancellationToken cancellationToken)
    {
        if (_holySuitCommands is null)
        {
            RecordHolySuitProviderUnavailable(
                HolySuitCommandOperation.StoreExperience,
                "quota provider is not configured");
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }

        HolySuitStoreQuotaSnapshot quota;
        try
        {
            quota = await _holySuitCommands.ReadStoreQuotaAsync(
                new CommandSubject(_account!.Id, _character!.Id),
                ownership,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception exception)
        {
            RecordHolySuitProviderUnavailable(
                HolySuitCommandOperation.StoreExperience,
                $"quota read failed: {exception.Message}");
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        if (quota.CharacterId != _character!.Id)
        {
            RecordHolySuitProviderUnavailable(
                HolySuitCommandOperation.StoreExperience,
                "quota projection identifies another character");
            return;
        }

        var transferredToday =
            HolySuitDesignProtocol.ClampDisplayCounter(
                quota.StoredExperienceToday);
        // The original client has no textual "unlimited" representation.
        // A battle pass therefore shows the largest encodable credit while
        // the authoritative service continues to enforce no daily ceiling.
        var transferCredit = quota.BattlePassDailyLimitExempt
            ? HolySuitDesignProtocol.MaximumEncodedCounter
            : HolySuitDesignProtocol.ClampDisplayCounter(
                quota.DailyExperienceCredit);
        await _session.SendAsync(
            HolySuitDesignProtocol.BuildStorePageResponse(
                npcId,
                transferredToday,
                transferCredit),
            cancellationToken,
            "HolySuitStorePage");
    }
}
