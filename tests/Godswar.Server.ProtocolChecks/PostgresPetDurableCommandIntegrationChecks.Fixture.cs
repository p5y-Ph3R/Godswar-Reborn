using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task<PetFixture> CreateFixtureAsync(
        string connectionString)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();
        var account = await store.LoginOrCreateAccountAsync(
            $"b12_pet_{token}",
            string.Empty);
        var character = await store.CreateCharacterAsync(
            account.Id,
            new GameCharacter
            {
                Name = $"Pet{token}",
                Camp = GameDefaults.SpartaCamp,
                Profession = 0,
                Level = 80
            });
        const int eggSlot = 90;
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slot, 10150,
                14, 1, 1, 1, 0, 0
            );
            """,
            connection);
        command.Parameters.AddWithValue(
            "characterId",
            character.Id);
        command.Parameters.AddWithValue("slot", (short)eggSlot);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "pet durable fixture inserts one egg");
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

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException(
                "PostgreSQL returned no database name.");
    }

    private static async Task SeedPetExperienceAsync(
        NpgsqlDataSource dataSource,
        long petId,
        long experience)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET experience = @experience
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("experience", experience);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "pet experience fixture updates exactly");
    }

    private static async Task<int> PrepareEquipmentActivationAsync(
        NpgsqlDataSource dataSource,
        PetFixture fixture)
    {
        const int bagSlot = 89;
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_items
            SET item_location = 1,
                slot_index = @bagSlot,
                updated_at = transaction_timestamp()
            WHERE user_id = @characterId
              AND item_location = 0
              AND slot_index = 3
            RETURNING id;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue("bagSlot", (short)bagSlot);
        Check.True(
            await command.ExecuteScalarAsync() is long,
            "equipment activation fixture moves starter armor to bag");
        return bagSlot;
    }

    private static async Task<PresenceReplayFixture>
        CheckPresenceReplayAsync(
        Infrastructure.Pets.PostgresPetDurableCommandExecutor executor,
        Infrastructure.Pets.PostgresPetDurableCommandExecutor restarted,
        Application.Commands.CommandSubject subject,
        Application.Commands.CommandConnectionCorrelation correlation,
        long petId,
        Application.Pets.PetPresenceCommandOperation operation,
        bool isCarried,
        bool isSummoned)
    {
        var envelope =
            PlayerOwnershipTestFences.Bind(
                Application.Pets.PetPresenceTransitionCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new Application.Pets.PetPresenceTransitionCommand(
                    Guid.NewGuid(),
                    petId,
                    operation)));
        var committed = await executor.ExecuteAsync(envelope);
        var replayed = await restarted.ExecuteAsync(envelope);
        Check.True(
            committed.Disposition ==
                Application.Pets.PetDurableExecutionDisposition.Committed &&
            replayed.Disposition ==
                Application.Pets.PetDurableExecutionDisposition.Duplicate &&
            committed.Receipt == replayed.Receipt &&
            committed.Receipt is
            {
                Status:
                    Application.Pets.PetDurableReceiptStatus.PresenceChanged
            } &&
            committed.Receipt.IsCarried == isCarried &&
            committed.Receipt.IsSummoned == isSummoned,
            $"{operation} commits once and replays exactly");
        return new PresenceReplayFixture(
            envelope,
            committed.Receipt ??
                throw new InvalidDataException(
                    "Presence replay fixture has no receipt."));
    }

    private static async Task<(bool IsCarried, bool IsSummoned)>
        ReadPetPresenceAsync(
            NpgsqlDataSource dataSource,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT is_carried, is_summoned
            FROM public.character_pets
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetBoolean(0), reader.GetBoolean(1))
            : throw new InvalidDataException(
                "The pet presence projection is missing.");
    }

    private static async Task AssertRawPostgresMutationsFailClosedAsync(
        string connectionString,
        PetFixture fixture,
        long petId)
    {
        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();
        await AssertRawBlockedAsync(
            () => store.HatchPetEggAsync(
                fixture.AccountId,
                fixture.CharacterId,
                fixture.EggSlot));
        await AssertRawBlockedAsync(
            () => store.UpgradePetLevelAsync(
                fixture.AccountId,
                fixture.CharacterId,
                petId));
        await AssertRawBlockedAsync(
            () => store.TransitionPetPresenceAsync(
                fixture.AccountId,
                fixture.CharacterId,
                petId,
                PetPresenceOperation.Recall));
    }

    private static async Task AssertRawBlockedAsync<T>(
        Func<Task<T>> operation)
    {
        try
        {
            await operation();
            throw new InvalidOperationException(
                "A raw PostgreSQL pet mutation did not fail closed.");
        }
        catch (PetDurableStreamActiveException)
        {
            // Expected after the first durable pet command.
        }
    }

    private static async Task
        AssertStreamProjectionDoesNotBlockPurgeAsync(
            NpgsqlDataSource dataSource,
            int characterId)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var delete = new NpgsqlCommand(
            """
            DELETE FROM public.character_base
            WHERE id = @characterId;
            """,
            connection,
            transaction))
        {
            delete.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                1,
                await delete.ExecuteNonQueryAsync(),
                "controlled character purge is not blocked by pet stream");
        }
        await using (var count = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM public.pet_durable_stream_versions
            WHERE character_id = @characterId;
            """,
            connection,
            transaction))
        {
            count.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                0L,
                Convert.ToInt64(await count.ExecuteScalarAsync()),
                "pet stream projection cascades during character purge");
        }
        await transaction.RollbackAsync();
    }

    private sealed record PetFixture(
        int AccountId,
        int CharacterId,
        int EggSlot);

    private sealed record PresenceReplayFixture(
        Application.Commands.CommandEnvelope<
            Application.Pets.PetPresenceTransitionCommand> Envelope,
        Application.Pets.PetDurableReceipt Receipt);
}
