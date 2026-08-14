using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<PetDurableReceipt?>
        HandleDurablePetAppearanceChangeAsync(
        PetCommandOperationIdentity identity,
        int kitBagSlot,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetAppearanceChange,
                identity,
                "provider or active character is unavailable");
            return null;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetAppearanceChangeCommand(identity, kitBagSlot);
        var unownedEnvelope = identity.IsSecureClient
            ? PetAppearanceChangeCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetAppearanceChangeCommandEnvelope.CreateRawLocal(
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
            CommandFamily.PetAppearanceChange,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task<bool> SendPetAppearanceChangeProjectionAsync(
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        string previousKitBag,
        CancellationToken cancellationToken)
    {
        if (!receipt.Succeeded)
        {
            return true;
        }
        if (_character is null)
        {
            return false;
        }

        var current = pets.SingleOrDefault(pet => pet.PetId == receipt.PetId);
        if (disposition == PetDurableExecutionDisposition.Committed &&
            (current is null ||
             current.Revision != receipt.PetRevision ||
             receipt.AppearanceChange is not { IsValid: true } evidence ||
             current.SpeciesId != evidence.NewSpeciesId))
        {
            return false;
        }

        if (disposition == PetDurableExecutionDisposition.Committed)
        {
            foreach (var deletion in
                     PacketBuilder.KitBagMutationDeletionAcknowledgements(
                         previousKitBag,
                         _character.KitBag))
            {
                await _session.SendAsync(
                    deletion,
                    cancellationToken,
                    "DurablePetAppearanceBagMutationClear");
            }
            await SendKitBagRefreshAsync(cancellationToken);
        }

        // A retry is reconciled from the current state of the receipt pet,
        // never from historical evidence and never from whichever pet may
        // have been summoned since the original request.
        if (current is null)
        {
            return true;
        }

        var refreshesSummonedModel =
            current.IsCarried && current.IsSummoned &&
            !current.ContributesToCharacter;
        if (!refreshesSummonedModel)
        {
            await _session.SendAsync(
                PacketBuilder.PetAppearanceRefresh(
                    RequirePetContent(),
                    current),
                cancellationToken,
                "DurablePetAppearanceRefresh");
            return true;
        }

        await _session.SendAsync(
            PacketBuilder.PetOperationResult(
                checked((uint)current.PetId),
                PetOperationResultCode.RecallSucceeded),
            cancellationToken,
            "DurablePetAppearancePreviousModelRecall");

        // The patched 72-byte 10286 refresh carries the authoritative species
        // and bound flags without rebuilding the pet list. Stock 10237 is not
        // safe here: its rebuild causes an unsolicited client Recall.
        await _session.SendAsync(
            PacketBuilder.PetAppearanceRefresh(
                RequirePetContent(),
                current),
            cancellationToken,
            "DurablePetAppearanceRefresh");

        await _session.SendAsync(
            PacketBuilder.PetOperationResult(
                checked((uint)current.PetId),
                PetOperationResultCode.CallOutSucceeded),
            cancellationToken,
            "DurablePetAppearanceCallOutRefresh");
        await _session.SendAsync(
            PacketBuilder.PetWorldPresence(
                checked((uint)current.PetId),
                LocalPlayerObjectId),
            cancellationToken,
            "DurablePetAppearanceWorldRefresh");
        return true;
    }
}
