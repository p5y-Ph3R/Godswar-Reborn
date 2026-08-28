using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolySuitCommandIntegrationChecks
{
    private static async Task AssertAdversarialAuthorityAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        await AssertClientOnlyBoxCannotCreateValueAsync(
            connectionString,
            itemContent);
        await AssertConcurrentBoxRequestsMutateOnceAsync(
            connectionString,
            itemContent);
        await AssertConcurrentPrismRequestsCannotDoubleSpendAsync(
            connectionString,
            itemContent);
    }

    private static async Task AssertClientOnlyBoxCannotCreateValueAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        var fixture = await CreateFixtureAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource, itemContent);
        var connection = CreateSecureConnection();

        var fake = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 20,
            primaryState: Item(9024).ToCompactString(),
            experience: 400_000_000);
        var fakeReceipt = Require(
            fake,
            HolySuitExecutionDisposition.TerminalRejected,
            HolySuitCommandResultStatus.PrimaryItemMissing,
            "client-only Holy Box V");
        Check.Equal(
            0,
            fakeReceipt.Mutations.Length,
            "client-only box produces no authoritative mutation");

        var edited = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 7,
            primaryState: Item(
                9024,
                bound: 1,
                exp: 399_000_000).ToCompactString(),
            experience: 1_000_000);
        var editedReceipt = Require(
            edited,
            HolySuitExecutionDisposition.TerminalRejected,
            HolySuitCommandResultStatus.StalePrimaryItem,
            "client-edited Holy Box V state");
        Check.Equal(
            0,
            editedReceipt.Mutations.Length,
            "client-edited box produces no authoritative mutation");

        var evidence = await ReadBoxFiveEvidenceAsync(
            connectionString,
            fixture);
        Check.True(
            evidence.CharacterExperience == 4_000_000_000 &&
            evidence.BoxExperience == 0 &&
            evidence.DailyStoredExperience == 0 &&
            evidence.InventoryLedgerCount == 0 &&
            evidence.OutboxCount == 0,
            "fake and edited client boxes cannot create or move value");
    }

    private static async Task AssertConcurrentBoxRequestsMutateOnceAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        var fixture = await CreateFixtureAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource, itemContent);
        var connection = CreateSecureConnection();
        var expected = Item(9024).ToCompactString();

        var results = await Task.WhenAll(
            ExecuteAsync(
                executor,
                fixture,
                connection,
                Guid.NewGuid(),
                HolySuitCommandOperation.StoreExperience,
                primarySlot: 7,
                primaryState: expected,
                experience: 400_000_000),
            ExecuteAsync(
                executor,
                fixture,
                connection,
                Guid.NewGuid(),
                HolySuitCommandOperation.StoreExperience,
                primarySlot: 7,
                primaryState: expected,
                experience: 400_000_000));

        Check.Equal(
            1,
            results.Count(static result =>
                result.Disposition ==
                    HolySuitExecutionDisposition.Committed &&
                result.Receipt?.Status ==
                    HolySuitCommandResultStatus.ExperienceStored),
            "only one fresh operation can fill the same Holy Box");
        Check.Equal(
            1,
            results.Count(static result =>
                result.Disposition ==
                    HolySuitExecutionDisposition.TerminalRejected &&
                result.Receipt?.Status ==
                    HolySuitCommandResultStatus.StalePrimaryItem),
            "racing request observes the changed authoritative box");

        var evidence = await ReadBoxFiveEvidenceAsync(
            connectionString,
            fixture);
        Check.True(
            evidence.CharacterExperience == 3_600_000_000 &&
            evidence.BoxExperience == 400_000_000 &&
            evidence.DailyStoredExperience == 400_000_000 &&
            evidence.InventoryLedgerCount == 1 &&
            evidence.OutboxCount == 1,
            "concurrent Box V requests debit and fill exactly once");
    }

    private static async Task
        AssertConcurrentPrismRequestsCannotDoubleSpendAsync(
            string connectionString,
            GameplayItemContent itemContent)
    {
        var fixture = await CreateFixtureAsync(connectionString);
        await SetCharacterExperienceAsync(
            connectionString,
            fixture,
            100_000_000);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource, itemContent);
        var connection = CreateSecureConnection();

        var results = await Task.WhenAll(
            ExecuteAsync(
                executor,
                fixture,
                connection,
                Guid.NewGuid(),
                HolySuitCommandOperation.TransformExperience,
                prisms: 1),
            ExecuteAsync(
                executor,
                fixture,
                connection,
                Guid.NewGuid(),
                HolySuitCommandOperation.TransformExperience,
                prisms: 1));

        Check.Equal(
            1,
            results.Count(static result =>
                result.Disposition ==
                    HolySuitExecutionDisposition.Committed &&
                result.Receipt?.Status ==
                    HolySuitCommandResultStatus.ExperienceTransformed),
            "only one Prism operation can spend the final 100m EXP");
        Check.Equal(
            1,
            results.Count(static result =>
                result.Disposition ==
                    HolySuitExecutionDisposition.TerminalRejected &&
                result.Receipt?.Status ==
                    HolySuitCommandResultStatus
                        .InsufficientCharacterExperience),
            "racing Prism request observes the authoritative EXP debit");

        var evidence = await ReadPrismRaceEvidenceAsync(
            connectionString,
            fixture);
        Check.True(
            evidence.CharacterExperience == 0 &&
            evidence.PrismStack == 21 &&
            evidence.InventoryLedgerCount == 1 &&
            evidence.OutboxCount == 1,
            "concurrent Prism requests spend and create exactly once");
    }

    private static PostgresHolySuitCommandExecutor CreateExecutor(
        NpgsqlDataSource dataSource,
        GameplayItemContent itemContent) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            itemContent,
            TestRealmCalendar());

    private static CommandConnectionCorrelation CreateSecureConnection() =>
        new(Guid.NewGuid(), CommandTransportKind.SecureTlsLegacy);

    private static async Task SetCharacterExperienceAsync(
        string connectionString,
        Fixture fixture,
        long experience)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = new NpgsqlCommand(
            """
            UPDATE character_base
            SET fighter_job_exp=@experience
            WHERE id=@characterId AND account_id=@accountId;
            """,
            connection);
        command.Parameters.AddWithValue("experience", experience);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "set Prism race EXP boundary");
    }

    private static async Task<PrismRaceEvidence> ReadPrismRaceEvidenceAsync(
        string connectionString,
        Fixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = new NpgsqlCommand(
            """
            SELECT cb.fighter_job_exp,
                   ci.stack,
                   (SELECT count(*)
                    FROM character_inventory_ledger
                    WHERE character_id=@characterId
                      AND reason_code='holy_suit_transform_experience'),
                   (SELECT count(*)
                    FROM outbox_events event
                    JOIN command_inbox inbox
                      ON inbox.id=event.command_inbox_id
                    WHERE inbox.principal_key=@principal
                      AND inbox.command_family=
                          'holy_suit_transform_experience')
            FROM character_base cb
            JOIN character_items ci
              ON ci.user_id=cb.id
             AND ci.item_location=1
             AND ci.slot_index=6
             AND ci.prop_id=9025
            WHERE cb.id=@characterId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principal",
            fixture.AccountId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "read Prism race evidence");
        return new PrismRaceEvidence(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private sealed record PrismRaceEvidence(
        long CharacterExperience,
        short PrismStack,
        long InventoryLedgerCount,
        long OutboxCount);
}
