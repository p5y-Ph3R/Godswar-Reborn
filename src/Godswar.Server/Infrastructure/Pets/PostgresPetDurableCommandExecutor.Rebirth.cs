using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetRebirthAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetRebirthCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var pet = await LockActivePetForRebirthAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null)
        {
            return await RejectPetRebirthAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetRebirthPetNotFound,
                pet: null,
                "active_pet_not_found",
                cancellationToken);
        }

        if (!TryResolveRebirthMaterials(
                envelope.Command,
                out var materials))
        {
            return await RejectPetRebirthAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetRebirthInvalidMaterial,
                pet,
                "invalid_material_selection",
                cancellationToken);
        }

        var stats = await LockPetRebirthStatsAsync(
            connection,
            transaction,
            pet.PetId,
            pet.Level,
            cancellationToken);
        if (stats is null ||
            !string.Equals(
                pet.InitialSavvySourceVersion,
                PetSavvyRuntimeSemantics.SourceVersion,
                StringComparison.Ordinal))
        {
            return await RejectPetRebirthAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetRebirthInvalidState,
                pet,
                "invalid_savvy_provenance",
                cancellationToken);
        }

        var ownedPet = pet.ToOwnedPet(
            envelope.Subject.CharacterId,
            stats.Value);
        var nextRebirth = checked(ownedPet.CompletedRebirths + 1);
        if (!_petContent.TryGetRebirthStep(
                nextRebirth,
                out var rebirthStep))
        {
            return await RejectPetRebirthAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetRebirthMaximumReached,
                pet,
                "maximum_rebirths_reached",
                cancellationToken);
        }

        // Run every deterministic eligibility check before drawing a roll.
        // A null outcome reaches InvalidAuthoritativeOutcome only when the
        // summoned pet and selected material class are otherwise eligible.
        _ = PetManagerPlanner.TryPlanRebirth(
            _petContent,
            ownedPet,
            envelope.Subject.CharacterId,
            materials,
            outcome: null,
            out _,
            out var eligibility);
        if (eligibility != PetPlanRejection.InvalidAuthoritativeOutcome)
        {
            return await RejectPetRebirthAsync(
                connection,
                transaction,
                envelope,
                ToPetRebirthReceiptStatus(eligibility),
                pet,
                RebirthReasonCode(eligibility),
                cancellationToken);
        }

        if (!PetRebirthExperiencePolicy.TryCalculateCarry(
                _petContent,
                pet.Level,
                rebirthStep.RequiredPetLevel,
                pet.Experience,
                out var experienceCarry))
        {
            return await RejectPetRebirthAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetRebirthInvalidState,
                pet,
                "carried_experience_overflow",
                cancellationToken);
        }

        IReadOnlyList<LockedRebirthMaterial> itemStacks = [];
        if (envelope.Command.Quantity > 0)
        {
            itemStacks = await LockRebirthMaterialStacksAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                envelope.Command.MaterialTemplateId,
                cancellationToken);
            if (itemStacks.Sum(static item => (int)item.Item.Stack) <
                envelope.Command.Quantity)
            {
                return await RejectPetRebirthAsync(
                    connection,
                    transaction,
                    envelope,
                    PetDurableReceiptStatus.PetRebirthInsufficientMaterial,
                    pet,
                    "insufficient_material",
                    cancellationToken);
            }
        }

        var growthRoll = PetRebirthSpiritPolicy.Roll(
            nextRebirth,
            envelope.Command.Quantity,
            ownedPet.GrowthAcceleration,
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var outcome = new AuthoritativePetRebirthOutcome(
            CarriedExperience: experienceCarry.TotalExperience,
            RankAfter: ownedPet.Rank,
            growthRoll.GrowthAccelerationAfter);

        if (!PetManagerPlanner.TryPlanRebirth(
                _petContent,
                ownedPet,
                envelope.Subject.CharacterId,
                materials,
                outcome,
                out var plan,
                out var rejection) ||
            plan is null)
        {
            return await RejectPetRebirthAsync(
                connection,
                transaction,
                envelope,
                ToPetRebirthReceiptStatus(rejection),
                pet,
                RebirthReasonCode(rejection),
                cancellationToken);
        }

        IReadOnlyList<ConsumedRebirthMaterial> consumed = [];
        if (envelope.Command.Quantity > 0)
        {
            consumed = await ConsumeRebirthMaterialsAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                itemStacks,
                envelope.Command.Quantity,
                cancellationToken);
        }

        var nextPetRevision = await PersistPetRebirthPlanAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            pet,
            stats.Value,
            plan,
            cancellationToken);
        IReadOnlyList<InventoryMutation> mutations = [];
        if (consumed.Count > 0)
        {
            var inventoryRevision = await AdvanceInventoryRevisionAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                character.InventoryRevision,
                cancellationToken);
            mutations = consumed
                .Select(value => new InventoryMutation(
                    value.Item.ItemId,
                    value.MutationKind,
                    value.Item.BeforeState,
                    value.AfterState,
                    "pet_rebirth",
                    inventoryRevision))
                .ToArray();
        }
        await WritePetRebirthAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            stats.Value,
            plan,
            consumed,
            cancellationToken);

        return new PetTransition(
            PetDurableReceiptStatus.PetReborn,
            KitBagSlot: consumed.Count == 0 ? -1 : consumed[0].BagSlot,
            PetId: pet.PetId,
            PetLevel: checked((short)plan.PetAfter.Level),
            PetExperience: plan.PetAfter.Experience,
            PetRevision: nextPetRevision,
            IsCarried: pet.IsCarried,
            IsSummoned: pet.IsSummoned,
            InventoryMutations: mutations,
            RebirthGrowth: new PetRebirthGrowthEvidence(
                ToGrowthVector(growthRoll.Increase)));
    }

    private bool TryResolveRebirthMaterials(
        PetRebirthCommand command,
        out PetRebirthMaterials materials)
    {
        materials = default;
        if (!PetRebirthSpiritPolicy.IsCanonicalMaterialSelection(
                command.MaterialTemplateId,
                command.Quantity))
        {
            return false;
        }
        if (command.Quantity == 0)
        {
            return true;
        }

        if (command.MaterialTemplateId ==
            checked((int)_petContent.Settings.RebirthSpiritItemId))
        {
            materials = new(command.Quantity, 0);
            return true;
        }
        if (command.MaterialTemplateId == checked((int)
                _petContent.Settings.RestrictedRebirthSpiritItemId))
        {
            materials = new(0, command.Quantity);
            return true;
        }
        return false;
    }

    private static PetDurableReceiptStatus ToPetRebirthReceiptStatus(
        PetPlanRejection rejection) =>
        rejection switch
        {
            PetPlanRejection.MissingPet or PetPlanRejection.NotOwned =>
                PetDurableReceiptStatus.PetRebirthPetNotFound,
            PetPlanRejection.LevelTooLow =>
                PetDurableReceiptStatus.PetRebirthLevelTooLow,
            PetPlanRejection.NoRebirthsRemaining or
                PetPlanRejection.MaximumRebirthsReached =>
                PetDurableReceiptStatus.PetRebirthMaximumReached,
            PetPlanRejection.SoulContractRequired =>
                PetDurableReceiptStatus.PetRebirthSoulContractRequired,
            PetPlanRejection.InvalidMaterialCount =>
                PetDurableReceiptStatus.PetRebirthInvalidMaterial,
            PetPlanRejection.RestrictedMaterialRequiresBoundPet =>
                PetDurableReceiptStatus
                    .PetRebirthRestrictedRequiresBound,
            _ => PetDurableReceiptStatus.PetRebirthInvalidState
        };

    private static string RebirthReasonCode(PetPlanRejection rejection) =>
        rejection switch
        {
            PetPlanRejection.LevelTooLow => "level_too_low",
            PetPlanRejection.NoRebirthsRemaining =>
                "no_rebirths_remaining",
            PetPlanRejection.MaximumRebirthsReached =>
                "maximum_rebirths_reached",
            PetPlanRejection.SoulContractRequired =>
                "soul_contract_required",
            PetPlanRejection.InvalidMaterialCount =>
                "invalid_material_count",
            PetPlanRejection.RestrictedMaterialRequiresBoundPet =>
                "restricted_material_requires_bound_pet",
            PetPlanRejection.PetUnavailable => "pet_unavailable",
            PetPlanRejection.AlreadyMergedWithOwner =>
                "pet_merged_with_owner",
            PetPlanRejection.MustBeSummoned => "pet_not_summoned",
            _ => "invalid_pet_state"
        };
}
