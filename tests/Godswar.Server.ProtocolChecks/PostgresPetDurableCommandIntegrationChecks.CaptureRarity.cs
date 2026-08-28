using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    public const string CaptureRarityCheckName =
        "PostgreSQL Medusa pet-capture rarity";

    public static async Task RunCaptureRarityOnlyAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CaptureRarityCheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                $"SKIP {CaptureRarityCheckName} requires a disposable " +
                $"B03/B12 database; received '{database}'");
            return;
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();

        GameplayItemContent itemContent;
        IPetContentCatalog petContent;
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            itemContent = store.ItemContent;
            petContent = store.PetContent;
        }

        await AssertStoredCaptureWeightsAsync(dataSource);
        var ownerMerge =
            await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(
                dataSource);
        var rollSource = new FixedPetCaptureRarityRollSource(500);
        var executor = new PostgresPetDurableCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            itemContent,
            petContent,
            ownerMerge,
            PetLearnedSkillContentBaseline.Create(),
            petCaptureRarityRollSource: rollSource);

        var advanced = await AssertCapturedQualityAsync(
            connectionString,
            dataSource,
            executor,
            MedusaEncounterDifficulty.Enhanced,
            PetAptitude.Weak);
        var mythic = await AssertCapturedQualityAsync(
            connectionString,
            dataSource,
            executor,
            MedusaEncounterDifficulty.Mythic,
            PetAptitude.Fool);
        Check.Equal(
            2,
            rollSource.CallCount,
            "one rarity roll occurs for each committed capture");

        var restarted = new PostgresPetDurableCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            itemContent,
            petContent,
            ownerMerge,
            PetLearnedSkillContentBaseline.Create(),
            petCaptureRarityRollSource:
                new ThrowingPetCaptureRarityRollSource());
        var replay = await restarted.ExecuteAsync(advanced.Envelope);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == advanced.Receipt &&
            await ReadCapturedQualityAsync(
                dataSource,
                advanced.CharacterId,
                advanced.BagSlot) == (short)PetAptitude.Weak,
            "a lost-result retry returns the committed egg without rerolling");

        Check.True(
            mythic.Receipt.Status == PetDurableReceiptStatus.PetCaptured,
            "Mythic capture commits through the same durable path");
        await AssertWeightTotalGuardAsync(dataSource);
    }

    private static async Task<CaptureCommit> AssertCapturedQualityAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        MedusaEncounterDifficulty difficulty,
        PetAptitude expectedAptitude)
    {
        var fixture = await CreateFixtureAsync(connectionString);
        await ReplaceFixtureEggWithNetAsync(dataSource, fixture);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var identity = PetCommandOperationIdentity.ServerSessionLifecycle(
            Guid.NewGuid(),
            correlation.ConnectionId);
        var envelope = PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateServerSessionLifecycle(
                new CommandSubject(
                    fixture.AccountId,
                    fixture.CharacterId),
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    identity,
                    fixture.EggSlot,
                    Capture: new PetCaptureIntent(
                        40_073,
                        Guid.NewGuid(),
                        1,
                        1,
                        10_150,
                        difficulty))));

        var result = await executor.ExecuteAsync(envelope);
        var receipt = result.Receipt ?? throw new InvalidDataException(
            "A committed capture returned no durable receipt.");
        Check.True(
            result.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            receipt.Status == PetDurableReceiptStatus.PetCaptured &&
            await ReadCapturedQualityAsync(
                dataSource,
                fixture.CharacterId,
                fixture.EggSlot) == (short)expectedAptitude,
            $"roll 500 selects {expectedAptitude} for {difficulty}");
        return new(
            fixture.CharacterId,
            fixture.EggSlot,
            envelope,
            receipt);
    }

    private static async Task ReplaceFixtureEggWithNetAsync(
        NpgsqlDataSource dataSource,
        PetFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_items
            SET prop_id = 10084,
                item_quality = 1,
                item_grade = 1,
                bound = 0,
                stack = 1,
                updated_at = transaction_timestamp()
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @bagSlot;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "bagSlot",
            checked((short)fixture.EggSlot));
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "capture fixture replaces its egg with one net");
    }

    private static async Task<short> ReadCapturedQualityAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        int bagSlot)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT item_quality
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @bagSlot
              AND prop_id = 10150
              AND stack = 1;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("bagSlot", checked((short)bagSlot));
        return await command.ExecuteScalarAsync() is short quality
            ? quality
            : throw new InvalidDataException(
                "The captured Rock Elf egg is missing.");
    }

    private static async Task AssertStoredCaptureWeightsAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT difficulty,
                   count(*)::integer,
                   sum(weight_basis_points)::integer,
                   string_agg(
                       aptitude::text || ':' || weight_basis_points::text,
                       ',' ORDER BY aptitude)
            FROM public.medusa_pet_capture_rarity_weights
            WHERE egg_item_id = 10150
            GROUP BY difficulty
            ORDER BY difficulty;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "Advanced capture rarity exists");
        Check.True(
            reader.GetInt16(0) == 2 &&
            reader.GetInt32(1) == 11 &&
            reader.GetInt32(2) == 10_000 &&
            reader.GetString(3) ==
                "1:2000,2:1800,3:1600,4:1400,5:1100,7:800," +
                "8:500,9:400,10:200,12:150,14:50",
            "Advanced weights match the approved database distribution");
        Check.True(await reader.ReadAsync(),
            "Mythic capture rarity exists");
        Check.True(
            reader.GetInt16(0) == 3 &&
            reader.GetInt32(1) == 11 &&
            reader.GetInt32(2) == 10_000 &&
            reader.GetString(3) ==
                "1:400,2:600,3:800,4:1100,5:1300,7:1400," +
                "8:1400,9:1200,10:900,12:600,14:300" &&
            !await reader.ReadAsync(),
            "Mythic weights match and Normal has no distribution");
    }

    private static async Task AssertWeightTotalGuardAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            """
            UPDATE public.medusa_pet_capture_rarity_weights
            SET weight_basis_points = weight_basis_points + 1
            WHERE difficulty = 2
              AND egg_item_id = 10150
              AND aptitude = 1;
            """,
            connection,
            transaction))
        {
            Check.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                "invalid rarity edit reaches deferred validation");
        }

        try
        {
            await transaction.CommitAsync();
            throw new InvalidOperationException(
                "An invalid capture rarity total was committed.");
        }
        catch (PostgresException exception)
            when (exception.SqlState == "P0001")
        {
            // Expected from the deferred distribution-total trigger.
        }
    }

    private sealed class FixedPetCaptureRarityRollSource(int roll) :
        IPetCaptureRarityRollSource
    {
        public int CallCount { get; private set; }

        public int NextRoll()
        {
            CallCount++;
            return roll;
        }
    }

    private sealed class ThrowingPetCaptureRarityRollSource :
        IPetCaptureRarityRollSource
    {
        public int NextRoll() => throw new InvalidOperationException(
            "A duplicate pet capture must never reroll rarity.");
    }

    private sealed record CaptureCommit(
        int CharacterId,
        int BagSlot,
        CommandEnvelope<BagItemActivationCommand> Envelope,
        PetDurableReceipt Receipt);
}
