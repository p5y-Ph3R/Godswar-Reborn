using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecuteGrowthCheckAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var pet = await LockSummonedUtilityPetAsync(
            connection, transaction, envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerPetNotSummoned);
        }
        var tear = await LockFirstUtilityItemAsync(
            connection, transaction, envelope.Subject.CharacterId,
            checked((int)PetItemCatalog.PixieTear), cancellationToken);
        if (tear is null)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerMaterialNotFound,
                pet);
        }

        var growth = await ReadEffectiveGrowthAsync(
            connection, transaction, pet.PetId, cancellationToken);
        var consumed = await ConsumeOneStackItemAsync(
            connection, transaction, envelope.Subject.CharacterId,
            tear.BagSlot, tear.Item, cancellationToken);
        var revision = await MarkGrowthRevealedAsync(
            connection, transaction, envelope.Subject.CharacterId,
            pet, cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection, transaction, envelope.Subject.CharacterId,
            character.InventoryRevision, cancellationToken);
        var evidence = UtilityEvidence(
            envelope.Command.Operation,
            pet,
            tear.Item.PropId,
            tear.Item.ItemId,
            tear.BagSlot,
            growth: growth,
            beforeState: pet.State(),
            afterState: pet.State(
                revision,
                growthRevealed: true));
        return UtilityTransition(
            PetDurableReceiptStatus.PetGrowthChecked,
            evidence,
            pet,
            tear.BagSlot,
            revision,
            [new InventoryMutation(
                tear.Item.ItemId,
                consumed.MutationKind,
                tear.Item.BeforeState,
                consumed.AfterState,
                "pet_growth_check",
                inventoryRevision)]);
    }

    private async Task<PetTransition> ExecuteGenderChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var pet = await LockSummonedUtilityPetAsync(
            connection, transaction, envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerPetNotSummoned);
        }
        if (!pet.IsBound)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerGenderPetUnbound,
                pet);
        }
        if (pet.ContributesToCharacter || pet.ActivityState != "owned")
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerGenderUnavailable,
                pet);
        }
        var item = await LockFirstUtilityItemAsync(
            connection, transaction, envelope.Subject.CharacterId,
            checked((int)PetItemCatalog.GenderReverser),
            cancellationToken);
        if (item is null)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerMaterialNotFound,
                pet);
        }

        var nextSex = checked((byte)(1 - pet.Sex));
        var revision = await UpdateUtilityPetSexAsync(
            connection, transaction, envelope.Subject.CharacterId,
            pet, nextSex, cancellationToken);
        var consumed = await ConsumeOneStackItemAsync(
            connection, transaction, envelope.Subject.CharacterId,
            item.BagSlot, item.Item, cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection, transaction, envelope.Subject.CharacterId,
            character.InventoryRevision, cancellationToken);
        var evidence = UtilityEvidence(
            envelope.Command.Operation,
            pet,
            item.Item.PropId,
            item.Item.ItemId,
            item.BagSlot,
            pet.Sex,
            nextSex,
            beforeState: pet.State(),
            afterState: pet.State(
                revision,
                sex: nextSex));
        return UtilityTransition(
            PetDurableReceiptStatus.PetGenderChanged,
            evidence,
            pet,
            item.BagSlot,
            revision,
            [new InventoryMutation(
                item.Item.ItemId,
                consumed.MutationKind,
                item.Item.BeforeState,
                consumed.AfterState,
                "pet_gender_change",
                inventoryRevision)]);
    }

    private static PetTransition RejectUtility(
        PetManagerUtilityOperation operation,
        PetDurableReceiptStatus status,
        LockedUtilityPet? pet = null,
        int kitBagSlot = -1) =>
        UtilityTransition(
            status,
            UtilityEvidence(
                operation,
                pet,
                kitBagSlot: kitBagSlot,
                beforeState: pet?.State()),
            pet,
            kitBagSlot);
}
