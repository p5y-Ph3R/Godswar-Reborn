using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IClassSuitCommandExecutor? _classSuitCommands;

    private async Task HandleClassSuitAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var hasStagedMutation = TryResolveClassSuitStagedMutation(
            packet,
            route,
            npcId,
            subId,
            out var stagedIntent);

        if (!hasStagedMutation &&
            ClassSuitProtocol.IsExactNavigation(packet, subId) &&
            ClassSuitProtocol.TryResolveOperation(
                subId,
                out var pageOperation))
        {
            await _session.SendAsync(
                ClassSuitProtocol.BuildOperationPageResponse(
                    npcId,
                    pageOperation),
                cancellationToken,
                "ClassSuitOperationPage");
            return;
        }

        if (IsClassSuitDetailNavigation(arguments) &&
            TryResolveClassSuitDetail(subId, out var detailSubId))
        {
            await _session.SendAsync(
                ClassSuitProtocol.BuildResultResponse(
                    npcId,
                    detailSubId),
                cancellationToken,
                "ClassSuitDetailPage");
            return;
        }

        if (subId ==
            (int)ClassSuitWireOperation.AddFifthAttribute)
        {
            await _session.SendAsync(
                ClassSuitProtocol.BuildResultResponse(
                    npcId,
                    ClassSuitNativeResults.UnsupportedFifthAttribute),
                cancellationToken,
                "ClassSuitFifthAttributeUnsupported");
            return;
        }

        var exactNpcId = npcId;
        var intent = stagedIntent;
        if ((!hasStagedMutation &&
                !ClassSuitProtocol.TryReadMutation(
                    packet,
                    out exactNpcId,
                    out intent)) ||
            exactNpcId != npcId ||
            !TryMapClassSuitOperation(
                intent.Operation,
                out var operation) ||
            !ClassSuitReplayIntent.TryCreate(
                operation,
                checked((int)exactNpcId),
                route.DialogIndex,
                intent.EquipmentLocation,
                intent.EquipmentKitBagSlot,
                intent.MaterialKitBagSlot,
                intent.SecondaryMaterialKitBagSlot,
                out var replayIntent))
        {
            if (TryMapClassSuitOperation(subId, out var rejectedOperation))
            {
                await SendClassSuitMalformedAsync(
                    npcId,
                    rejectedOperation,
                    packet.ClientOperationId,
                    cancellationToken);
            }
            return;
        }

        // The stock dialog clears its visual item controls immediately before
        // its final action. Consume the server-side snapshot before awaiting
        // persistence so another packet cannot reuse the same selections.
        ClearGearEnhancerSelection();

        if (_session.IsSecure && !packet.ClientOperationId.HasValue)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                ClassSuitCommandEnvelope.Family(operation));
            await _session.SendAsync(
                ClassSuitProtocol.BuildResultResponse(
                    npcId,
                    ClassSuitNativeResults.GenericWrongSelection),
                cancellationToken,
                "ClassSuitMissingOperationIdentity");
            return;
        }
        if (!_session.IsSecure &&
            !AllowLegacyPlayerMutationFallback("class_suit"))
        {
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }
        if (_classSuitCommands is null)
        {
            CommandMetrics.Record(
                ClassSuitCommandEnvelope.Family(operation),
                _session.IsSecure
                    ? CommandIdentityStrength.ClientOperationId
                    : CommandIdentityStrength.ServerOperationId,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[class-suit] durable command provider unavailable");
            return;
        }

        var identity = _session.IsSecure
            ? ClassSuitOperationIdentity.SecureClient(
                packet.ClientOperationId!.Value)
            : ClassSuitOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
        var subject = new CommandSubject(_account.Id, _character.Id);
        var bagBefore = _character.KitBag;
        ClassSuitExecutionResult execution;
        try
        {
            execution = identity.IsSecureClient
                ? await _classSuitCommands.TryReplayAsync(
                    subject,
                    ownership,
                    replayIntent,
                    identity,
                    cancellationToken)
                : ClassSuitExecutionResult.ReplayNotFound();
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return;
            }

            if (execution.Disposition ==
                ClassSuitExecutionDisposition.ReplayNotFound)
            {
                execution = await ExecuteClassSuitAsync(
                    subject,
                    ownership,
                    identity,
                    operation,
                    npcId,
                    route.DialogIndex,
                    intent,
                    bagBefore,
                    cancellationToken);
            }
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
            Console.Error.WriteLine(
                "[class-suit] durable command failed before a safe " +
                $"reply: {exception.Message}");
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        var family = ClassSuitCommandEnvelope.Family(operation);
        CommandMetrics.Record(
            family,
            identity.Strength,
            MapClassSuitOutcome(execution.Disposition));
        if (!execution.IsDurable)
        {
            await SendClassSuitNonDurableAsync(
                npcId,
                operation,
                identity,
                execution.Disposition,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable Class Suit result has no receipt.");
        if (receipt.CharacterId != _character.Id ||
            receipt.Operation != operation ||
            receipt.Family != family ||
            receipt.ReplayIntent != replayIntent)
        {
            throw new InvalidDataException(
                "The Class Suit receipt does not match the active command.");
        }

        await ReloadDurableClassSuitProjectionAsync(
            ownership,
            receipt,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        await _session.SendAsync(
            ClassSuitProtocol.BuildResultResponse(
                npcId,
                receipt.NativeResultSubId),
            cancellationToken,
            "ClassSuitResult");
        if (receipt.Status == ClassSuitCommandResultStatus.Succeeded)
        {
            foreach (var acknowledgement in
                PacketBuilder.KitBagMutationDeletionAcknowledgements(
                    bagBefore,
                    _character.KitBag))
            {
                await _session.SendAsync(
                    acknowledgement,
                    cancellationToken,
                    "ClassSuitKitBagDeleteAck");
            }
        }
        await SendClassSuitAuthoritativeProjectionAsync(
            receipt,
            execution.Disposition.ToString(),
            cancellationToken);
        if (identity.IsSecureClient)
        {
            await SendSecureGearMentorResultAsync(
                identity.OperationId,
                family,
                receipt.NativeResultSubId,
                execution.Disposition switch
                {
                    ClassSuitExecutionDisposition.Committed =>
                        SecureLegacyCommandDisposition.Applied,
                    ClassSuitExecutionDisposition.Duplicate =>
                        SecureLegacyCommandDisposition.Replayed,
                    _ => SecureLegacyCommandDisposition.Rejected
                },
                receipt.InventoryRevision,
                cancellationToken);
        }

        Console.WriteLine(
            $"[class-suit] operation={operation} status={receipt.Status} " +
            $"outcome={execution.Disposition} revision=" +
            receipt.InventoryRevision);
    }

    private async Task<ClassSuitExecutionResult> ExecuteClassSuitAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        ClassSuitOperationIdentity identity,
        ClassSuitCommandOperation operation,
        uint npcId,
        int dialogIndex,
        ClassSuitWireIntent intent,
        string kitBag,
        CancellationToken cancellationToken)
    {
        ClassSuitCommandSelection CaptureKitBag(int slot) =>
            new(
                slot,
                KitBagSlots.GetItem(kitBag, slot).ToCompactString());

        var gear = intent.EquipmentLocation ==
            ClassSuitItemLocation.Equipment
            ? new ClassSuitCommandSelection(
                intent.EquipmentKitBagSlot,
                EquipmentSlots.GetItem(
                    _character!.Equipment,
                    _character.Profession,
                    intent.EquipmentKitBagSlot).ToCompactString(),
                ClassSuitItemLocation.Equipment)
            : CaptureKitBag(intent.EquipmentKitBagSlot);

        var primary = intent.MaterialKitBagSlot >= 0
            ? CaptureKitBag(intent.MaterialKitBagSlot)
            : (ClassSuitCommandSelection?)null;
        var secondary = intent.SecondaryMaterialKitBagSlot >= 0
            ? CaptureKitBag(intent.SecondaryMaterialKitBagSlot)
            : (ClassSuitCommandSelection?)null;
        if (!ClassSuitCommandEnvelope.TryCreateCommand(
                identity,
                operation,
                checked((int)npcId),
                dialogIndex,
                gear,
                primary,
                secondary,
                out var command))
        {
            return ClassSuitExecutionResult.InvalidIntent();
        }

        var envelope = ClassSuitCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                identity.IsSecureClient
                    ? CommandTransportKind.SecureTlsLegacy
                    : CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command) with
        {
            Ownership = ownership
        };
        return await _classSuitCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private async Task SendClassSuitMalformedAsync(
        uint npcId,
        ClassSuitCommandOperation operation,
        Guid? clientOperationId,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            ClassSuitProtocol.BuildResultResponse(
                npcId,
                ClassSuitNativeResults.GenericWrongSelection),
            cancellationToken,
            "ClassSuitMalformedSelection");
        if (_session.IsSecure && clientOperationId.HasValue)
        {
            await SendSecureGearMentorResultAsync(
                clientOperationId.Value,
                ClassSuitCommandEnvelope.Family(operation),
                ClassSuitNativeResults.GenericWrongSelection,
                SecureLegacyCommandDisposition.Rejected,
                0,
                cancellationToken);
        }
    }

    private async Task SendClassSuitNonDurableAsync(
        uint npcId,
        ClassSuitCommandOperation operation,
        ClassSuitOperationIdentity identity,
        ClassSuitExecutionDisposition disposition,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            ClassSuitProtocol.BuildResultResponse(
                npcId,
                ClassSuitNativeResults.GenericWrongSelection),
            cancellationToken,
            "ClassSuitRejected");
        if (identity.IsSecureClient)
        {
            await SendSecureGearMentorResultAsync(
                identity.OperationId,
                ClassSuitCommandEnvelope.Family(operation),
                ClassSuitNativeResults.GenericWrongSelection,
                disposition ==
                    ClassSuitExecutionDisposition.RequestHashConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected,
                0,
                cancellationToken);
        }
    }

    private static bool IsClassSuitDetailNavigation(
        IReadOnlyList<int> arguments) =>
        arguments.All(static value => value == -1) ||
        arguments.Count > 0 && arguments[0] == 0 &&
        arguments.Skip(1).All(static value => value == -1);

    private static bool TryResolveClassSuitDetail(
        int subId,
        out int detailSubId)
    {
        detailSubId = subId switch
        {
            119 => 1119,
            118 => 1118,
            203 => 2203,
            132 => 1132,
            142 => 1133,
            _ => 0
        };
        return detailSubId != 0;
    }

    private static bool TryMapClassSuitOperation(
        ClassSuitWireOperation wire,
        out ClassSuitCommandOperation operation) =>
        TryMapClassSuitOperation((int)wire, out operation);

    private static bool TryMapClassSuitOperation(
        int wire,
        out ClassSuitCommandOperation operation)
    {
        operation = (ClassSuitCommandOperation)wire;
        return wire is 100 or 101 or 102 or 104 or 105 or 106 or 108;
    }

    private static CommandOutcome MapClassSuitOutcome(
        ClassSuitExecutionDisposition disposition) =>
        disposition switch
        {
            ClassSuitExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            ClassSuitExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            ClassSuitExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            ClassSuitExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            ClassSuitExecutionDisposition.TerminalRejected or
                ClassSuitExecutionDisposition.PreconditionFailed or
                ClassSuitExecutionDisposition.ReplayNotFound =>
                CommandOutcome.PreconditionFailed,
            _ => CommandOutcome.ProviderUnavailable
        };
}
