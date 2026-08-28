using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolySuitCommandIntegrationChecks
{
    private static async Task AssertAutomaticMaximumAsync(
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

        var first = await ExecuteAsync(
            executor,
            fixture,
            connection,
            operationId,
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 0,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: 0);
        var receipt = Require(
            first,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.ExperienceStored,
            "automatic Store Maximum");
        Check.True(
            receipt.RequestedExperience == 0 &&
            receipt.CharacterExperienceBefore -
                receipt.CharacterExperienceAfter == 100_000_000 &&
            receipt.DailyStoredExperienceAfter -
                receipt.DailyStoredExperienceBefore == 100_000_000 &&
            CompactItemEntry.Parse(
                receipt.Mutations.Single().AfterCompactItemState).Exp ==
                100_000_000,
            "Store Maximum resolves the Box IV 100m capacity");

        var duplicate = await ExecuteAsync(
            executor,
            fixture,
            connection,
            operationId,
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 0,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: 0);
        var replay = Require(
            duplicate,
            HolySuitExecutionDisposition.Duplicate,
            HolySuitCommandResultStatus.ExperienceStored,
            "automatic Store Maximum replay");
        Check.True(
            replay.RequestedExperience == 0 &&
            replay.CharacterExperienceAfter ==
                receipt.CharacterExperienceAfter,
            "automatic replay preserves intent and resolved result");

        var evidence = await ReadAutomaticStoreEvidenceAsync(
            connectionString,
            fixture);
        Check.True(
            evidence.CharacterExperience == 3_900_000_000 &&
            evidence.BoxExperience == 100_000_000 &&
            evidence.DailyStoredExperience == 100_000_000,
            "automatic Store Maximum commits exact EXP and box deltas once");
        Check.True(
            evidence.ProgressionRevision == 1 &&
            evidence.InventoryRevision == 1 &&
            evidence.OutboxCount == 1 &&
            evidence.InboxCount == 1 &&
            evidence.AuditCount == 1 &&
            evidence.InventoryLedgerCount == 1 &&
            evidence.DuplicateCount == 1,
            "automatic duplicate produces no second mutation or event");
        Check.True(
            evidence.AuditRequestedExperience == 0 &&
            evidence.AuditAppliedExperience == 100_000_000 &&
            evidence.AuditAutomaticMaximum,
            "audit separates automatic intent from applied EXP");

        await AssertAutomaticMaximumAtDailyBoundaryAsync(
            connectionString,
            itemContent);
    }

    private static async Task AssertAutomaticMaximumAtDailyBoundaryAsync(
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
        var ownership = PlayerOwnershipTestFences.ForCharacter(
            fixture.CharacterId);
        var quota = await executor.ReadStoreQuotaAsync(
            fixture.Subject,
            ownership);
        await SetDailyStoredExperienceAsync(
            connectionString,
            fixture,
            quota.UsageDay,
            1_950_000_000);

        var filled = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 0,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: 0);
        var receipt = Require(
            filled,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.ExperienceStored,
            "automatic Store Maximum fills fixed daily remainder");
        Check.True(
            receipt.CharacterExperienceBefore -
                receipt.CharacterExperienceAfter == 50_000_000 &&
            receipt.DailyStoredExperienceAfter == 2_000_000_000,
            "automatic Store Maximum stops exactly at the 2b daily cap");

        var rejected = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 5,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: 0);
        Require(
            rejected,
            HolySuitExecutionDisposition.TerminalRejected,
            HolySuitCommandResultStatus.DailyStoreLimitExceeded,
            "fresh automatic request rejects after fixed cap is exhausted");
        var finalQuota = await executor.ReadStoreQuotaAsync(
            fixture.Subject,
            ownership);
        Check.True(
            finalQuota.StoredExperienceToday == 2_000_000_000 &&
            finalQuota.DailyExperienceCredit == 2_000_000_000,
            "daily quota projection remains at the exact fixed boundary");
    }

    private static async Task<AutomaticStoreEvidence>
        ReadAutomaticStoreEvidenceAsync(
        string connectionString,
        Fixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = new NpgsqlCommand(
            """
            SELECT cb.fighter_job_exp,
                   cb.progression_reward_revision,
                   cb.inventory_revision,
                   hs.stored_exp,
                   ci.item_exp,
                   (SELECT count(*) FROM outbox_events o
                    JOIN command_inbox i ON i.id=o.command_inbox_id
                    WHERE i.principal_key=@principal
                      AND i.command_family='holy_suit_store_experience'),
                   (SELECT count(*) FROM command_inbox
                    WHERE principal_key=@principal
                      AND command_family='holy_suit_store_experience'),
                   (SELECT count(*) FROM command_audit
                    WHERE principal_key=@principal
                      AND command_family='holy_suit_store_experience'),
                   (SELECT count(*) FROM character_inventory_ledger
                    WHERE character_id=@characterId
                      AND reason_code='holy_suit_store_experience'),
                   (SELECT coalesce(sum(duplicate_count),0)
                    FROM command_inbox
                    WHERE principal_key=@principal
                      AND command_family='holy_suit_store_experience'),
                   (audit.detail_payload->>'requestedExperience')::bigint,
                   (audit.detail_payload->>'appliedExperience')::bigint,
                   (audit.detail_payload->>'automaticStoreMaximum')::boolean
            FROM character_base cb
            JOIN holy_suit_daily_exp_storage hs
              ON hs.account_id=cb.account_id
             AND hs.realm_id=cb.server_id
             AND hs.usage_day=
                 (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Singapore')::date
            JOIN character_items ci
              ON ci.user_id=cb.id AND ci.item_location=1
             AND ci.slot_index=0 AND ci.prop_id=9023
            JOIN LATERAL (
                SELECT detail_payload
                FROM command_audit
                WHERE principal_key=@principal
                  AND command_family='holy_suit_store_experience'
                ORDER BY id DESC
                LIMIT 1
            ) audit ON true
            WHERE cb.id=@characterId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principal",
            fixture.AccountId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "read automatic Store Maximum evidence");
        return new AutomaticStoreEvidence(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetBoolean(12));
    }

    private sealed record AutomaticStoreEvidence(
        long CharacterExperience,
        long ProgressionRevision,
        long InventoryRevision,
        long DailyStoredExperience,
        int BoxExperience,
        long OutboxCount,
        long InboxCount,
        long AuditCount,
        long InventoryLedgerCount,
        long DuplicateCount,
        long AuditRequestedExperience,
        long AuditAppliedExperience,
        bool AuditAutomaticMaximum);
}
