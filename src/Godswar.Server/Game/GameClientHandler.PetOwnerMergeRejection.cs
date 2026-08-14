using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendPetOwnerMergeRejectionProjectionAsync(
        PetDurableReceipt receipt,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        CancellationToken cancellationToken)
    {
        // Native owner Merge has no rejection packet. Re-project the current
        // authoritative gauge for an energy rejection so the stock UI shows
        // why Merge did not start. Delayed results must not use receipt state.
        if (receipt.Status !=
                PetDurableReceiptStatus.OwnerMergeEnergyNotFull ||
            pets.SingleOrDefault(candidate =>
                candidate.PetId == receipt.PetId) is not { } currentPet)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.PetEnergy(
                currentPet.CurrentEnergy,
                currentPet.MaximumEnergy),
            cancellationToken,
            "PetOwnerMergeEnergyRejected");
    }
}
