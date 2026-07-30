using System.Text;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Zodiac;

namespace Godswar.Server.ProtocolChecks;

internal static class ZodiacSkillGridActivationPersistenceChecks
{
    private static readonly Guid EventId =
        Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    public static async Task RunAsync()
    {
        var receipt = CreateReceipt();
        var payload =
            ZodiacSkillGridActivationPersistenceCodec.Encode(receipt);

        CheckCanonicalRoundTrip(receipt, payload);
        CheckStoredEvidenceVerification(receipt, payload);
        await CheckOutboxConsumerAsync(receipt, payload);
        CheckOversizedStoredJsonAllocationBound();
    }

    private static void CheckCanonicalRoundTrip(
        ZodiacSkillGridActivationExecutionReceipt expected,
        byte[] payload)
    {
        const string canonical =
            "{\"contractVersion\":1,\"characterId\":13," +
            "\"gridIndex\":6,\"goldCost\":10000," +
            "\"goldBefore\":25000,\"goldAfter\":15000," +
            "\"currentLevel\":1,\"selectedSkillId\":10057," +
            "\"walletRevision\":12,\"auditReference\":\"421\"," +
            "\"outboxEventId\":" +
            "\"00112233-4455-6677-8899-aabbccddeeff\"}";
        Check.Equal(
            canonical,
            Encoding.UTF8.GetString(payload),
            "Zodiac activation receipt has canonical JSON");

        var decoded =
            ZodiacSkillGridActivationPersistenceCodec.Decode(payload);
        CheckReceipt(expected, decoded, "canonical round-trip");
        Check.True(
            ZodiacSkillGridActivationPersistenceCodec
                .Encode(decoded)
                .SequenceEqual(payload),
            "decoded Zodiac receipt re-encodes canonically");
    }

