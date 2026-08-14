using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertPetToPetMergeAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        GameplayItemContent itemContent)
    {
        var fixture = await CreatePetMergeFixtureAsync(
            connectionString,
            itemContent);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        await SeedSecondPetEggAsync(dataSource, fixture.CharacterId);
        var first = await executor.ExecuteAsync(
            CreatePetMergeHatchEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                fixture.EggSlot));
        // These are two independent fixture setup actions. Expire the stock
        // one-second egg cooldown so the second hatch can establish the
        // deputy without weakening production cooldown enforcement.
        await ExpireConsumableCooldownAsync(
            dataSource,
            fixture.CharacterId,
            cooldownGroup: 4740);
        var second = await executor.ExecuteAsync(
            CreatePetMergeHatchEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                fixture.EggSlot - 1));
        Check.True(
            first.IsSuccess && second.IsSuccess,
            "pet Merge fixture hatches primary and deputy pets");
        var primaryPetId = second.Receipt!.PetId;
        var deputyPetId = first.Receipt!.PetId;
        await SeedPetMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            primaryPetId,
            deputyPetId);
        var before = await ReadPetMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            primaryPetId,
            deputyPetId);

        var operationId = Guid.NewGuid();
        var envelope = CreatePetMergeEnvelope(
            subject,
            correlation,
            operationId,
            primaryPetId,
            deputyPetId,
            quantity: 5);
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(envelope),
            restarted.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(
            concurrent,
            PetDurableReceiptStatus.PetToPetMerged,
            "concurrent pet-to-pet Merge");
        var receipt = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var after = await ReadPetMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            primaryPetId,
            deputyPetId);
        AssertCommittedPetMerge(before, after, receipt);
        await AssertPetMergeAuditAfterStateAsync(
            dataSource,
            fixture.CharacterId,
            before.DeputyPetId,
            after.PrimaryRevision,
            after.CompletedMerges,
            after.PrimaryRank,
            receipt.PetMergeDelta ??
                throw new InvalidDataException(
                    "Committed pet Merge receipt lost its exact gains."));

        var replay = await restarted.ExecuteAsync(envelope);
        var afterReplay = await ReadPetMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            primaryPetId,
            deputyPetId);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == receipt &&
            PetMergeStateEquals(afterReplay, after),
            "pet Merge replay preserves random gains, deputy deletion, and material state");

        var conflict = await restarted.ExecuteAsync(
            CreatePetMergeEnvelope(
                subject,
                correlation,
                operationId,
                primaryPetId,
                deputyPetId,
                quantity: 4));
        var afterConflict = await ReadPetMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            primaryPetId,
            deputyPetId);
        Check.True(
            conflict.Disposition ==
                PetDurableExecutionDisposition.RequestHashConflict &&
            PetMergeStateEquals(afterConflict, after),
            "one pet Merge operation ID cannot authorize changed material quantity");

        await AssertZeroSpiritPetMergeAsync(
            connectionString, dataSource, executor, restarted, itemContent);
    }

    private static void AssertCommittedPetMerge(
        PetMergePersistenceState before,
        PetMergePersistenceState after,
        PetDurableReceipt receipt)
    {
        var delta = receipt.PetMergeDelta ??
            throw new InvalidDataException(
                "Committed pet Merge receipt lost its exact gains.");
        var expected = new[]
        {
            delta.Agility, delta.Strength, delta.Accuracy,
            delta.Technique, delta.Wisdom, delta.Luck
        };
        Check.True(
            after.PrimaryExists && !after.DeputyExists &&
            after.PrimaryRank ==
                before.PrimaryRank + delta.Rank / 100m &&
            after.PrimaryRevision == before.PrimaryRevision + 1 &&
            after.CompletedMerges == before.CompletedMerges + 1 &&
            after.InventoryRevision == before.InventoryRevision + 1 &&
            after.MaterialCount == 0 &&
            after.InventoryLedgerCount == 5 &&
            after.InventoryOutboxCount == 1 &&
            after.PetMergeAuditCount == 1 &&
            after.PetMergeCommittedAuditCount == 1 &&
            after.ConsumedAuditQuantity == 5 &&
            after.CommandInboxCount == 1 &&
            after.CommandAuditCount == 1 &&
            after.CommandOutboxCount == 1 &&
            after.EvidenceViewCount == 1 &&
            after.InboxContractVersion ==
                PetDurablePersistenceCodec.PetToPetMergeContractVersion,
            "pet Merge atomically persists primary, deputy, five stacks, and durable evidence");
        Check.True(
            receipt.DeputyPetId == before.DeputyPetId &&
            delta.Rank <= 650 &&
            expected.All(value => value is >= 0 and <= 420),
            "pet Merge receipt preserves bounded six-stat and rank rolls");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                after.InitialSavvy[index] -
                    before.InitialSavvy[index] == expected[index] / 100m,
                $"pet Merge stat {index + 1} matches its durable wire delta");
        }
    }

    private static CommandEnvelope<BagItemActivationCommand>
        CreatePetMergeHatchEnvelope(
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

    private static CommandEnvelope<PetToPetMergeCommand>
        CreatePetMergeEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            Guid operationId,
            long primaryPetId,
            long deputyPetId,
            byte quantity) =>
        PlayerOwnershipTestFences.Bind(
            PetToPetMergeCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetToPetMergeCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        operationId,
                        correlation.ConnectionId),
                    primaryPetId,
                    deputyPetId,
                    PetToPetMergeCommandEnvelope.StandardMaterialItemId,
                    quantity)));

    private static async Task SeedSecondPetEggAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, 89, 10150,
                @eggAptitude, 1, 1, 1, 0, 0
            );
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "eggAptitude",
            (short)PetAptitude.Smart);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "pet Merge fixture inserts its deputy egg");
    }

    private static async Task<PetFixture> CreatePetMergeFixtureAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        await using var store = new PostgresGameStore(
            connectionString,
            itemContent);
        var account = await store.LoginOrCreateAccountAsync(
            $"b12_merge_{token}",
            string.Empty);
        var character = await store.CreateCharacterAsync(
            account.Id,
            new GameCharacter
            {
                Name = $"Merge{token}",
                Camp = GameDefaults.SpartaCamp,
                Profession = 0,
                Level = 80
            });
        const int eggSlot = 90;
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var egg = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slot, 10150,
                @eggAptitude, 1, 1, 1, 0, 0
            );
            """,
            connection))
        {
            egg.Parameters.AddWithValue("characterId", character.Id);
            egg.Parameters.AddWithValue("slot", (short)eggSlot);
            egg.Parameters.AddWithValue(
                "eggAptitude",
                (short)PetAptitude.Smart);
            Check.Equal(
                1,
                await egg.ExecuteNonQueryAsync(),
                "pet Merge fixture inserts its primary egg");
        }
        await using var transaction =
            await connection.BeginTransactionAsync();
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            account.Id,
            character.Id);
        await transaction.CommitAsync();
        return new PetFixture(account.Id, character.Id, eggSlot);
    }

    private static async Task SeedPetMergeStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long primaryPetId,
        long deputyPetId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var pets = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET level = 30,
                rank = 10.00,
                is_carried = (id = @primaryPetId),
                is_summoned = (id = @primaryPetId),
                contributes_to_character = false,
                activity_state = 'owned',
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE user_id = @characterId
              AND id = ANY(@petIds);

            UPDATE public.character_pet_stat_values
            SET added_savvy =
                    (base_growth_rate + growth_acceleration) * 30,
                revision = revision + 1
            WHERE pet_id = ANY(@petIds);
            """,
            connection,
            transaction))
        {
            pets.Parameters.AddWithValue("characterId", characterId);
            pets.Parameters.AddWithValue("primaryPetId", primaryPetId);
            pets.Parameters.AddWithValue(
                "petIds",
                new[] { primaryPetId, deputyPetId });
            Check.Equal(
                14,
                await pets.ExecuteNonQueryAsync(),
                "pet Merge fixture prepares both pets and scaled Added values");
        }
        await using (var materials = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            SELECT
                @characterId, 1, slot, 10103,
                1, 1, 0, 1, 0, 0
            FROM generate_series(80, 84) AS slot;
            """,
            connection,
            transaction))
        {
            materials.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                5,
                await materials.ExecuteNonQueryAsync(),
                "pet Merge fixture inserts five one-item material stacks");
        }
        await transaction.CommitAsync();
    }

}
