using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ToggleOwnerMergeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetOwnerMergeToggleCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        _ = character;

        var candidates = await LockOwnerMergeCandidatesAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        var pet = SelectOwnerMergePet(candidates);
        if (pet is null)
        {
            var missingStatus = candidates.Count == 0
                ? PetDurableReceiptStatus.OwnerMergePetNotFound
                : PetDurableReceiptStatus.OwnerMergeInvalidState;
            return await RejectOwnerMergeAsync(
                connection,
                transaction,
                envelope,
                missingStatus,
                pet: null,
                cancellationToken);
        }

        // Toggle-off is an escape path, not a new eligibility decision. It
        // must remain available even if the pet's Savvy rows or the stored
        // derived contribution are stale, otherwise an active Merge can
        // strand the character in boosted state.
        if (pet.ContributesToCharacter)
        {
            var stored = await ReadOwnerMergeContributionAsync(
                connection,
                transaction,
                pet.PetId,
                cancellationToken);
            var petAfter = pet.ToOwnedPet(
                envelope.Subject.CharacterId,
                default,
                new PetOwnerMergeState(stored.Contribution, [])) with
            {
                OwnerMerge = null
            };
            var unmerge = new PetOwnerMergePlan(
                petAfter,
                IsMerging: false,
                stored.Contribution,
                GrantedSkillIds: []);
            var unmergeRevision = await PersistOwnerMergePlanAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                pet,
                unmerge,
                cancellationToken);
            await InsertOwnerMergeAuditAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.OwnerUnmerged,
                pet,
                afterContributes: false,
                stored.IsCurrentRevision,
                committed: true,
                cancellationToken);
            return new PetTransition(
                PetDurableReceiptStatus.OwnerUnmerged,
                PetId: pet.PetId,
                PetLevel: pet.Level,
                PetExperience: pet.Experience,
                PetRevision: unmergeRevision,
                IsCarried: pet.IsCarried,
                IsSummoned: pet.IsSummoned);
        }

        var savvy = await ReadOwnerMergeSavvyAsync(
            connection,
            transaction,
            pet,
            cancellationToken);
        if (savvy is null)
        {
            return await RejectOwnerMergeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.OwnerMergeInvalidState,
                pet,
                cancellationToken);
        }

        var storedContribution = new OwnerMergeStoredContribution(
            PetOwnerStatContribution.Zero,
            IsCurrentRevision: true);
        var ownedPet = pet.ToOwnedPet(
            envelope.Subject.CharacterId,
            savvy.Value,
            ownerMerge: null);
        var calculatedContribution =
            PetOwnerMergeContributionCalculator.Calculate(
                ownedPet.EffectiveTotalSavvy,
                _ownerMergeContent);
        var outcome = new AuthoritativePetOwnerMergeOutcome(
            calculatedContribution,
            GrantedSkillIds: [],
            EnergyAfterMerge: ownedPet.CurrentEnergy);
        if (!PetManagerPlanner.TryToggleOwnerMerge(
                _petContent,
                ownedPet,
                envelope.Subject.CharacterId,
                outcome,
                out var plan,
                out var rejection) ||
            plan is null)
        {
            return await RejectOwnerMergeAsync(
                connection,
                transaction,
                envelope,
                ToOwnerMergeReceiptStatus(rejection),
                pet,
                cancellationToken);
        }

        var nextRevision = await PersistOwnerMergePlanAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            pet,
            plan,
            cancellationToken);
        var committedStatus = plan.IsMerging
            ? PetDurableReceiptStatus.OwnerMerged
            : PetDurableReceiptStatus.OwnerUnmerged;
        await InsertOwnerMergeAuditAsync(
            connection,
            transaction,
            envelope,
            committedStatus,
            pet,
            plan.IsMerging,
            storedContribution.IsCurrentRevision,
            committed: true,
            cancellationToken);
        return new PetTransition(
            committedStatus,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: nextRevision,
            IsCarried: pet.IsCarried,
            IsSummoned: pet.IsSummoned);
    }

    private async Task<IReadOnlyList<LockedOwnerMergePet>>
        LockOwnerMergeCandidatesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, species_id, name, level, experience, rank, aptitude,
                completed_pet_merges, completed_rebirths,
                rebirths_remaining, has_soul_contract,
                soul_contract_stage,
                has_owner_merge_talent, bound, is_carried, is_summoned,
                activity_state, current_energy, maximum_energy, amity,
                contributes_to_character, revision,
                initial_savvy_source_version
            FROM public.character_pets
            WHERE user_id = @characterId
              AND (is_carried OR contributes_to_character)
            ORDER BY contributes_to_character DESC, is_carried DESC, id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        var result = new List<LockedOwnerMergePet>(2);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LockedOwnerMergePet(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetString(2),
                reader.GetInt16(3),
                reader.GetInt64(4),
                reader.GetDecimal(5),
                reader.GetInt16(6),
                reader.GetInt32(7),
                reader.GetInt16(8),
                reader.GetInt16(9),
                reader.GetBoolean(10),
                checked((byte)reader.GetInt16(11)),
                reader.GetBoolean(12),
                reader.GetBoolean(13),
                reader.GetBoolean(14),
                reader.GetBoolean(15),
                reader.GetString(16),
                reader.GetInt32(17),
                reader.GetInt32(18),
                reader.GetInt32(19),
                reader.GetBoolean(20),
                reader.GetInt64(21),
                reader.IsDBNull(22) ? null : reader.GetString(22)));
        }

        return result;
    }

    private static LockedOwnerMergePet? SelectOwnerMergePet(
        IReadOnlyList<LockedOwnerMergePet> candidates)
    {
        var contributing = candidates
            .Where(static pet => pet.ContributesToCharacter)
            .ToArray();
        if (contributing.Length == 1)
        {
            return contributing[0];
        }
        if (contributing.Length > 1)
        {
            return null;
        }

        var carried = candidates.Where(static pet => pet.IsCarried).ToArray();
        return carried.Length == 1 ? carried[0] : null;
    }

    private static PetDurableReceiptStatus ToOwnerMergeReceiptStatus(
        PetPlanRejection rejection) =>
        rejection switch
        {
            PetPlanRejection.MissingPet or PetPlanRejection.NotOwned =>
                PetDurableReceiptStatus.OwnerMergePetNotFound,
            PetPlanRejection.PetUnavailable =>
                PetDurableReceiptStatus.OwnerMergePetUnavailable,
            PetPlanRejection.MustBeSummoned =>
                PetDurableReceiptStatus.OwnerMergeMustBeSummoned,
            PetPlanRejection.OwnerMergeTalentRequired =>
                PetDurableReceiptStatus.OwnerMergeTalentRequired,
            PetPlanRejection.EnergyNotFull =>
                PetDurableReceiptStatus.OwnerMergeEnergyNotFull,
            PetPlanRejection.InsufficientAmity =>
                PetDurableReceiptStatus.OwnerMergeInsufficientAmity,
            _ => PetDurableReceiptStatus.OwnerMergeInvalidState
        };

    private static PetSavvy ToPetSavvy(IReadOnlyList<decimal> value) =>
        new(value[0], value[1], value[2], value[3], value[4], value[5]);

    private readonly record struct OwnerMergeSavvy(
        PetSavvy Initial,
        PetSavvy Added,
        PetSavvy BaseGrowth,
        PetSavvy GrowthAcceleration,
        PetSavvy RarityAdded);

    private sealed record LockedOwnerMergePet(
        long PetId,
        short SpeciesId,
        string Name,
        short Level,
        long Experience,
        decimal Rank,
        short Aptitude,
        int CompletedPetMerges,
        short CompletedRebirths,
        short RebirthsRemaining,
        bool HasSoulContract,
        byte SoulContractStage,
        bool HasOwnerMergeTalent,
        bool IsBound,
        bool IsCarried,
        bool IsSummoned,
        string ActivityState,
        int CurrentEnergy,
        int MaximumEnergy,
        int Amity,
        bool ContributesToCharacter,
        long Revision,
        string? InitialSavvySourceVersion)
    {
        public OwnedPet ToOwnedPet(
            int ownerCharacterId,
            OwnerMergeSavvy savvy,
            PetOwnerMergeState? ownerMerge) =>
            new(
                PetId,
                ownerCharacterId,
                SpeciesId,
                Name,
                Level,
                Experience,
                Rank,
                (PetAptitude)Aptitude,
                savvy.Initial,
                savvy.Added,
                savvy.BaseGrowth,
                savvy.GrowthAcceleration,
                CompletedPetMerges,
                CompletedRebirths,
                RebirthsRemaining,
                HasSoulContract,
                HasOwnerMergeTalent,
                IsBound,
                IsSummoned,
                IsAway: !string.Equals(
                    ActivityState,
                    "owned",
                    StringComparison.Ordinal),
                CurrentEnergy,
                MaximumEnergy,
                Amity,
                ownerMerge,
                savvy.RarityAdded,
                SoulContractStage);
    }
}
