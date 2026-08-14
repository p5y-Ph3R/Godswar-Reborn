using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<PetDurableReceipt?>
        HandleDurablePetBasicSavvyResetAsync(
            PetCommandOperationIdentity identity,
            PetBasicSavvyResetOperation operation,
            Guid previewOperationId,
            CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetBasicSavvyReset,
                identity,
                "provider or active character is unavailable");
            return null;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetBasicSavvyResetCommand(
            identity,
            operation,
            previewOperationId);
        var unownedEnvelope = identity.IsSecureClient
            ? PetBasicSavvyResetCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetBasicSavvyResetCommandEnvelope.CreateRawLocal(
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
            CommandFamily.PetBasicSavvyReset,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task<bool> SendPetBasicSavvyResetProjectionAsync(
        PetDurableReceipt receipt,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }

        var committedRoll = receipt.Status ==
                PetDurableReceiptStatus.PetBasicSavvyAccepted &&
            receipt.KitBagSlot >= 0 &&
            receipt.BasicSavvyPreview is { IsValid: true };
        if (receipt.Status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewed ||
            committedRoll)
        {
            if (receipt.KitBagSlot < 0 ||
                receipt.BasicSavvyPreview is null)
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
                    "DurablePetBasicSavvyPreviewSlotClear");
            }
            await SendKitBagRefreshAsync(cancellationToken);
            if (!committedRoll)
            {
                return true;
            }
        }

        if (receipt.Status !=
            PetDurableReceiptStatus.PetBasicSavvyAccepted)
        {
            return true;
        }

        var pet = pets.SingleOrDefault(
            candidate => candidate.PetId == receipt.PetId);
        if (pet is null ||
            !pet.IsCarried ||
            !pet.IsSummoned ||
            pet.ContributesToCharacter ||
            pet.StatValues.Count != 6 ||
            pet.Revision < receipt.PetRevision)
        {
            // A duplicate receipt can arrive after recall, a pet switch, or
            // a newer mutation. The historical reset is already durable;
            // settle it without projecting stale pet state.
            return true;
        }
        if (committedRoll &&
            pet.Revision == receipt.PetRevision &&
            !PetBasicSavvyMatchesReceipt(pet, receipt.BasicSavvyPreview!))
        {
            return true;
        }
        return
            await SendPetProgressionRefreshAsync(
                pet,
                "DurablePetBasicSavvyResetRefresh",
                cancellationToken);
    }

    private bool TryBuildPetBasicSavvyResetResultSubIds(
        PetDurableReceipt receipt,
        out int[] responseSubIds)
    {
        responseSubIds = receipt.Status switch
        {
            PetDurableReceiptStatus.PetNotTaken =>
                [PetManagerProtocol.BasicSavvyResetNoPetResultSubId],
            PetDurableReceiptStatus.FairyFeatherNotFound =>
                [PetManagerProtocol.BasicSavvyResetMissingFeatherResultSubId],
            PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable =>
                [PetManagerProtocol.BasicSavvyResetPreviewUnavailableResultSubId],
            PetDurableReceiptStatus.PetBasicSavvyPreviewed =>
                [PetManagerProtocol.BasicSavvyResetPreviewUnavailableResultSubId],
            _ => []
        };
        if (responseSubIds.Length > 0)
        {
            return true;
        }

        if (receipt.Status ==
                PetDurableReceiptStatus.PetBasicSavvyAccepted &&
            receipt.BasicSavvyPreview is not { IsValid: true })
        {
            // A rolling-upgrade replay from the retired two-phase flow has
            // no committed roll in its v2 receipt. It must remain harmless,
            // never throw and disconnect the session.
            responseSubIds =
                [PetManagerProtocol.BasicSavvyResetPreviewUnavailableResultSubId];
            return true;
        }

        if (receipt.Status ==
                PetDurableReceiptStatus.PetBasicSavvyAccepted &&
            receipt.BasicSavvyPreview is { IsValid: true } preview)
        {
            var values = preview.ToOrderedValues();
            if (receipt.Status ==
                PetDurableReceiptStatus.PetBasicSavvyAccepted)
            {
                var current = _characterLoadSnapshot?.Pets.SingleOrDefault(
                    pet => pet.PetId == receipt.PetId);
                var ordered = current?.StatValues
                    .OrderBy(static stat => stat.StatCode)
                    .ToArray();
                if (current is null ||
                    !current.IsCarried ||
                    !current.IsSummoned ||
                    current.ContributesToCharacter ||
                    current.Revision < receipt.PetRevision ||
                    ordered is not { Length: 6 } ||
                    ordered.Where((stat, index) =>
                        stat.StatCode != index + 1).Any())
                {
                    responseSubIds =
                        [PetManagerProtocol.BasicSavvyResetPreviewUnavailableResultSubId];
                    return true;
                }
                values = ordered
                    .Select(static stat => stat.InitialSavvy)
                    .ToArray();
                if (current.Revision == receipt.PetRevision &&
                    !values.SequenceEqual(preview.ToOrderedValues()))
                {
                    responseSubIds =
                        [PetManagerProtocol.BasicSavvyResetPreviewUnavailableResultSubId];
                    return true;
                }
            }
            responseSubIds =
                PetManagerProtocol.BuildBasicSavvyResetSuccessPage(
                    values);
            return true;
        }
        return false;
    }

    private static bool PetBasicSavvyMatchesReceipt(
        PetBootstrapSnapshot pet,
        PetBasicSavvyPreviewSnapshot committedRoll)
    {
        var ordered = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .ToArray();
        return ordered.Length == 6 &&
            !ordered.Where((stat, index) =>
                stat.StatCode != index + 1).Any() &&
            ordered.Select(static stat => stat.InitialSavvy)
                .SequenceEqual(committedRoll.ToOrderedValues());
    }

}
