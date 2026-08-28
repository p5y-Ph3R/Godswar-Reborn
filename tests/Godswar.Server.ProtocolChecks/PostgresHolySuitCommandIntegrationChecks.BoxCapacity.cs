using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolySuitCommandIntegrationChecks
{
    private static async Task AssertBoxFiveCapacityAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        await AssertBoxFiveAcceptsCapacityAsync(
            connectionString,
            itemContent);
        await AssertBoxFiveRejectsCapacityOverflowAsync(
            connectionString,
            itemContent);
    }

    private static async Task AssertBoxFiveAcceptsCapacityAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        var fixture = await CreateFixtureAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var executor = new PostgresHolySuitCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            itemContent,
            TestRealmCalendar());
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var operationId = Guid.NewGuid();
        var emptyBox = Item(9024).ToCompactString();

        var result = await ExecuteAsync(
            executor,
            fixture,
            connection,
            operationId,
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 7,
            primaryState: emptyBox,
            experience: 400_000_000);
        var receipt = Require(
            result,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.ExperienceStored,
            "empty Holy Box V exact-capacity store");
        var mutation = receipt.Mutations.Single();
        var filledBox = CompactItemEntry.Parse(
            mutation.AfterCompactItemState);
        Check.True(
            receipt.CharacterExperienceBefore -
                receipt.CharacterExperienceAfter == 400_000_000 &&
            receipt.DailyStoredExperienceAfter -
                receipt.DailyStoredExperienceBefore == 400_000_000 &&
            filledBox.Id == 9024 &&
            filledBox.Exp == 400_000_000 &&
            filledBox.Bound == 1,
            "Box V stores and conserves its complete 400m capacity");

        var duplicate = await ExecuteAsync(
            executor,
            fixture,
            connection,
            operationId,
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 7,
            primaryState: emptyBox,
            experience: 400_000_000);
        Require(
            duplicate,
            HolySuitExecutionDisposition.Duplicate,
            HolySuitCommandResultStatus.ExperienceStored,
            "exact-capacity Box V replay");

        var evidence = await ReadBoxFiveEvidenceAsync(
            connectionString,
            fixture);
        Check.True(
            evidence.CharacterExperience == 3_600_000_000 &&
            evidence.BoxExperience == 400_000_000 &&
            evidence.DailyStoredExperience == 400_000_000 &&
            evidence.InventoryLedgerCount == 1 &&
            evidence.OutboxCount == 1 &&
            evidence.DuplicateCount == 1,
            "Box V exact-capacity operation mutates authoritative value once");
    }

    private static async Task AssertBoxFiveRejectsCapacityOverflowAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        var fixture = await CreateFixtureAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var executor = new PostgresHolySuitCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            itemContent,
            TestRealmCalendar());
        var result = await ExecuteAsync(
            executor,
            fixture,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            Guid.NewGuid(),
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 7,
            primaryState: Item(9024).ToCompactString(),
            experience: 450_000_000);
        var receipt = Require(
            result,
            HolySuitExecutionDisposition.TerminalRejected,
            HolySuitCommandResultStatus.HolyBoxFull,
            "empty Holy Box V 450m capacity overflow");
        Check.True(
            receipt.NativeResultSubId == HolySuitNativeResults.HolyBoxFullSubId &&
            receipt.Mutations.Length == 0 &&
            receipt.CharacterExperienceBefore ==
                receipt.CharacterExperienceAfter &&
            receipt.DailyStoredExperienceBefore ==
                receipt.DailyStoredExperienceAfter,
            "Box V overflow returns native capacity result without value mutation");

        var evidence = await ReadBoxFiveEvidenceAsync(
            connectionString,
            fixture);
        Check.True(
            evidence.CharacterExperience == 4_000_000_000 &&
            evidence.BoxExperience == 0 &&
            evidence.DailyStoredExperience == 0 &&
            evidence.InventoryLedgerCount == 0 &&
            evidence.OutboxCount == 0,
            "rejected Box V overflow leaves every authoritative value unchanged");
    }

    private static async Task<BoxFiveEvidence> ReadBoxFiveEvidenceAsync(
        string connectionString,
        Fixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = new NpgsqlCommand(
            """
            SELECT cb.fighter_job_exp,
                   ci.item_exp,
                   COALESCE(usage.stored_exp, 0),
                   (SELECT count(*)
                    FROM character_inventory_ledger
                    WHERE character_id=@characterId
                      AND reason_code='holy_suit_store_experience'),
                   (SELECT count(*)
                    FROM outbox_events event
                    JOIN command_inbox inbox
                      ON inbox.id=event.command_inbox_id
                    WHERE inbox.principal_key=@principal
                      AND inbox.command_family=
                          'holy_suit_store_experience'),
                   (SELECT COALESCE(sum(duplicate_count), 0)
                    FROM command_inbox
                    WHERE principal_key=@principal
                      AND command_family='holy_suit_store_experience')
            FROM character_base cb
            JOIN character_items ci
              ON ci.user_id=cb.id
             AND ci.item_location=1
             AND ci.slot_index=7
             AND ci.prop_id=9024
            LEFT JOIN holy_suit_daily_exp_storage usage
              ON usage.account_id=cb.account_id
             AND usage.realm_id=cb.server_id
             AND usage.usage_day=
                 (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Singapore')::date
            WHERE cb.id=@characterId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principal",
            fixture.AccountId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "read Holy Box V evidence");
        return new BoxFiveEvidence(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    private sealed record BoxFiveEvidence(
        long CharacterExperience,
        int BoxExperience,
        long DailyStoredExperience,
        long InventoryLedgerCount,
        long OutboxCount,
        long DuplicateCount);
}
