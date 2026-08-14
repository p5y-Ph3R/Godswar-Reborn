using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetManagerUtilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var transition = envelope.Command.Operation switch
        {
            PetManagerUtilityOperation.CheckGrowth =>
                await ExecuteGrowthCheckAsync(
                    connection, transaction, envelope, character,
                    cancellationToken),
            PetManagerUtilityOperation.Seal =>
                await ExecuteSealAsync(
                    connection, transaction, envelope, character,
                    cancellationToken),
            PetManagerUtilityOperation.Unseal =>
                await ExecuteUnsealAsync(
                    connection, transaction, envelope, character,
                    cancellationToken),
            PetManagerUtilityOperation.ClaimPetCall =>
                await ExecuteClaimAsync(
                    connection, transaction, envelope, character,
                    itemTemplateId: 11003,
                    PetDurableReceiptStatus.PetCallClaimed,
                    cancellationToken),
            PetManagerUtilityOperation.ClaimMerge =>
                await ExecuteClaimAsync(
                    connection, transaction, envelope, character,
                    itemTemplateId: 11004,
                    PetDurableReceiptStatus.PetMergeClaimed,
                    cancellationToken),
            PetManagerUtilityOperation.ChangeGender =>
                await ExecuteGenderChangeAsync(
                    connection, transaction, envelope, character,
                    cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(envelope.Command.Operation))
        };
        await InsertPetManagerUtilityAuditAsync(
            connection,
            transaction,
            envelope,
            transition,
            cancellationToken);
        return transition;
    }

    private static PetManagerUtilityEvidence UtilityEvidence(
        PetManagerUtilityOperation operation,
        LockedUtilityPet? pet = null,
        int itemTemplateId = 0,
        long itemInstanceId = 0,
        int kitBagSlot = -1,
        byte previousSex = 0,
        byte newSex = 0,
        PetManagerGrowthEvidence? growth = null,
        PetManagerUtilityPetState? beforeState = null,
        PetManagerUtilityPetState? afterState = null) =>
        new(
            operation,
            pet?.PetId ?? 0,
            itemTemplateId,
            itemInstanceId,
            kitBagSlot,
            previousSex,
            newSex,
            growth,
            beforeState,
            afterState);

    private static PetTransition UtilityTransition(
        PetDurableReceiptStatus status,
        PetManagerUtilityEvidence evidence,
        LockedUtilityPet? pet = null,
        int kitBagSlot = -1,
        long petRevision = 0,
        IReadOnlyList<InventoryMutation>? mutations = null) =>
        new(
            status,
            KitBagSlot: kitBagSlot,
            PetId: evidence.PetId,
            PetLevel: pet?.Level ?? 0,
            PetExperience: pet?.Experience ?? 0,
            PetRevision: petRevision == 0
                ? pet?.Revision ?? 0
                : petRevision,
            IsCarried: pet?.IsCarried ?? false,
            IsSummoned: pet?.IsSummoned ?? false,
            InventoryMutations: mutations,
            PetManagerUtility: evidence);

    private sealed record LockedUtilityPet(
        long PetId,
        short SpeciesId,
        string Name,
        byte Sex,
        short Level,
        long Experience,
        bool IsBound,
        bool IsCarried,
        bool IsSummoned,
        bool ContributesToCharacter,
        string ActivityState,
        long Revision,
        bool GrowthRevealed,
        bool HasSoulContract,
        byte SoulContractStage,
        int CurrentEnergy,
        int MaximumEnergy)
    {
        public PetManagerUtilityPetState State(
            long? revision = null,
            string? activityState = null,
            bool? isCarried = null,
            bool? isSummoned = null,
            bool? contributesToCharacter = null,
            bool? growthRevealed = null,
            bool? hasSoulContract = null,
            byte? soulContractStage = null,
            byte? sex = null,
            int? currentEnergy = null) =>
            new(
                activityState ?? ActivityState,
                isCarried ?? IsCarried,
                isSummoned ?? IsSummoned,
                contributesToCharacter ?? ContributesToCharacter,
                growthRevealed ?? GrowthRevealed,
                hasSoulContract ?? HasSoulContract,
                soulContractStage ?? SoulContractStage,
                sex ?? Sex,
                revision ?? Revision)
            {
                CurrentEnergy = currentEnergy ?? CurrentEnergy,
                MaximumEnergy = MaximumEnergy
            };
    }
}
