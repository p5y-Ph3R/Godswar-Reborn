using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecuteClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        LockedCharacter character,
        int itemTemplateId,
        PetDurableReceiptStatus successStatus,
        CancellationToken cancellationToken)
    {
        var held = await HoldsUtilityItemAnywhereAsync(
            connection, transaction, envelope.Subject.CharacterId,
            itemTemplateId, cancellationToken);
        if (held)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerClaimAlreadyHeld);
        }
        var bagSlot = await LockFirstEmptyUtilityBagSlotAsync(
            connection, transaction, envelope.Subject.CharacterId,
            cancellationToken);
        if (!bagSlot.HasValue)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerBagFull);
        }

        var created = await InsertUtilityItemAsync(
            connection, transaction, envelope.Subject.CharacterId,
            bagSlot.Value, itemTemplateId, cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection, transaction, envelope.Subject.CharacterId,
            character.InventoryRevision, cancellationToken);
        var evidence = UtilityEvidence(
            envelope.Command.Operation,
            itemTemplateId: itemTemplateId,
            itemInstanceId: created.ItemId,
            kitBagSlot: bagSlot.Value);
        return UtilityTransition(
            successStatus,
            evidence,
            kitBagSlot: bagSlot.Value,
            mutations:
            [
                new InventoryMutation(
                    created.ItemId,
                    "add",
                    null,
                    created.AfterState,
                    "pet_manager_charm_claim",
                    inventoryRevision)
            ]);
    }
}
