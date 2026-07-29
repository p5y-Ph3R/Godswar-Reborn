using System.Globalization;
using System.Text;
using Godswar.Server.Application.Talents;
using Godswar.Server.Infrastructure.Talents;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresTalentInboxOutboxIntegrationChecks
{
    private static async Task AssertDurableHashConflictAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "hash",
            level: 80,
            talentPoints: 100);
        var envelope = CreateEnvelope(
            fixture,
            expectedRank: 0);
        await SeedConflictingInboxAsync(
            connectionString,
            fixture,
            envelope);

        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(source)
            .ExecuteAsync(envelope);
        Check.True(
            result.Disposition ==
                TalentUpgradeExecutionDisposition.RequestHashConflict &&
            result.Receipt is null,
            "durable same-operation/different-hash conflict is rejected");

        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.True(
            state.TalentPoints == 100 &&
            state.Rank == 0 &&
            state.OutboxRevision == 0 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.RequestConflictCount == 1,
            "hash conflict updates only durable security evidence");
    }

    private static async Task SeedConflictingInboxAsync(
        string connectionString,
        TalentFixture fixture,
        Godswar.Server.Application.Commands.CommandEnvelope<
            TalentUpgradeCommand> envelope)
    {
        // A valid legacy envelope derives operation ID and request hash from
        // the same intent, so normal traffic cannot create this mismatch.
        // Seeding it proves the PostgreSQL inbox still fails closed if durable
        // state is corrupted or written by a future incompatible producer.
        var operationId =
            Convert.FromHexString(envelope.OperationId);
        var conflictingHash =
            Convert.FromHexString(envelope.RequestHash);
        conflictingHash[0] ^= 0xFF;

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        long auditId;
        await using (var audit = new NpgsqlCommand(
            """
            INSERT INTO public.command_audit (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                outcome_code,
                detail_payload,
                retention_policy
            )
            VALUES (
                @principalType,
                @principalKey,
                @aggregateType,
                @aggregateKey,
                @commandFamily,
                @operationId,
                @requestHash,
                @resultCode,
                '{}'::jsonb,
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            AddConflictIdentityParameters(
                audit,
                fixture,
                operationId,
                conflictingHash);
            audit.Parameters.AddWithValue(
                "resultCode",
                TalentUpgradePersistenceCodec.ResultCode);
            audit.Parameters.AddWithValue(
                "retentionPolicy",
                TalentUpgradePersistenceCodec.RetentionPolicy);
            auditId = Convert.ToInt64(
                await audit.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The conflict audit returned no identity."));
        }

        var syntheticReceipt =
            new TalentUpgradeExecutionReceipt(
                fixture.CharacterId,
                TalentId,
                rank: 1,
                cost: 1,
                remainingTalentPoints: 99,
                displayValue: 4,
                aggregateRevision: 1,
                auditId.ToString(CultureInfo.InvariantCulture),
                Guid.NewGuid());
        var payload =
            TalentUpgradePersistenceCodec.Encode(syntheticReceipt);
        var resultHash =
            TalentUpgradePersistenceCodec.Hash(payload);

        await using (var inbox = new NpgsqlCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                result_contract_version,
                result_code,
                result_payload,
                result_hash,
                audit_id,
                retention_policy
            )
            VALUES (
                @principalType,
                @principalKey,
                @aggregateType,
                @aggregateKey,
                @commandFamily,
                @operationId,
                @requestHash,
                @contractVersion,
                @resultCode,
                @resultPayload,
                @resultHash,
                @auditId,
                @retentionPolicy
            );
            """,
            connection,
            transaction))
        {
            AddConflictIdentityParameters(
                inbox,
                fixture,
                operationId,
                conflictingHash);
            inbox.Parameters.AddWithValue(
                "contractVersion",
                TalentUpgradePersistenceCodec.ContractVersion);
            inbox.Parameters.AddWithValue(
                "resultCode",
                TalentUpgradePersistenceCodec.ResultCode);
            inbox.Parameters.Add(
                "resultPayload",
                NpgsqlDbType.Jsonb).Value =
                Encoding.UTF8.GetString(payload);
            inbox.Parameters.Add(
                "resultHash",
                NpgsqlDbType.Bytea).Value =
                resultHash;
            inbox.Parameters.AddWithValue("auditId", auditId);
            inbox.Parameters.AddWithValue(
                "retentionPolicy",
                TalentUpgradePersistenceCodec.RetentionPolicy);
            Check.Equal(
                1,
                await inbox.ExecuteNonQueryAsync(),
                "conflict inbox seed inserts one row");
        }

        await transaction.CommitAsync();
    }

    private static void AddConflictIdentityParameters(
        NpgsqlCommand command,
        TalentFixture fixture,
        byte[] operationId,
        byte[] requestHash)
    {
        command.Parameters.AddWithValue(
            "principalType",
            TalentUpgradePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            PrincipalKey(fixture));
        command.Parameters.AddWithValue(
            "aggregateType",
            TalentUpgradePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            AggregateKey(fixture));
        command.Parameters.AddWithValue(
            "commandFamily",
            TalentUpgradePersistenceCodec.CommandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
    }

    private static async Task AssertRetentionGuardsAsync(
        string connectionString,
        PersistedCommand persisted)
    {
        await AssertGuardRejectsAsync(
            connectionString,
            """
            UPDATE public.command_audit
            SET outcome_code = 'tampered'
            WHERE id = @id;
            """,
            persisted.AuditId,
            "audit mutation");
        await AssertGuardRejectsAsync(
            connectionString,
            """
            DELETE FROM public.command_audit
            WHERE id = @id;
            """,
            persisted.AuditId,
            "audit deletion");
        await AssertGuardRejectsAsync(
            connectionString,
            """
            UPDATE public.command_inbox
            SET result_code = 'tampered'
            WHERE id = @id;
            """,
            persisted.InboxId,
            "inbox result mutation");
        await AssertGuardRejectsAsync(
            connectionString,
            """
            DELETE FROM public.command_inbox
            WHERE id = @id;
            """,
            persisted.InboxId,
            "inbox deletion");
        await AssertGuardRejectsAsync(
            connectionString,
            """
            UPDATE public.outbox_events
            SET aggregate_version = aggregate_version + 1
            WHERE id = @id;
            """,
            persisted.OutboxId,
            "outbox version mutation");
        await AssertGuardRejectsAsync(
            connectionString,
            """
            UPDATE public.outbox_events
            SET payload = '{}'::jsonb
            WHERE id = @id;
            """,
            persisted.OutboxId,
            "outbox payload mutation");
    }

    private static async Task AssertGuardRejectsAsync(
        string connectionString,
        string sql,
        long id,
        string description)
    {
        try
        {
            await using var connection =
                new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command =
                new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", id);
            _ = await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
            when (exception.SqlState == "P0001")
        {
            return;
        }

        throw new InvalidOperationException(
            $"The permanent {description} guard did not reject the write.");
    }
}
