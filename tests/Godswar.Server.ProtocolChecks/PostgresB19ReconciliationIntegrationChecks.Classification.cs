using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Zodiac;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;
using System.Text;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresB19ReconciliationIntegrationChecks
{
    private static async Task SeedExpectedPurgedCharacterAsync(
        string connectionString,
        NpgsqlDataSource dataSource)
    {
        GameAccount account;
        await using (var store =
                     new PostgresGameStore(connectionString))
        {
            account = await store.LoginOrCreateAccountAsync(
                "b19_expected_purge",
                string.Empty);
        }

        var executor = new PostgresCharacterLifecycleCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions());
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var created = await executor.ExecuteAsync(
            CharacterCreateCommandEnvelope.Create(
                account.Id,
                correlation,
                DateTimeOffset.UtcNow,
                new CharacterCreateCommand(
                    Guid.NewGuid(),
                    0,
                    "B19Purge",
                    1,
                    GameDefaults.SpartaCamp,
                    0,
                    1,
                    0,
                    0,
                    1)));
        Check.True(
            created is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Created
            },
            "B19 purge fixture creates through the durable command path");

        var receipt = created.Receipt!;
        var deleted = await executor.ExecuteAsync(
            CharacterDeleteCommandEnvelope.Create(
                account.Id,
                correlation,
                DateTimeOffset.UtcNow,
                new CharacterDeleteCommand(
                    Guid.NewGuid(),
                    0,
                    receipt.CharacterName,
                    receipt.CharacterId,
                    receipt.LifecycleVersion)));
        Check.True(
            deleted is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Deleted
            },
            "B19 purge fixture deletes through the durable command path");

        await using (var eligible = dataSource.CreateCommand("""
            UPDATE public.character_base
            SET "Register_time" =
                    transaction_timestamp() - interval '40 days',
                deleted_at =
                    transaction_timestamp() - interval '3 days',
                restore_until =
                    transaction_timestamp() - interval '2 days',
                purge_after =
                    transaction_timestamp() - interval '1 day'
            WHERE account_id = @account_id
              AND id = @character_id
              AND lifecycle_state = 'deleted';
            """))
        {
            eligible.Parameters.AddWithValue(
                "account_id",
                account.Id);
            eligible.Parameters.AddWithValue(
                "character_id",
                receipt.CharacterId);
            Check.Equal(
                1,
                await eligible.ExecuteNonQueryAsync(),
                "one B19 tombstone becomes purge eligible");
        }

        var purged = await executor.ExecuteAsync(
            CharacterPurgeCommandEnvelope.Create(
                account.Id,
                correlation,
                DateTimeOffset.UtcNow,
                new CharacterPurgeCommand(
                    Guid.NewGuid(),
                    0,
                    receipt.CharacterId,
                    deleted.Receipt!.LifecycleVersion)));
        Check.True(
            purged is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Purged
            },
            "B19 purge fixture commits complete purge evidence");
    }

    private static async Task SeedBenignPendingOutboxAsync(
        NpgsqlDataSource dataSource,
        EconomyFixture fixture)
    {
        long inboxId;
        await using (var inbox = dataSource.CreateCommand("""
            SELECT id
            FROM public.command_inbox
            WHERE principal_type = 'account'
            ORDER BY id
            LIMIT 1;
            """))
        {
            inboxId = Convert.ToInt64(
                await inbox.ExecuteScalarAsync());
        }

        var eventId = Guid.NewGuid();
        var receipt = new ZodiacSkillGridUpgradeExecutionReceipt(
            fixture.CharacterId,
            ZodiacSkillGridUpgradeReceiptStatus.Succeeded,
            gridIndex: 1,
            previousLevel: 8,
            currentLevel: 9,
            currentZodiacLevel: 30,
            requiredZodiacLevel: 1,
            energyCost: 1,
            energyBefore: 10,
            energyRemainderBeforeX100: 0,
            energyAfter: 9,
            energyRemainderAfterX100: 0,
            talentPointCost: 1,
            talentPointsBefore: 10,
            talentPointsAfter: 9,
            selectedSkillId: 0,
            auditReference: inboxId.ToString(),
            outboxEventId: eventId);
        var payload = Encoding.UTF8.GetString(
            ZodiacSkillGridUpgradePersistenceCodec.Encode(receipt));
        await using var command = dataSource.CreateCommand("""
            INSERT INTO public.outbox_events (
                event_id,
                command_inbox_id,
                consumer_key,
                aggregate_type,
                aggregate_key,
                aggregate_version,
                event_type,
                contract_version,
                ordering_policy,
                payload,
                available_at
            )
            VALUES (
                @event_id,
                @inbox_id,
                @consumer_key,
                @aggregate_type,
                @aggregate_key,
                9,
                @event_type,
                @contract_version,
                @ordering_policy,
                @payload,
                clock_timestamp()
            );
            """);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("inbox_id", inboxId);
        command.Parameters.AddWithValue(
            "consumer_key",
            ZodiacSkillGridUpgradePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregate_type",
            ZodiacSkillGridUpgradePersistenceCodec.EventAggregateType);
        command.Parameters.AddWithValue(
            "aggregate_key",
            ZodiacSkillGridUpgradePersistenceCodec.EventAggregateKey(
                fixture.CharacterId,
                1));
        command.Parameters.AddWithValue(
            "event_type",
            ZodiacSkillGridUpgradePersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contract_version",
            ZodiacSkillGridUpgradePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "ordering_policy",
            ZodiacSkillGridUpgradePersistenceCodec.OrderingPolicy);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value = payload;
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "a valid pending latest-wins version jump is seeded");
    }
}
