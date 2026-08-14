using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetToPetMergeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetToPetMergeCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var request = envelope.Command;
        var materialPublished = request is
            { MaterialItemId: 0, MaterialQuantity: 0 } ||
            _itemContent.Templates.TryGet(request.MaterialItemId, out _);
        var pets = await LockPetMergeCandidatesAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            request.PrimaryPetId,
            request.DeputyPetId,
            cancellationToken);
        var primary = pets.SingleOrDefault(
            pet => pet.PetId == request.PrimaryPetId);
        var deputy = pets.SingleOrDefault(
            pet => pet.PetId == request.DeputyPetId);
        if (primary is null || deputy is null)
        {
            return await RejectPetMergeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetMergePetNotFound,
                primary,
                deputy,
                cancellationToken);
        }

        if (!materialPublished)
        {
            return await RejectPetMergeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetMergeInvalidMaterial,
                primary,
                deputy,
                cancellationToken);
        }

        var stats = new Dictionary<long, PetMergeStats>(2);
        foreach (var pet in pets.OrderBy(static pet => pet.PetId))
        {
            var value = await LockPetMergeStatsAsync(
                connection,
                transaction,
                pet,
                cancellationToken);
            if (value is null)
            {
                return await RejectPetMergeAsync(
                    connection,
                    transaction,
                    envelope,
                    PetDurableReceiptStatus.PetMergePetUnavailable,
                    primary,
                    deputy,
                    cancellationToken);
            }
            stats.Add(pet.PetId, value);
        }

        var primaryOwned = primary.ToOwnedPet(
            envelope.Subject.CharacterId,
            stats[primary.PetId].ToSavvy(),
            ownerMerge: primary.ContributesToCharacter
                ? new PetOwnerMergeState(
                    PetOwnerStatContribution.Zero,
                    [])
                : null);
        var deputyOwned = deputy.ToOwnedPet(
            envelope.Subject.CharacterId,
            stats[deputy.PetId].ToSavvy(),
            ownerMerge: deputy.ContributesToCharacter
                ? new PetOwnerMergeState(
                    PetOwnerStatContribution.Zero,
                    [])
                : null);
        var materials = request.MaterialItemId ==
                PetToPetMergeCommandEnvelope.StandardMaterialItemId
            ? new PetMergeMaterials(request.MaterialQuantity, 0)
            : request.MaterialItemId ==
                PetToPetMergeCommandEnvelope.RestrictedMaterialItemId
                ? new PetMergeMaterials(0, request.MaterialQuantity)
                : new PetMergeMaterials(0, 0);

        var materialStacks = request.MaterialQuantity == 0
            ? Array.Empty<LockedPetMergeMaterialStack>()
            : await LockPetMergeMaterialStacksAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                request.MaterialItemId,
                cancellationToken);
        if (materialStacks.Sum(static stack => (int)stack.Item.Stack) <
            request.MaterialQuantity)
        {
            return await RejectPetMergeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetMergeInsufficientMaterial,
                primary,
                deputy,
                cancellationToken);
        }

        var random = new Random(
            RandomNumberGenerator.GetInt32(int.MaxValue));
        if (!PetMergeSavvyPolicy.TryRollGains(
                _petContent,
                primaryOwned.InitialSavvy,
                deputyOwned.InitialSavvy,
                deputyOwned.CurrentAddedSavvy,
                deputyOwned.SpeciesType,
                request.MaterialQuantity,
                random,
                out var savvyEvidence,
                out var gains) ||
            !PetMergeRankPolicy.TryRollIncrease(
                _petContent,
                primaryOwned.Rank,
                deputyOwned.Rank,
                deputyOwned.SpeciesType,
                request.MaterialQuantity,
                random,
                out var rankEvidence,
                out var rankIncrease,
                out var rankAfter))
        {
            return await RejectPetMergeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetMergeInvalidMaterial,
                primary,
                deputy,
                cancellationToken);
        }

        var outcome = new AuthoritativePetMergeOutcome(
            rankAfter,
            primaryOwned.InitialSavvy + gains);
        if (!PetManagerPlanner.TryPlanPetMerge(
                _petContent,
                primaryOwned,
                deputyOwned,
                envelope.Subject.CharacterId,
                materials,
                outcome,
                out var plan,
                out var rejection) ||
            plan is null)
        {
            return await RejectPetMergeAsync(
                connection,
                transaction,
                envelope,
                ToPetMergeReceiptStatus(rejection),
                primary,
                deputy,
                cancellationToken);
        }

        var nextPetRevision = await PersistPetMergePlanAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            primary,
            deputy,
            stats[primary.PetId],
            plan,
            cancellationToken);
        var primaryAfter = primary with
        {
            Rank = plan.PrimaryPetAfter.Rank,
            CompletedPetMerges =
                plan.PrimaryPetAfter.CompletedPetMerges,
            Revision = nextPetRevision
        };
        var consumed = await ConsumePetMergeMaterialsAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            request.MaterialQuantity,
            materialStacks,
            cancellationToken);
        var inventoryRevision = consumed.Count == 0
            ? character.InventoryRevision
            : await AdvanceInventoryRevisionAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                character.InventoryRevision,
                cancellationToken);
        var mutations = consumed.Select(value => new InventoryMutation(
            value.Stack.Item.ItemId,
            value.MutationKind,
            value.Stack.Item.BeforeState,
            value.AfterState,
            "pet_merge_material_consumed",
            inventoryRevision)).ToArray();
        await InsertPetMergeAuditAsync(
            connection,
            transaction,
            envelope,
            PetDurableReceiptStatus.PetToPetMerged,
            primary,
            primaryAfter,
            deputy,
            stats[primary.PetId].Initial,
            stats[deputy.PetId].Initial,
            plan.PrimaryPetAfter.InitialSavvy,
            savvyEvidence,
            rankEvidence,
            consumed,
            committed: true,
            cancellationToken);

        return new PetTransition(
            PetDurableReceiptStatus.PetToPetMerged,
            KitBagSlot: materialStacks.Count == 0
                ? -1
                : materialStacks[0].BagSlot,
            PetId: primary.PetId,
            PetLevel: primary.Level,
            PetExperience: primary.Experience,
            PetRevision: nextPetRevision,
            IsCarried: primary.IsCarried,
            IsSummoned: primary.IsSummoned,
            InventoryMutations: mutations,
            DeputyPetId: deputy.PetId,
            PetMergeDelta: ToPetMergeDelta(gains, rankIncrease));
    }

    private async Task<IReadOnlyList<LockedOwnerMergePet>>
        LockPetMergeCandidatesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            long primaryPetId,
            long deputyPetId,
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
              AND id = ANY(@petIds)
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "petIds",
            new[] { primaryPetId, deputyPetId });
        var result = new List<LockedOwnerMergePet>(2);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LockedOwnerMergePet(
                reader.GetInt64(0), reader.GetInt16(1), reader.GetString(2),
                reader.GetInt16(3), reader.GetInt64(4), reader.GetDecimal(5),
                reader.GetInt16(6), reader.GetInt32(7), reader.GetInt16(8),
                reader.GetInt16(9), reader.GetBoolean(10),
                checked((byte)reader.GetInt16(11)),
                reader.GetBoolean(12), reader.GetBoolean(13),
                reader.GetBoolean(14), reader.GetBoolean(15),
                reader.GetString(16), reader.GetInt32(17),
                reader.GetInt32(18), reader.GetInt32(19),
                reader.GetBoolean(20), reader.GetInt64(21),
                reader.IsDBNull(22) ? null : reader.GetString(22)));
        }
        return result;
    }

    private async Task<PetMergeStats?> LockPetMergeStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockedOwnerMergePet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                stat_code, initial_savvy, added_savvy,
                base_growth_rate, growth_acceleration,
                rarity_added_savvy, revision
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        var rows = new List<PetMergeStatRow>(6);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PetMergeStatRow(
                reader.GetInt16(0), reader.GetDecimal(1),
                reader.GetDecimal(2), reader.GetDecimal(3),
                reader.GetDecimal(4), reader.GetDecimal(5),
                reader.GetInt64(6)));
        }
        return string.Equals(
                    pet.InitialSavvySourceVersion,
                    PetSavvyRuntimeSemantics.SourceVersion,
                    StringComparison.Ordinal) &&
                rows.Count == 6 &&
                rows.Where((row, index) => row.StatCode != index + 1)
                    .Any() == false &&
                rows.All(row => row.IsValidAtLevel(pet.Level)) &&
                rows.Sum(static row => row.InitialSavvy) >=
                    rows.Sum(static row => row.RarityAddedSavvy)
            ? new PetMergeStats(rows)
            : null;
    }

    private static PetDurableReceiptStatus ToPetMergeReceiptStatus(
        PetPlanRejection rejection) =>
        rejection switch
        {
            PetPlanRejection.MissingPet or PetPlanRejection.NotOwned =>
                PetDurableReceiptStatus.PetMergePetNotFound,
            PetPlanRejection.SamePet =>
                PetDurableReceiptStatus.PetMergeSamePet,
            PetPlanRejection.PetUnavailable or
                PetPlanRejection.AlreadyMergedWithOwner or
                PetPlanRejection.InvalidPetState =>
                PetDurableReceiptStatus.PetMergePetUnavailable,
            PetPlanRejection.MustBeSummoned =>
                PetDurableReceiptStatus.PetMergeMustBeSummoned,
            PetPlanRejection.LevelTooLow =>
                PetDurableReceiptStatus.PetMergeLevelTooLow,
            PetPlanRejection.RestrictedMaterialRequiresBoundPet =>
                PetDurableReceiptStatus
                    .PetMergeRestrictedMaterialRequiresBoundPet,
            _ => PetDurableReceiptStatus.PetMergeInvalidMaterial
        };

    private static PetToPetMergeDelta ToPetMergeDelta(
        PetSavvy gains,
        ushort rankIncrease) =>
        new(
            ToHundredths(gains.Agility),
            ToHundredths(gains.Strength),
            ToHundredths(gains.Accuracy),
            ToHundredths(gains.Technique),
            ToHundredths(gains.Wisdom),
            ToHundredths(gains.Luck),
            rankIncrease);

    private static int ToHundredths(decimal value)
    {
        var scaled = checked(value * 100m);
        return scaled == decimal.Truncate(scaled)
            ? decimal.ToInt32(scaled)
            : throw new InvalidDataException(
                "A pet Merge gain is not an exact hundredth.");
    }

    private sealed record PetMergeStatRow(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal GrowthAcceleration,
        decimal RarityAddedSavvy,
        long Revision)
    {
        public bool IsValidAtLevel(int petLevel) =>
            StatCode is >= 1 and <= 6 &&
            InitialSavvy > 0m &&
            BaseGrowthRate > 0m &&
            GrowthAcceleration >= 0m &&
            AddedSavvy ==
                PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                    petLevel,
                    BaseGrowthRate,
                    GrowthAcceleration) &&
            RarityAddedSavvy > 0m &&
            Revision >= 0;
    }

    private sealed record PetMergeStats(
        IReadOnlyList<PetMergeStatRow> Rows)
    {
        public PetSavvy Initial => ToSavvy(
            Rows.Select(static row => row.InitialSavvy));

        public OwnerMergeSavvy ToSavvy() => new(
            Initial,
            ToSavvy(Rows.Select(static row => row.AddedSavvy)),
            ToSavvy(Rows.Select(static row => row.BaseGrowthRate)),
            ToSavvy(Rows.Select(static row => row.GrowthAcceleration)),
            ToSavvy(Rows.Select(static row => row.RarityAddedSavvy)));

        private static PetSavvy ToSavvy(IEnumerable<decimal> values)
        {
            var value = values.ToArray();
            return new(
                value[0], value[1], value[2],
                value[3], value[4], value[5]);
        }
    }
}
