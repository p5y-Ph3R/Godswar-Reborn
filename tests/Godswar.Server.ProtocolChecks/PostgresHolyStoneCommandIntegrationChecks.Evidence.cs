using System.Globalization;
using System.Text;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertStoredEvidenceBindingAsync(
        string connectionString)
    {
        await AssertWrongCharacterReceiptRejectedAsync(connectionString);
        await AssertInvalidOperationIsNotMaskedAsync(connectionString);
    }

    private static async Task AssertWrongCharacterReceiptRejectedAsync(
        string connectionString)
    {
        var requested = await CreateFixtureAsync(
            connectionString,
            "bindrq",
            target: Weapon(2));
        var other = await CreateFixtureAsync(
            connectionString,
            "bindot",
            target: Weapon(2));
        var clientOperationId = Guid.NewGuid();
        var operation = HolyStoneCommandOperation.Drill;
        var operationId = Convert.FromHexString(
            HolyStoneCommandEnvelope.CreateOperationId(
                requested.Subject,
                operation,
                clientOperationId));
        var requestHash = new byte[32];
        Random.Shared.NextBytes(requestHash);
        var principalKey = requested.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey = HolyStonePersistenceCodec.AggregateKey(
            requested.CharacterId);
        var commandFamily =
            HolyStonePersistenceCodec.CommandFamilyCode(operation);

        await using (var connection =
                     new NpgsqlConnection(connectionString))
        {
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
                    @outcomeCode,
                    '{}'::jsonb,
                    @retentionPolicy
                )
                RETURNING id;
                """,
                connection,
                transaction))
            {
                AddStoredEvidenceIdentity(
                    audit,
                    principalKey,
                    aggregateKey,
                    commandFamily,
                    operationId,
                    requestHash);
                audit.Parameters.AddWithValue(
                    "outcomeCode",
                    HolyStonePersistenceCodec.ResultCode(
                        HolyStoneCommandResultStatus.MaximumSockets));
                audit.Parameters.AddWithValue(
                    "retentionPolicy",
                    HolyStonePersistenceCodec.RetentionPolicy);
                auditId = Convert.ToInt64(
                    await audit.ExecuteScalarAsync() ??
                    throw new InvalidDataException(
                        "The corrupt-evidence fixture has no audit."));
            }

            var receipt = new HolyStoneExecutionReceipt(
                other.CharacterId,
                operation,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneCommandResultStatus.MaximumSockets,
                HolyStoneNativeResults.MaximumSocketsSubId,
                requested.TargetLocation,
                requested.TargetSlot,
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                requested.TargetItemId,
                requested.TargetState,
                requested.TargetState,
                requested.TargetState,
                HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                stoneItemInstanceId: null,
                "[]",
                "[]",
                "[]",
                outputKitBagSlot: -1,
                outputItemInstanceId: null,
                outputBeforeCompactItemState: null,
                outputAfterCompactItemState: null,
                goldSpent: 0,
                goldBefore: 1000,
                goldAfter: 1000,
                walletRevision: 0,
                inventoryRevision: 0,
                auditId.ToString(CultureInfo.InvariantCulture),
                outboxEventId: null);
            var payload = HolyStonePersistenceCodec.Encode(receipt);
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
                AddStoredEvidenceIdentity(
                    inbox,
                    principalKey,
                    aggregateKey,
                    commandFamily,
                    operationId,
                    requestHash);
                inbox.Parameters.AddWithValue(
                    "contractVersion",
                    HolyStonePersistenceCodec.ContractVersion);
                inbox.Parameters.AddWithValue(
                    "resultCode",
                    HolyStonePersistenceCodec.ResultCode(
                        receipt.Status));
                inbox.Parameters.Add(
                    "resultPayload",
                    NpgsqlDbType.Jsonb).Value =
                    Encoding.UTF8.GetString(payload);
                inbox.Parameters.Add(
                    "resultHash",
                    NpgsqlDbType.Bytea).Value =
                    HolyStonePersistenceCodec.Hash(payload);
                inbox.Parameters.AddWithValue("auditId", auditId);
                inbox.Parameters.AddWithValue(
                    "retentionPolicy",
                    HolyStonePersistenceCodec.RetentionPolicy);
                Check.Equal(
                    1,
                    await inbox.ExecuteNonQueryAsync(),
                    "wrong-character fixture stores coherent evidence");
            }
            await transaction.CommitAsync();
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        try
        {
            await CreateExecutor(dataSource).TryReplayAsync(
                requested.Subject,
                PlayerOwnershipTestFences.ForCharacter(
                    requested.Subject.CharacterId),
                operation,
                clientOperationId);
        }
        catch (InvalidDataException exception)
        {
            Check.True(
                exception.Message.Contains(
                    "character identity",
                    StringComparison.Ordinal),
                "wrong-character receipt fails at subject binding");
            var state = await ReadStateAsync(
                connectionString,
                requested,
                operation);
            Check.True(
                state.AuditCount == 1 &&
                state.InboxCount == 1 &&
                state.DuplicateCount == 0 &&
                state.InventoryRevision == 0 &&
                state.WalletRevision == 0 &&
                state.WalletReconciled &&
                state.InventoryReconciled,
                "corrupt replay cannot mutate evidence or player state");
            return;
        }

        throw new InvalidOperationException(
            "A coherent wrong-character Holy Stone receipt was replayed.");
    }

    private static async Task AssertInvalidOperationIsNotMaskedAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "badop",
            target: Weapon(1));
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(dataSource).TryReplayAsync(
            fixture.Subject,
            PlayerOwnershipTestFences.ForCharacter(
                fixture.Subject.CharacterId),
            (HolyStoneCommandOperation)byte.MaxValue,
            Guid.NewGuid());
        Check.Equal(
            (int)HolyStoneExecutionDisposition.InvalidIntent,
            (int)result.Disposition,
            "invalid operation is not masked by metrics recording");
    }

    private static void AddStoredEvidenceIdentity(
        NpgsqlCommand command,
        string principalKey,
        string aggregateKey,
        string commandFamily,
        byte[] operationId,
        byte[] requestHash)
    {
        command.Parameters.AddWithValue(
            "principalType",
            HolyStonePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            HolyStonePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            commandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
    }
}
