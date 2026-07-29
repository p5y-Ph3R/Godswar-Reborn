using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;
using Godswar.Server.Infrastructure.Talents;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresTalentInboxOutboxIntegrationChecks
{
    private static async Task<PersistedCommand> ReadPersistedCommandAsync(
        string connectionString,
        TalentFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                audit.id,
                audit.operation_id,
                audit.request_hash,
                audit.outcome_code,
                inbox.id,
                inbox.operation_id,
                inbox.request_hash,
                inbox.result_contract_version,
                inbox.result_code,
                inbox.result_payload::text,
                inbox.result_hash,
                inbox.audit_id,
                inbox.duplicate_count,
                inbox.request_conflict_count,
                outbox.id,
                outbox.event_id,
                outbox.command_inbox_id,
                outbox.consumer_key,
                outbox.aggregate_type,
                outbox.aggregate_key,
                outbox.aggregate_version,
                outbox.event_type,
                outbox.contract_version,
                outbox.ordering_policy,
                outbox.payload::text,
                outbox.attempt_count,
                talent.outbox_revision
            FROM public.command_audit audit
            JOIN public.command_inbox inbox
              ON inbox.audit_id = audit.id
            JOIN public.outbox_events outbox
              ON outbox.command_inbox_id = inbox.id
            JOIN public.character_talents talent
              ON talent.user_id = @characterId
             AND talent.talent_id = @talentId
            WHERE audit.principal_type = @principalType
              AND audit.principal_key = @principalKey
              AND audit.aggregate_type = @aggregateType
              AND audit.aggregate_key = @aggregateKey
              AND audit.command_family = @commandFamily;
            """,
            connection);
        AddFixtureParameters(command, fixture);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The committed command rows were not found.");
        }

        var persisted = new PersistedCommand(
            reader.GetInt64(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetFieldValue<byte[]>(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetFieldValue<byte[]>(5),
            reader.GetFieldValue<byte[]>(6),
            reader.GetInt16(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetFieldValue<byte[]>(10),
            reader.GetInt64(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt64(14),
            reader.GetGuid(15),
            reader.GetInt64(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetInt64(20),
            reader.GetString(21),
            reader.GetInt16(22),
            reader.GetString(23),
            reader.GetString(24),
            reader.GetInt16(25),
            reader.GetInt64(26));
        Check.True(
            !await reader.ReadAsync(),
            "one operation has exactly one joined durable command row");
        return persisted;
    }

    private static void AssertPersistedCommand(
        PersistedCommand persisted,
        TalentFixture fixture,
        CommandEnvelope<TalentUpgradeCommand> envelope,
        TalentUpgradeExecutionReceipt receipt,
        int expectedDuplicateCount)
    {
        var operationId =
            Convert.FromHexString(envelope.OperationId);
        var requestHash =
            Convert.FromHexString(envelope.RequestHash);
        Check.True(
            operationId.SequenceEqual(persisted.AuditOperationId) &&
            operationId.SequenceEqual(persisted.InboxOperationId),
            "audit and inbox retain the canonical operation identity");
        Check.True(
            requestHash.SequenceEqual(persisted.AuditRequestHash) &&
            requestHash.SequenceEqual(persisted.InboxRequestHash),
            "audit and inbox retain the canonical request hash");

        Check.Equal(
            TalentUpgradePersistenceCodec.ResultCode,
            persisted.AuditOutcome,
            "audit outcome code");
        Check.Equal(
            persisted.AuditId,
            persisted.InboxAuditId,
            "inbox references its immutable audit");
        Check.Equal(
            persisted.InboxId,
            persisted.OutboxInboxId,
            "outbox references its immutable inbox");
        Check.Equal(
            expectedDuplicateCount,
            persisted.DuplicateCount,
            "durable duplicate counter");
        Check.Equal(
            0,
            persisted.RequestConflictCount,
            "durable request-conflict counter");

        Check.Equal(
            TalentUpgradePersistenceCodec.ContractVersion,
            persisted.ResultContractVersion,
            "stored result contract version");
        Check.Equal(
            TalentUpgradePersistenceCodec.ResultCode,
            persisted.ResultCode,
            "stored result code");
        var storedReceipt =
            TalentUpgradePersistenceCodec.DecodeAndVerify(
                persisted.ResultPayload,
                persisted.ResultHash);
        AssertReceiptsEqual(
            receipt,
            storedReceipt,
            "stored result and hash recover the canonical receipt");

        Check.Equal(
            TalentUpgradePersistenceCodec.ConsumerKey,
            persisted.ConsumerKey,
            "outbox consumer key");
        Check.Equal(
            TalentUpgradePersistenceCodec.AggregateType,
            persisted.AggregateType,
            "outbox aggregate type");
        Check.Equal(
            AggregateKey(fixture),
            persisted.AggregateKey,
            "outbox aggregate key");
        Check.Equal(
            receipt.AggregateRevision,
            persisted.AggregateRevision,
            "outbox aggregate revision");
        Check.Equal(
            TalentUpgradePersistenceCodec.EventType,
            persisted.EventType,
            "outbox event type");
        Check.Equal(
            TalentUpgradePersistenceCodec.ContractVersion,
            persisted.OutboxContractVersion,
            "outbox contract version");
        Check.Equal(
            TalentUpgradePersistenceCodec.OrderingPolicy,
            persisted.OrderingPolicy,
            "outbox ordering policy");
        Check.Equal(
            receipt.OutboxEventId,
            persisted.EventId,
            "receipt references the exact outbox event");
        Check.Equal(
            receipt.AggregateRevision,
            persisted.TalentOutboxRevision,
            "talent outbox revision matches its event");
        Check.Equal(
            (short)0,
            persisted.AttemptCount,
            "new outbox event has not been dispatched");

        var outboxReceipt =
            TalentUpgradePersistenceCodec.Decode(
                System.Text.Encoding.UTF8.GetBytes(
                    persisted.OutboxPayload));
        AssertReceiptsEqual(
            receipt,
            outboxReceipt,
            "outbox payload carries the canonical receipt");
    }

    private static void AssertCommittedState(
        DurableState state,
        int expectedPoints,
        int expectedRank,
        long expectedRevision,
        int expectedDuplicateCount,
        string description)
    {
        Check.True(
            state.TalentPoints == expectedPoints &&
            state.Rank == expectedRank &&
            state.OutboxRevision == expectedRevision &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == expectedDuplicateCount &&
            state.RequestConflictCount == 0,
            description);
    }

    private static void AssertNoCommandRows(
        DurableState state,
        int expectedPoints,
        int expectedRank,
        long expectedRevision,
        string description)
    {
        Check.True(
            state.TalentPoints == expectedPoints &&
            state.Rank == expectedRank &&
            state.OutboxRevision == expectedRevision &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.OutboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.RequestConflictCount == 0,
            description);
    }

    private sealed record PersistedCommand(
        long AuditId,
        byte[] AuditOperationId,
        byte[] AuditRequestHash,
        string AuditOutcome,
        long InboxId,
        byte[] InboxOperationId,
        byte[] InboxRequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long InboxAuditId,
        int DuplicateCount,
        int RequestConflictCount,
        long OutboxId,
        Guid EventId,
        long OutboxInboxId,
        string ConsumerKey,
        string AggregateType,
        string AggregateKey,
        long AggregateRevision,
        string EventType,
        short OutboxContractVersion,
        string OrderingPolicy,
        string OutboxPayload,
        short AttemptCount,
        long TalentOutboxRevision);
}
