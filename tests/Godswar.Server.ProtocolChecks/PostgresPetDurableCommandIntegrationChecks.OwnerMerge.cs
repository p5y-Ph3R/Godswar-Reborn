using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertOwnerMergeAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        GameplayItemContent itemContent,
        IPetOwnerMergeContentCatalog ownerMergeContent)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            PetAptitude.Smart);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var hatchEnvelope = CreateOwnerMergeActivationEnvelope(
            subject,
            correlation,
            Guid.NewGuid(),
            fixture.EggSlot);
        var hatch = await executor.ExecuteAsync(hatchEnvelope);
        Check.True(
            hatch.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            hatch.Receipt?.Status == PetDurableReceiptStatus.EggHatched,
            "owner Merge fixture hatches one authoritative pet");
        var petId = hatch.Receipt!.PetId;

        await SeedOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var expected = PetOwnerMergeContributionCalculator.Calculate(
            await ReadOwnerMergeTotalSavvyAsync(dataSource, petId),
            ownerMergeContent);
        var expectedEffects = PetOwnerMergeStoredBonusCodec
            .ToStoredValues(expected);
        var before = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var beforeStats = await ReadProjectedMergeStatsAsync(
            connectionString,
            fixture,
            itemContent);
        Check.True(
            !before.Contributes &&
            before.BonusCount == 0 &&
            before.AuditCount == 0,
            "owner Merge fixture starts unmerged without any bag item");

        var mergeEnvelope = CreateOwnerMergeToggleEnvelope(
            subject,
            correlation,
            Guid.NewGuid());
        var concurrentMerge = await Task.WhenAll(
            executor.ExecuteAsync(mergeEnvelope),
            restarted.ExecuteAsync(mergeEnvelope));
        AssertCommitAndDuplicate(
            concurrentMerge,
            PetDurableReceiptStatus.OwnerMerged,
            "concurrent owner Merge");
        var mergeReceipt = concurrentMerge.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var merged = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var mergedEffects = await ReadOwnerMergeEffectsAsync(
            dataSource,
            petId);
        AssertOwnerMergeEffects(
            mergedEffects,
            expectedEffects,
            merged.PetRevision);
        Check.True(
            merged.Contributes &&
            merged.PetRevision == before.PetRevision + 1 &&
            merged.BonusCount == 18 &&
            merged.InventoryRevision == before.InventoryRevision &&
            merged.AuditCount == 1 &&
            merged.CommittedAuditCount == 1 &&
            merged.EmptyConsumedAuditCount == 1,
            "owner Merge atomically sets the flag and writes all stored rows without changing inventory");

        var mergedStats = await ReadProjectedMergeStatsAsync(
            connectionString,
            fixture,
            itemContent);
        Check.True(
            mergedStats.IsStrictlyGreaterThan(beforeStats),
            "all owner Merge channels reach the calculated character projection");

        var replayed = await restarted.ExecuteAsync(mergeEnvelope);
        var afterReplay = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            replayed.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replayed.Receipt == mergeReceipt &&
            afterReplay == merged,
            "owner Merge replay returns the canonical receipt without another mutation or audit");

        await AssertActiveOwnerMergeLifecycleAsync(
            connectionString,
            dataSource,
            executor,
            restarted,
            fixture,
            subject,
            correlation,
            petId,
            before,
            merged,
            beforeStats,
            itemContent);

        await MakeOwnerMergeEnergyIncompleteAsync(dataSource, petId);
        var beforeRejection = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var rejectedEnvelope = CreateOwnerMergeToggleEnvelope(
            subject,
            correlation,
            Guid.NewGuid());
        var rejected = await executor.ExecuteAsync(rejectedEnvelope);
        var afterRejection = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            rejected.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            rejected.Receipt?.Status ==
                PetDurableReceiptStatus.OwnerMergeEnergyNotFull &&
            !afterRejection.Contributes &&
            afterRejection.PetRevision == beforeRejection.PetRevision &&
            afterRejection.BonusCount == 0 &&
            afterRejection.InventoryRevision ==
                beforeRejection.InventoryRevision &&
            afterRejection.AuditCount == beforeRejection.AuditCount + 1 &&
            afterRejection.RejectedAuditCount == 1 &&
            afterRejection.EmptyConsumedAuditCount ==
                afterRejection.AuditCount,
            "owner Merge rejection leaves pet value and inventory unchanged while retaining an explicit audit");
    }

    private static CommandEnvelope<BagItemActivationCommand>
        CreateOwnerMergeActivationEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            Guid operationId,
            int bagSlot) =>
        PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        operationId,
                        correlation.ConnectionId),
                    bagSlot)));

    private static CommandEnvelope<PetOwnerMergeToggleCommand>
        CreateOwnerMergeToggleEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            Guid operationId) =>
        PlayerOwnershipTestFences.Bind(
            PetOwnerMergeToggleCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetOwnerMergeToggleCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        operationId,
                        correlation.ConnectionId))));

    private static async Task SeedOwnerMergeStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var pet = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET is_carried = true,
                is_summoned = true,
                activity_state = 'owned',
                soul_contract_stage = 6,
                has_soul_contract = true,
                current_energy = maximum_energy,
                amity = @amity,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId;
            """,
            connection,
            transaction))
        {
            pet.Parameters.AddWithValue("petId", petId);
            pet.Parameters.AddWithValue("characterId", characterId);
            pet.Parameters.AddWithValue(
                "amity",
                PetManagerPlanner.MinimumOwnerMergeAmity);
            Check.Equal(
                1,
                await pet.ExecuteNonQueryAsync(),
                "owner Merge fixture enables one innate merge talent");
        }
        await transaction.CommitAsync();
    }

    private static async Task<PetSavvy> ReadOwnerMergeTotalSavvyAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                ARRAY(
                    SELECT initial_savvy + added_savvy
                    FROM public.character_pet_stat_values
                    WHERE pet_id = @petId
                    ORDER BY stat_code
                ),
                soul_contract_stage
            FROM public.character_pets
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The owner Merge fixture pet disappeared.");
        }
        var values = reader.GetFieldValue<decimal[]>(0);
        var soulContractStage = checked((byte)reader.GetInt16(1));
        if (values.Length != 6)
        {
            throw new InvalidDataException(
                "The owner Merge fixture requires six savvy rows.");
        }
        var raw = new PetSavvy(
            values[0],
            values[1],
            values[2],
            values[3],
            values[4],
            values[5]);
        return PetSoulContractPolicy.ResolveDisplayedTotal(
            raw,
            soulContractStage);
    }

    private static async Task<OwnerMergePersistenceState>
        ReadOwnerMergeStateAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.contributes_to_character,
                pet.revision,
                pet.current_energy,
                (
                    SELECT count(*)
                    FROM public.character_pet_character_bonuses bonus
                    WHERE bonus.pet_id = pet.id
                ),
                character_row.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit audit
                    WHERE audit.pet_id_snapshot = pet.id
                      AND audit.operation = 'owner_merge'
                ),
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit audit
                    WHERE audit.pet_id_snapshot = pet.id
                      AND audit.operation = 'owner_merge'
                      AND audit.outcome = 'committed'
                ),
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit audit
                    WHERE audit.pet_id_snapshot = pet.id
                      AND audit.operation = 'owner_merge'
                      AND audit.outcome = 'rejected'
                ),
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit audit
                    WHERE audit.pet_id_snapshot = pet.id
                      AND audit.operation = 'owner_merge'
                      AND audit.consumed_items = '[]'::jsonb
                )
            FROM public.character_pets pet
            JOIN public.character_base character_row
              ON character_row.id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The owner Merge persistence state disappeared.");
        }
        return new OwnerMergePersistenceState(
            reader.GetBoolean(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8));
    }

    private static async Task<IReadOnlyList<OwnerMergeEffectState>>
        ReadOwnerMergeEffectsAsync(
            NpgsqlDataSource dataSource,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT effect_code, effect_value, revision
            FROM public.character_pet_character_bonuses
            WHERE pet_id = @petId
            ORDER BY effect_code;
            """);
        command.Parameters.AddWithValue("petId", petId);
        var values = new List<OwnerMergeEffectState>(
            PetOwnerMergeStoredBonusCodec.TotalCount);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(new OwnerMergeEffectState(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.GetInt64(2)));
        }
        return values;
    }

    private static void AssertOwnerMergeEffects(
        IReadOnlyList<OwnerMergeEffectState> actual,
        IReadOnlyList<PetOwnerMergeStoredBonusValue> expected,
        long expectedRevision)
    {
        Check.Equal(PetOwnerMergeStoredBonusCodec.TotalCount, actual.Count,
            "owner Merge persists every native and internal bonus row");
        for (var index = 0; index < expected.Count; index++)
        {
            Check.True(
                actual[index].EffectCode == expected[index].Code &&
                actual[index].EffectValue == expected[index].Value &&
                actual[index].Revision == expectedRevision,
                $"owner Merge effect row {index} is exact and revisioned");
        }
    }

    private static async Task<OwnerMergeProjectedStats>
        ReadProjectedMergeStatsAsync(
            string connectionString,
            PetFixture fixture,
            GameplayItemContent itemContent)
    {
        await using var store = new PostgresGameStore(
            connectionString,
            itemContent);
        var stats = await store.GetCharacterStatsAsync(
            fixture.AccountId,
            fixture.CharacterId) ??
            throw new InvalidDataException(
                "The owner Merge character projection disappeared.");
        return OwnerMergeProjectedStats.From(stats);
    }

    private static async Task MakeOwnerMergeEnergyIncompleteAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET current_energy = maximum_energy - 1,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "owner Merge rejection fixture lowers energy once");
    }

}
