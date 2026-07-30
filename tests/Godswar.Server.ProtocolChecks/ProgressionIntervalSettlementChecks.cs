using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Progression;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ProgressionIntervalSettlementChecks
{
    private const string MigrationId =
        "20260731_033_progression_interval_authority";

    public static async Task RunAsync()
    {
        CheckServerEnvelopeIdentity();
        CheckIntervalAuthorityPolicy();
        await CheckPersistenceContractAsync();
        await ProgressionIntervalRetryHandoffChecks.RunAsync();
        CheckMigration();
    }

    private static void CheckServerEnvelopeIdentity()
    {
        var subject = new CommandSubject(7, 13);
        var sessionId = Guid.Parse(
            "17c55b19-a055-4aed-b709-c4817aab9e8d");
        var from = new DateTimeOffset(
            2026,
            7,
            31,
            1,
            0,
            0,
            TimeSpan.Zero);
        var until = from.AddSeconds(30);
        var first =
            ProgressionIntervalSettlementCommandEnvelope.Create(
                subject,
                sessionId,
                1,
                from,
                until,
                CommandTransportKind.LegacyTcp);
        var exactRetry =
            ProgressionIntervalSettlementCommandEnvelope.Create(
                subject,
                sessionId,
                1,
                from,
                until,
                CommandTransportKind.LegacyTcp);
        Check.Equal(
            first.OperationId,
            exactRetry.OperationId,
            "server interval operation identity is deterministic");
        Check.Equal(
            first.RequestHash,
            exactRetry.RequestHash,
            "exact server interval retry has the same request hash");
        Check.Equal(
            (int)CommandIdentityStrength.ServerOperationId,
            (int)first.IdentityStrength,
            "progression intervals use server-issued identity");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)ProgressionIntervalSettlementCommandEnvelope.Validate(first),
            "well-formed UTC interval validates");
        var subMicrosecond =
            ProgressionIntervalSettlementCommandEnvelope.Create(
                subject,
                sessionId,
                2,
                until.AddTicks(7),
                until.AddSeconds(30).AddTicks(9),
                CommandTransportKind.LegacyTcp);
        Check.True(
            subMicrosecond.Command.OnlineFromUtc.UtcDateTime.Ticks %
                10 == 0 &&
            subMicrosecond.Command.OnlineUntilUtc.UtcDateTime.Ticks %
                10 == 0,
            "interval identity is canonicalized to PostgreSQL microseconds");

        var conflictingRequest =
            ProgressionIntervalSettlementCommandEnvelope.Create(
                subject,
                sessionId,
                1,
                from,
                until.AddSeconds(1),
                CommandTransportKind.LegacyTcp);
        Check.Equal(
            first.OperationId,
            conflictingRequest.OperationId,
            "one session sequence always resolves to one operation");
        Check.True(
            !string.Equals(
                first.RequestHash,
                conflictingRequest.RequestHash,
                StringComparison.Ordinal),
            "changed interval bounds produce a request-hash conflict");

        Check.Throws<ArgumentOutOfRangeException>(
            () => ProgressionIntervalSettlementCommandEnvelope.Create(
                subject,
                sessionId,
                2,
                until,
                until +
                    ProgressionIntervalSettlementCommandEnvelope
                        .MaximumInterval +
                    TimeSpan.FromTicks(10),
                CommandTransportKind.LegacyTcp),
            "online intervals have a hard duration bound");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ProgressionIntervalSettlementCommandEnvelope.Create(
                subject,
                sessionId,
                2,
                until.ToOffset(TimeSpan.FromHours(12)),
                until.AddSeconds(30),
                CommandTransportKind.LegacyTcp),
            "online interval endpoints must be UTC");
    }

    private static void CheckIntervalAuthorityPolicy()
    {
        var sessionId = Guid.Parse(
            "17c55b19-a055-4aed-b709-c4817aab9e8d");
        var replacementId = Guid.Parse(
            "b23c3e68-4082-4fe1-ac91-cb45a2594c7b");
        var end = new DateTimeOffset(
            2026,
            7,
            31,
            2,
            0,
            0,
            TimeSpan.Zero);
        var authority = new ProgressionIntervalAuthorityState(
            sessionId,
            4,
            end,
            7);
        Check.Equal(
            (int)ProgressionIntervalConflict.None,
            (int)ProgressionIntervalSettlementPolicy.ValidateNext(
                new(
                    sessionId,
                    5,
                    end,
                    end.AddSeconds(30)),
                authority,
                end),
            "the next contiguous interval is accepted");
        Check.Equal(
            (int)ProgressionIntervalConflict.InvalidSequence,
            (int)ProgressionIntervalSettlementPolicy.ValidateNext(
                new(
                    sessionId,
                    6,
                    end,
                    end.AddSeconds(30)),
                authority,
                end),
            "reordered sequence is rejected");
        Check.Equal(
            (int)ProgressionIntervalConflict.Overlap,
            (int)ProgressionIntervalSettlementPolicy.ValidateNext(
                new(
                    sessionId,
                    5,
                    end.AddSeconds(-1),
                    end.AddSeconds(30)),
                authority,
                end),
            "same-session overlap is rejected");
        Check.Equal(
            (int)ProgressionIntervalConflict.Gap,
            (int)ProgressionIntervalSettlementPolicy.ValidateNext(
                new(
                    sessionId,
                    5,
                    end.AddSeconds(1),
                    end.AddSeconds(30)),
                authority,
                end),
            "same-session online gap is rejected");
        Check.Equal(
            (int)ProgressionIntervalConflict.StaleSession,
            (int)ProgressionIntervalSettlementPolicy.ValidateNext(
                new(
                    replacementId,
                    2,
                    end.AddMinutes(1),
                    end.AddMinutes(2)),
                authority,
                end),
            "a stale or malformed replacement session is rejected");
        Check.Equal(
            (int)ProgressionIntervalConflict.None,
            (int)ProgressionIntervalSettlementPolicy.ValidateNext(
                new(
                    replacementId,
                    1,
                    end.AddMinutes(1),
                    end.AddMinutes(2)),
                authority,
                end),
            "a new server session may skip an offline gap");
    }

    private static async Task CheckPersistenceContractAsync()
    {
        var eventId = Guid.Parse(
            "447f0b28-18d3-4860-a17f-14e587a2e6f8");
        var sessionId = Guid.Parse(
            "17c55b19-a055-4aed-b709-c4817aab9e8d");
        var from = new DateTimeOffset(
            2026,
            7,
            31,
            3,
            0,
            0,
            TimeSpan.Zero);
        var projection = new ProgressionIntervalProjection(
            sessionId,
            1,
            from.AddSeconds(30),
            1,
            123,
            45,
            new DateOnly(2026, 7, 30),
            TimeSpan.FromSeconds(30).Ticks,
            null);
        var receipt = new ProgressionIntervalSettlementReceipt(
            13,
            sessionId,
            1,
            from,
            from.AddSeconds(30),
            20,
            false,
            3,
            projection,
            "41",
            eventId);
        var payload =
            ProgressionIntervalSettlementPersistenceCodec.Encode(
                receipt);
        var decoded =
            ProgressionIntervalSettlementPersistenceCodec.Decode(
                payload);
        Check.Equal(
            receipt,
            decoded,
            "progression interval evidence round-trips canonically");

        var consumer =
            new ProgressionIntervalSettlementOutboxConsumer();
        await consumer.ConsumeAsync(
            new OutboxEventMessage(
                eventId,
                consumer.ConsumerKey,
                ProgressionIntervalSettlementPersistenceCodec
                    .AggregateType,
                ProgressionIntervalSettlementPersistenceCodec
                    .AggregateKey(13),
                1,
                ProgressionIntervalSettlementPersistenceCodec.EventType,
                ProgressionIntervalSettlementPersistenceCodec
                    .ContractVersion,
                from.AddSeconds(30),
                payload));
    }

    private static void CheckMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id == MigrationId);
        Check.True(
            migration.Sql.Contains(
                "character_progression_interval_authority",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "online_session_id uuid NOT NULL",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "last_interval_sequence bigint NOT NULL",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "aggregate_revision >=",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ON DELETE CASCADE",
                StringComparison.Ordinal),
            "migration creates bounded per-character interval authority");
        Check.True(
            migration.Sql.Contains(
                "remaining_online_ticks >= 0",
                StringComparison.Ordinal) &&
            !migration.Sql.Contains(
                "DROP TABLE",
                StringComparison.OrdinalIgnoreCase),
            "migration hardens online duration without destructive schema work");
    }
}
