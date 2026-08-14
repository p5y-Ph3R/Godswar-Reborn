using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    public const string PackedSealOwnershipCheckName =
        "PostgreSQL packed Seal Jade ownership boundary";

    public static async Task RunPackedSealOwnershipOnlyAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {PackedSealOwnershipCheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                $"SKIP {PackedSealOwnershipCheckName} requires a " +
                $"disposable B03/B12 database; received '{database}'");
            return;
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();
        var boundOwner = await CreateFixtureAsync(connectionString);
        var unboundOwner = await CreateFixtureAsync(connectionString);
        var recipient = await CreateFixtureAsync(connectionString);

        var bound = await SeedPackedOwnershipCaseAsync(
            connectionString, dataSource, boundOwner,
            isBound: true, bagSlot: 70);
        await AssertBoundPackedOwnershipAsync(
            dataSource, bound, boundOwner.CharacterId,
            recipient.CharacterId);

        var unbound = await SeedPackedOwnershipCaseAsync(
            connectionString, dataSource, unboundOwner,
            isBound: false, bagSlot: 71);
        await AssertUnboundPackedOwnershipAsync(
            dataSource, unbound, unboundOwner.CharacterId,
            recipient.CharacterId);
    }

    private static async Task<PackedOwnershipCase>
        SeedPackedOwnershipCaseAsync(
            string connectionString,
            NpgsqlDataSource dataSource,
            PetFixture owner,
            bool isBound,
            short bagSlot)
    {
        if (!isBound)
        {
            await using var unbindEgg = dataSource.CreateCommand(
                """
                UPDATE public.character_items
                SET bound = 0
                WHERE user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @eggSlot
                  AND prop_id = 10150;
                """);
            unbindEgg.Parameters.AddWithValue(
                "characterId",
                owner.CharacterId);
            unbindEgg.Parameters.AddWithValue(
                "eggSlot",
                checked((short)owner.EggSlot));
            Check.Equal(
                1,
                await unbindEgg.ExecuteNonQueryAsync(),
                "unbound ownership fixture prepares one unbound egg");
        }

        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();
        var hatch = await store.HatchPetEggAsync(
            owner.AccountId,
            owner.CharacterId,
            owner.EggSlot);
        Check.True(
            hatch.Succeeded,
            "packed ownership fixture creates a fully authoritative pet");
        var petId = hatch.PetId;

        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var pet = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET activity_state = 'sealed',
                is_carried = false,
                is_summoned = false,
                contributes_to_character = false,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND bound = @isBound
              AND activity_state = 'owned';
            """,
            connection,
            transaction))
        {
            pet.Parameters.AddWithValue("characterId", owner.CharacterId);
            pet.Parameters.AddWithValue("petId", petId);
            pet.Parameters.AddWithValue("isBound", isBound);
            Check.Equal(
                1,
                await pet.ExecuteNonQueryAsync(),
                "packed ownership fixture seals one hatched pet");
        }

        long itemId;
        await using (var item = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack)
            VALUES (
                @characterId, 1, @bagSlot, 10109,
                1, 1, @bound, 1)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            item.Parameters.AddWithValue(
                "characterId",
                owner.CharacterId);
            item.Parameters.AddWithValue("bagSlot", bagSlot);
            item.Parameters.AddWithValue(
                "bound",
                checked((short)(isBound ? 1 : 0)));
            itemId = Convert.ToInt64(await item.ExecuteScalarAsync());
        }

        var sealRequestId = Guid.NewGuid();
        await using (var link = new NpgsqlCommand(
            """
            INSERT INTO public.sealed_pet_items (
                item_instance_id, pet_id, owner_character_id,
                seal_request_id, item_instance_id_snapshot,
                pet_id_snapshot, owner_character_id_snapshot,
                pet_species_id_snapshot, pet_name_snapshot,
                pet_bound_snapshot)
            SELECT
                @itemId, pet.id, @characterId,
                @sealRequestId, @itemId,
                pet.id, @characterId,
                pet.species_id, pet.name, pet.bound
            FROM public.character_pets pet
            WHERE pet.id = @petId;
            """,
            connection,
            transaction))
        {
            link.Parameters.AddWithValue("itemId", itemId);
            link.Parameters.AddWithValue("petId", petId);
            link.Parameters.AddWithValue(
                "characterId",
                owner.CharacterId);
            link.Parameters.AddWithValue("sealRequestId", sealRequestId);
            Check.Equal(
                1,
                await link.ExecuteNonQueryAsync(),
                "packed ownership fixture creates one authoritative link");
        }
        await transaction.CommitAsync();
        return new PackedOwnershipCase(itemId, petId, isBound);
    }

    private static async Task AssertBoundPackedOwnershipAsync(
        NpgsqlDataSource dataSource,
        PackedOwnershipCase packed,
        int ownerId,
        int recipientId)
    {
        await AssertPackedMutationRejectedAsync(
            dataSource,
            """
            UPDATE public.character_items
            SET user_id = @recipientId
            WHERE id = @itemId;
            """,
            packed.ItemId,
            recipientId,
            "bound packed jade cannot transfer ownership");
        await AssertPackedMutationRejectedAsync(
            dataSource,
            """
            UPDATE public.character_items
            SET bound = 0, user_id = @recipientId
            WHERE id = @itemId;
            """,
            packed.ItemId,
            recipientId,
            "one statement cannot clear and transfer a bound packed jade");
        await AssertPackedMutationRejectedAsync(
            dataSource,
            """
            UPDATE public.sealed_pet_items
            SET pet_bound_snapshot = false
            WHERE item_instance_id = @itemId;
            """,
            packed.ItemId,
            recipientId,
            "packed binding snapshot is immutable");
        await AssertPackedBindingMismatchRejectedAsync(
            dataSource,
            packed.ItemId,
            "bound packed jade cannot be cleared in a prior transaction");

        var owners = await ReadPackedOwnersAsync(
            dataSource, packed.ItemId, packed.PetId);
        Check.True(
            owners == new PackedOwners(
                ownerId, ownerId, ownerId, 1, true, true),
            "all bound packed-pet authority remains with its original owner");
    }

    private static async Task AssertUnboundPackedOwnershipAsync(
        NpgsqlDataSource dataSource,
        PackedOwnershipCase packed,
        int ownerId,
        int recipientId)
    {
        var initial = await ReadPackedOwnersAsync(
            dataSource, packed.ItemId, packed.PetId);
        await TransferPackedItemAsync(
            dataSource, packed.ItemId, recipientId);
        var transferred = await ReadPackedOwnersAsync(
            dataSource, packed.ItemId, packed.PetId);
        Check.True(
            initial == new PackedOwners(
                ownerId, ownerId, ownerId, 0, false, false,
                initial.PetRevision) &&
            transferred == new PackedOwners(
                recipientId, recipientId, recipientId,
                0, false, false, initial.PetRevision + 1),
            "unbound packed jade atomically transfers its item, pet, " +
            "link, and pet revision");

        await TransferPackedItemAsync(
            dataSource, packed.ItemId, ownerId);
        var returned = await ReadPackedOwnersAsync(
            dataSource, packed.ItemId, packed.PetId);
        Check.True(
            returned == new PackedOwners(
                ownerId, ownerId, ownerId, 0, false, false,
                transferred.PetRevision + 1),
            "unbound packed jade can transfer back without losing authority");
    }

    private static async Task AssertPackedMutationRejectedAsync(
        NpgsqlDataSource dataSource,
        string sql,
        long itemId,
        int recipientId,
        string description)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("recipientId", recipientId);
        try
        {
            await command.ExecuteNonQueryAsync();
            throw new InvalidOperationException(
                $"{description} unexpectedly succeeded.");
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            // Expected authoritative rejection.
        }
    }

    private static async Task AssertPackedBindingMismatchRejectedAsync(
        NpgsqlDataSource dataSource,
        long itemId,
        string description)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_items
            SET bound = 0
            WHERE id = @itemId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", itemId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "mismatch probe reaches the deferred validation boundary");
        try
        {
            await transaction.CommitAsync();
            throw new InvalidOperationException(
                $"{description} unexpectedly succeeded.");
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            // Expected deferred integrity rejection.
        }
    }

    private static async Task TransferPackedItemAsync(
        NpgsqlDataSource dataSource,
        long itemId,
        int recipientId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_items
            SET user_id = @recipientId
            WHERE id = @itemId;
            """);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("recipientId", recipientId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "unbound packed ownership transfer updates one item");
    }

    private static async Task<PackedOwners> ReadPackedOwnersAsync(
        NpgsqlDataSource dataSource,
        long itemId,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                item.user_id, pet.user_id, link.owner_character_id,
                item.bound, pet.bound, link.pet_bound_snapshot,
                pet.revision
            FROM public.character_items item
            JOIN public.sealed_pet_items link
              ON link.item_instance_id = item.id
            JOIN public.character_pets pet
              ON pet.id = link.pet_id
            WHERE item.id = @itemId
              AND pet.id = @petId;
            """);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Packed ownership authority is missing.");
        }
        return new PackedOwners(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt16(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetInt64(6));
    }

    private sealed record PackedOwnershipCase(
        long ItemId,
        long PetId,
        bool IsBound);

    private sealed record PackedOwners(
        int ItemOwnerId,
        int PetOwnerId,
        int LinkOwnerId,
        short ItemBound,
        bool PetBound,
        bool BoundSnapshot,
        long PetRevision = 0);
}
