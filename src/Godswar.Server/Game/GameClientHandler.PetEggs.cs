using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandlePetEggHatchAsync(
        int kitBagSlot,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var previousKitBag = _character.KitBag;
        PetEggHatchResult result;
        try
        {
            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.HatchPetEgg);
            result = await _store.HatchPetEggAsync(
                _account.Id,
                _character.Id,
                kitBagSlot,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[pet-egg] hatch failed character={_character.Name} slot={kitBagSlot} error={ex.GetType().Name}");
            await _session.SendAsync(
                PacketBuilder.KitBagSlotIndex(
                    _character,
                    kitBagSlot),
                cancellationToken,
                "PetEggRejectedKitBagRefresh");
            return;
        }

        if (result.Character is not null)
        {
            InstallUpdatedCharacter(result.Character);
            _registry.UpdateCharacter(
                _session,
                _character,
                advanceWorldRevision: false);
        }

        if (!result.Succeeded || _character is null)
        {
            await SendPetEggBagRefreshAsync(
                previousKitBag,
                cancellationToken);
            Console.WriteLine(
                $"[pet-egg] hatch rejected character={_character?.Name ?? "<none>"} slot={kitBagSlot} status={result.Status}");
            return;
        }

        await SendPetEggBagRefreshAsync(
            previousKitBag,
            cancellationToken);
        try
        {
            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.GetOwnedPets);
            var ownedPets = await _store.GetOwnedPetsAsync(
                _account.Id,
                _character.Id,
                cancellationToken);
            await _session.SendAsync(
                PacketBuilder.OwnedPetList(ownedPets),
                cancellationToken,
                "PetEggOwnedPetListRefresh");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The hatch is already committed. Keep the session alive and let
            // the normal login bootstrap recover the pet list if this
            // post-commit projection refresh is temporarily unavailable.
            Console.WriteLine(
                $"[pet-egg] post-commit pet-list refresh deferred character={_character.Name} pet={result.PetId} error={ex.GetType().Name}");
        }

        Console.WriteLine(
            $"[pet-egg] hatched character={_character.Name} pet={result.PetId} species={result.SpeciesType} aptitude={result.Aptitude} added-savvy={result.AddedSavvy!.TotalSavvy} growth={result.Growth!.TotalGrowth:0.00}");
    }

    private async Task SendPetEggBagRefreshAsync(
        string previousKitBag,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        foreach (var acknowledgement in
                 PacketBuilder.KitBagMutationDeletionAcknowledgements(
                     previousKitBag,
                     _character.KitBag))
        {
            await _session.SendAsync(
                acknowledgement,
                cancellationToken,
                "PetEggKitBagEviction");
        }

        await SendKitBagRefreshAsync(cancellationToken);
    }
}