    private static void CheckStoredEvidenceVerification(
        ZodiacSkillGridActivationExecutionReceipt expected,
        byte[] payload)
    {
        var canonicalHash =
            ZodiacSkillGridActivationPersistenceCodec.Hash(payload);
        var nonCanonical =
            "{ \"outboxEventId\": \"" + EventId + "\", " +
            "\"auditReference\": \"421\", " +
            "\"walletRevision\": 12, " +
            "\"selectedSkillId\": 10057, \"currentLevel\": 1, " +
            "\"goldAfter\": 15000, \"goldBefore\": 25000, " +
            "\"goldCost\": 10000, \"gridIndex\": 6, " +
            "\"characterId\": 13, \"contractVersion\": 1 }";
        var verified =
            ZodiacSkillGridActivationPersistenceCodec.DecodeAndVerify(
                nonCanonical,
                canonicalHash,
                ZodiacSkillGridActivationPersistenceCodec.ResultCode,
                expectedAuditId: 421);
        CheckReceipt(
            expected,
            verified,
            "canonical verification of reordered JSONB evidence");

        var corruptedHash = (byte[])canonicalHash.Clone();
        corruptedHash[0] ^= 0x80;
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridActivationPersistenceCodec
                .DecodeAndVerify(
                    Encoding.UTF8.GetString(payload),
                    corruptedHash,
                    ZodiacSkillGridActivationPersistenceCodec.ResultCode,
                    expectedAuditId: 421),
            "Zodiac activation rejects a corrupted evidence hash");
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridActivationPersistenceCodec
                .DecodeAndVerify(
                    Encoding.UTF8.GetString(payload),
                    canonicalHash,
                    "failed",
                    expectedAuditId: 421),
            "Zodiac activation rejects a corrupted result code");
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridActivationPersistenceCodec
                .DecodeAndVerify(
                    Encoding.UTF8.GetString(payload),
                    canonicalHash,
                    ZodiacSkillGridActivationPersistenceCodec.ResultCode,
                    expectedAuditId: 422),
            "Zodiac activation rejects a mismatched audit reference");
    }

    private static async Task CheckOutboxConsumerAsync(
        ZodiacSkillGridActivationExecutionReceipt receipt,
        byte[] payload)
    {
        var consumer = new ZodiacSkillGridActivationOutboxConsumer();
        Check.Equal(
            ZodiacSkillGridActivationPersistenceCodec.ConsumerKey,
            consumer.ConsumerKey,
            "Zodiac activation consumer key");
        Check.Equal(
            (int)OutboxOrderingPolicy.StrictSequence,
            (int)consumer.OrderingPolicy,
            "Zodiac activation consumer ordering");

        var message = CreateMessage(receipt, payload);
        await consumer.ConsumeAsync(message);

        await CheckRejectedAsync(
            consumer,
            Copy(message, eventId: Guid.NewGuid()),
            "mismatched outbox event ID");
        await CheckRejectedAsync(
            consumer,
            Copy(
                message,
                aggregateKey:
                    ZodiacSkillGridActivationPersistenceCodec
                        .AggregateKey(receipt.CharacterId + 1, 6)),
            "mismatched aggregate key");
        await CheckRejectedAsync(
            consumer,
            Copy(message, consumerKey: "zodiac_grid_activation_v2"),
            "mismatched consumer contract");
        await CheckRejectedAsync(
            consumer,
            Copy(message, aggregateType: "zodiac_grid_activation_bad"),
            "mismatched aggregate type");
        await CheckRejectedAsync(
            consumer,
            Copy(message, eventType: "zodiac.skill_grid_rejected"),
            "mismatched event type");
        await CheckRejectedAsync(
            consumer,
            Copy(
                message,
                schemaVersion:
                    ZodiacSkillGridActivationPersistenceCodec
                        .ContractVersion + 1),
            "mismatched schema contract");
        await CheckRejectedAsync(
            consumer,
            Copy(message, aggregateRevision: 2),
            "mismatched one-shot aggregate revision");

        var unsupportedPayload = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(payload).Replace(
                "\"contractVersion\":1",
                "\"contractVersion\":2",
                StringComparison.Ordinal));
        await CheckRejectedAsync(
            consumer,
            Copy(message, payload: unsupportedPayload),
            "mismatched payload contract");
    }

    private static void CheckOversizedStoredJsonAllocationBound()
    {
        var hash = new byte[32];
        var warmup = new string(
            'x',
            OutboxEventMessage.MaximumPayloadBytes + 1);
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridActivationPersistenceCodec
                .DecodeAndVerify(
                    warmup,
                    hash,
                    ZodiacSkillGridActivationPersistenceCodec.ResultCode,
                    expectedAuditId: 1),
            "oversized stored JSON warmup rejects");

        var oversized = new string(
            'x',
            OutboxEventMessage.MaximumPayloadBytes * 64);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridActivationPersistenceCodec
                .DecodeAndVerify(
                    oversized,
                    hash,
                    ZodiacSkillGridActivationPersistenceCodec.ResultCode,
                    expectedAuditId: 1),
            "oversized stored JSON rejects before UTF-8 materialization");
        var allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(oversized);
        Check.True(
            allocated < 64 * 1024,
            $"oversized stored JSON rejection allocation is bounded " +
            $"({allocated} bytes)");

        var oversizedBytes = new byte[
            OutboxEventMessage.MaximumPayloadBytes + 1];
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridActivationPersistenceCodec.Decode(
                oversizedBytes),
            "oversized stored payload rejects before copying");
    }

    private static ZodiacSkillGridActivationExecutionReceipt
        CreateReceipt() =>
        new(
            characterId: 13,
            gridIndex: 6,
            goldCost: 10_000,
            goldBefore: 25_000,
            goldAfter: 15_000,
            currentLevel: 1,
            selectedSkillId: 10_057,
            walletRevision: 12,
            auditReference: "421",
            outboxEventId: EventId);

    private static OutboxEventMessage CreateMessage(
        ZodiacSkillGridActivationExecutionReceipt receipt,
        byte[] payload) =>
        new(
            receipt.OutboxEventId,
            ZodiacSkillGridActivationPersistenceCodec.ConsumerKey,
            ZodiacSkillGridActivationPersistenceCodec.AggregateType,
            ZodiacSkillGridActivationPersistenceCodec.AggregateKey(
                receipt.CharacterId,
                receipt.GridIndex),
            ZodiacSkillGridActivationPersistenceCodec.AggregateRevision,
            ZodiacSkillGridActivationPersistenceCodec.EventType,
            ZodiacSkillGridActivationPersistenceCodec.ContractVersion,
            DateTimeOffset.Parse("2026-07-30T12:00:00Z"),
            payload);

    private static OutboxEventMessage Copy(
        OutboxEventMessage source,
        Guid? eventId = null,
        string? consumerKey = null,
        string? aggregateType = null,
        string? aggregateKey = null,
        long? aggregateRevision = null,
        string? eventType = null,
        int? schemaVersion = null,
        ReadOnlyMemory<byte>? payload = null) =>
        new(
            eventId ?? source.EventId,
            consumerKey ?? source.ConsumerKey,
            aggregateType ?? source.AggregateType,
            aggregateKey ?? source.AggregateKey,
            aggregateRevision ?? source.AggregateRevision,
            eventType ?? source.EventType,
            schemaVersion ?? source.SchemaVersion,
            source.OccurredAtUtc,
            payload ?? source.Payload);

    private static void CheckReceipt(
        ZodiacSkillGridActivationExecutionReceipt expected,
        ZodiacSkillGridActivationExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected == actual,
            $"Zodiac activation receipt matches after {description}");
    }

    private static async Task CheckRejectedAsync(
        ZodiacSkillGridActivationOutboxConsumer consumer,
        OutboxEventMessage message,
        string description)
    {
        try
        {
            await consumer.ConsumeAsync(message);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: Zodiac activation rejects {description}.");
    }
}
