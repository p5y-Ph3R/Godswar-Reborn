using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const int MaximumPetGrowthAcceptBindings = 32;
    private readonly Dictionary<Guid, Guid> _petGrowthAcceptBindings = [];
    private readonly Queue<Guid> _petGrowthAcceptBindingOrder = [];
    private Guid _activePetGrowthPreviewOperationId;

    private async Task<PetDurableReceipt?>
        HandleDurablePetGrowthResetAsync(
            PetCommandOperationIdentity identity,
            PetGrowthResetOperation operation,
            Guid previewOperationId,
            CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetGrowthReset,
                identity,
                "provider or active character is unavailable");
            return null;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetGrowthResetCommand(
            identity,
            operation,
            previewOperationId);
        var unownedEnvelope = identity.IsSecureClient
            ? PetGrowthResetCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetGrowthResetCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return null;
        }

        return await ExecuteAndCompletePetCommandAsync(
            identity,
            CommandFamily.PetGrowthReset,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task<bool> SendPetGrowthResetProjectionAsync(
        PetDurableReceipt receipt,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }

        if (receipt.Status == PetDurableReceiptStatus.PetGrowthPreviewed)
        {
            if (receipt.KitBagSlot < 0 || receipt.GrowthPreview is null)
            {
                return false;
            }
            if (KitBagSlots.GetItem(
                    _character.KitBag,
                    receipt.KitBagSlot).IsEmpty)
            {
                await _session.SendAsync(
                    PacketBuilder.StorageItemKitBagDelete(
                        receipt.KitBagSlot),
                    cancellationToken,
                    "DurablePetGrowthPreviewSlotClear");
            }
            await SendKitBagRefreshAsync(cancellationToken);

            // A preview is deliberately not an authoritative pet-stat
            // mutation. Opcode 10286 is sent only after OK commits it.
            return true;
        }

        if (receipt.Status is not (
                PetDurableReceiptStatus.PetGrowthAccepted or
                PetDurableReceiptStatus.PetGrowthReset))
        {
            return true;
        }

        var pet = pets.SingleOrDefault(
            candidate => candidate.PetId == receipt.PetId);
        if (pet is null ||
            !pet.GrowthRevealed ||
            pet.StatValues.Count != 6)
        {
            return false;
        }

        // OK commits the six previewed values. Extended opcode 10286 refreshes
        // the existing pet object's Basic and level-scaled Added fields in
        // place. Do not send opcode 10237 here: the
        // stock client treats it as a destructive pet collection rebuild,
        // clears live selection/presentation pointers, and emits an
        // unsolicited Recall for the still-summoned companion.
        // Presence is intentionally not validated here. A secure duplicate
        // can arrive after the player recalls or switches pets; this narrow
        // projection neither changes nor transmits pet presence.
        // The next login/bootstrap remains the full-state reconciliation.
        return await SendPetProgressionRefreshAsync(
            pet,
            "DurablePetGrowthResetRefresh",
            cancellationToken);
    }

    private bool TryBuildPetGrowthResetResultSubIds(
        PetDurableReceipt receipt,
        out int[] responseSubIds)
    {
        responseSubIds = receipt.Status switch
        {
            PetDurableReceiptStatus.PetNotTaken =>
                [PetManagerProtocol.GrowthResetNoPetResultSubId],
            PetDurableReceiptStatus.PhoenixFeatherNotFound =>
                [PetManagerProtocol.GrowthResetMissingFeatherResultSubId],
            PetDurableReceiptStatus.PetGrowthPreviewUnavailable =>
                [PetManagerProtocol.GrowthResetPreviewUnavailableResultSubId],
            _ => []
        };
        if (responseSubIds.Length > 0)
        {
            return true;
        }
        if (receipt.Status == PetDurableReceiptStatus.PetGrowthPreviewed &&
            receipt.GrowthPreview is { IsValid: true } preview)
        {
            if (!TryResolvePetGrowthComparisonRates(
                    receipt,
                    preview,
                    out var previewRates,
                    out var previewCurrentRates))
            {
                return false;
            }
            responseSubIds = PetManagerProtocol.BuildGrowthResetSuccessPage(
                previewRates,
                previewCurrentRates);
            return true;
        }
        if (receipt.Status != PetDurableReceiptStatus.PetGrowthReset)
        {
            return false;
        }

        var pet = _characterLoadSnapshot?.Pets.SingleOrDefault(
            candidate => candidate.PetId == receipt.PetId);
        if (pet is null || !pet.GrowthRevealed ||
            pet.StatValues.Count != 6)
        {
            return false;
        }
        var ordered = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .ToArray();
        if (ordered.Where((stat, index) => stat.StatCode != index + 1).Any())
        {
            return false;
        }
        var currentRates = ordered
            .Select(static stat =>
                stat.BaseGrowthRate + stat.GrowthAcceleration)
            .ToArray();
        responseSubIds = PetManagerProtocol.BuildGrowthResetSuccessPage(
            currentRates,
            currentRates);
        return true;
    }

    private bool TryResolvePetGrowthComparisonRates(
        PetDurableReceipt receipt,
        PetGrowthPreviewSnapshot preview,
        out decimal[] previewRates,
        out decimal[] currentRates)
    {
        previewRates = [];
        currentRates = [];
        var pet = _characterLoadSnapshot?.Pets.SingleOrDefault(
            candidate => candidate.PetId == preview.PetId);
        if (pet is null ||
            !preview.HasAuthoritativeCurrentRates ||
            receipt.PetId != preview.PetId ||
            receipt.PetLevel != preview.PetLevel ||
            receipt.PetRevision != preview.ExpectedPetRevision ||
            pet.Level != preview.PetLevel ||
            pet.Revision != preview.ExpectedPetRevision ||
            preview.UsesRebirthCountWidenedRates &&
                pet.CompletedRebirths != preview.CompletedRebirths ||
            pet.StatValues.Count != 6)
        {
            return false;
        }

        var ordered = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .ToArray();
        if (ordered.Where((stat, index) => stat.StatCode != index + 1).Any() ||
            ordered.Any(static stat => stat.BaseGrowthRate < 0))
        {
            return false;
        }

        var frozenRates = preview.ToOrderedCurrentRates();
        if (!ordered.Select(static stat => stat.BaseGrowthRate)
                .SequenceEqual(frozenRates))
        {
            return false;
        }
        var acceleration = ordered
            .Select(static stat => stat.GrowthAcceleration)
            .ToArray();
        var proposedModifier = preview.UsesRebirthCountWidenedRates
            ? preview.ToOrderedRebirthModifiers()
            : acceleration;
        previewRates = preview.ToOrderedRates()
            .Select((rate, index) => rate + proposedModifier[index])
            .ToArray();
        currentRates = frozenRates
            .Select((rate, index) => rate + acceleration[index])
            .ToArray();
        return true;
    }

    private Guid BindPetGrowthAcceptOperation(Guid operationId)
    {
        if (_petGrowthAcceptBindings.TryGetValue(
                operationId,
                out var existing))
        {
            return existing;
        }

        var preview = _activePetGrowthPreviewOperationId;
        _petGrowthAcceptBindings.Add(operationId, preview);
        _petGrowthAcceptBindingOrder.Enqueue(operationId);
        while (_petGrowthAcceptBindingOrder.Count >
               MaximumPetGrowthAcceptBindings)
        {
            _petGrowthAcceptBindings.Remove(
                _petGrowthAcceptBindingOrder.Dequeue());
        }
        return preview;
    }

    private async Task<bool> TryActivatePetGrowthPreviewAsync(
        PetDurableReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.Status != PetDurableReceiptStatus.PetGrowthPreviewed ||
            receipt.GrowthPreview is not { IsValid: true } preview ||
            _petDurableCommands is not IPetGrowthPreviewLifecycleStore store ||
            _account is null ||
            _character is null ||
            !TryGetCharacterOwnership(_character, out var ownership))
        {
            return false;
        }

        if (!await store.IsCurrentAsync(
                new CommandSubject(_account.Id, _character.Id),
                ownership,
                _commandConnectionId,
                preview.PreviewOperationId,
                cancellationToken))
        {
            return false;
        }
        _activePetGrowthPreviewOperationId = preview.PreviewOperationId;
        return true;
    }

    private async Task DiscardPetGrowthPreviewForSessionExitAsync()
    {
        try
        {
            if (_petDurableCommands is IPetGrowthPreviewLifecycleStore store &&
                _account is not null &&
                _character is not null &&
                TryGetCharacterOwnership(_character, out var ownership))
            {
                await store.DiscardForSessionAsync(
                    new CommandSubject(_account.Id, _character.Id),
                    ownership,
                    _commandConnectionId,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                "[pet] failed discarding Phoenix Growth preview on " +
                $"session exit: {exception.Message}");
        }
        finally
        {
            _activePetGrowthPreviewOperationId = Guid.Empty;
            _petGrowthAcceptBindings.Clear();
            _petGrowthAcceptBindingOrder.Clear();
        }
    }
}
