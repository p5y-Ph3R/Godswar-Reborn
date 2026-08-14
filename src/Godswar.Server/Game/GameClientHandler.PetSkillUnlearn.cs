using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<PetDurableReceipt?>
        HandleDurablePetSkillUnlearnAsync(
            PetCommandOperationIdentity identity,
            int skillSlot,
            CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetSkillUnlearn,
                identity,
                "provider or active character is unavailable");
            return null;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetSkillUnlearnCommand(identity, skillSlot);
        var unownedEnvelope = identity.IsSecureClient
            ? PetSkillUnlearnCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetSkillUnlearnCommandEnvelope.CreateRawLocal(
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
            CommandFamily.PetSkillUnlearn,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task<bool> SendPetSkillUnlearnProjectionAsync(
        PetDurableReceipt receipt,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            receipt.KitBagSlot < 0)
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
                "DurablePetSkillUnlearnSlotClear");
        }
        await SendKitBagRefreshAsync(cancellationToken);

        var pet = pets.SingleOrDefault(
            candidate => candidate.PetId == receipt.PetId);
        if (pet is null ||
            !pet.IsCarried ||
            !pet.IsSummoned)
        {
            return false;
        }

        await _session.SendAsync(
            PacketBuilder.PetSkillState(pet),
            cancellationToken,
            "DurablePetSkillUnlearnStateRefresh");
        return await SendPetSkillOwnerStatRefreshAsync(
            "DurablePetSkillUnlearned",
            cancellationToken);
    }
}
