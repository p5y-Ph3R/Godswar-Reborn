using System.Text;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Zodiac;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ZodiacSkillGridUpgradePersistenceChecks
{
    private static readonly Guid EventId =
        Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");

    public static async Task RunAsync()
    {
        var receipt = CreateSuccessfulReceipt();
        var payload =
            ZodiacSkillGridUpgradePersistenceCodec.Encode(receipt);

        CheckAggregateIdentity();
        CheckCanonicalRoundTrip(receipt, payload);
        CheckTerminalReceipts();
        CheckStoredEvidenceVerification(receipt, payload);
        CheckMalformedEvidence(payload);
        await CheckOutboxConsumerAsync(receipt, payload);
        CheckOversizedStoredJsonAllocationBound();
    }

    private static void CheckAggregateIdentity()
    {
        Check.Equal(
            "character:13:zodiac-skill-grids",
            ZodiacSkillGridUpgradePersistenceCodec
                .CommandAggregateKey(13),
            "Zodiac UUID command identity is character scoped");
        Check.Equal(
            "character:13:zodiac-grid:6",
            ZodiacSkillGridUpgradePersistenceCodec
                .EventAggregateKey(13, 6),
            "Zodiac outbox identity is character and grid scoped");
        Check.True(
            !string.Equals(
                ZodiacSkillGridUpgradePersistenceCodec
                    .EventAggregateKey(13, 6),
                ZodiacSkillGridUpgradePersistenceCodec
                    .EventAggregateKey(13, 7),
                StringComparison.Ordinal),
            "different grids have independent projection sequences");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ZodiacSkillGridUpgradePersistenceCodec
                .CommandAggregateKey(0),
            "nonpositive character aggregate identity rejects");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ZodiacSkillGridUpgradePersistenceCodec
                .EventAggregateKey(13, 16),
            "out-of-range grid aggregate identity rejects");
    }

    private static void CheckCanonicalRoundTrip(
        ZodiacSkillGridUpgradeExecutionReceipt expected,
        byte[] payload)
    {
        const string canonical =
            "{\"contractVersion\":1,\"characterId\":13," +
            "\"status\":1,\"gridIndex\":6,\"previousLevel\":1," +
            "\"currentLevel\":2,\"currentZodiacLevel\":10," +
            "\"requiredZodiacLevel\":5,\"energyCost\":5," +
            "\"energyBefore\":100," +
            "\"energyRemainderBeforeX100\":25," +
            "\"energyAfter\":95," +
            "\"energyRemainderAfterX100\":25," +
            "\"talentPointCost\":7,\"talentPointsBefore\":100," +
            "\"talentPointsAfter\":93,\"selectedSkillId\":10057," +
            "\"auditReference\":\"421\",\"outboxEventId\":" +
            "\"10213243-5465-7687-98a9-bacbdcedfe0f\"}";
        Check.Equal(
            canonical,
            Encoding.UTF8.GetString(payload),
            "Zodiac upgrade receipt has canonical JSON");

        var decoded =
            ZodiacSkillGridUpgradePersistenceCodec.Decode(payload);
        CheckReceipt(expected, decoded, "canonical round-trip");
        Check.True(
            ZodiacSkillGridUpgradePersistenceCodec
                .Encode(decoded)
                .SequenceEqual(payload),
            "decoded Zodiac upgrade receipt re-encodes canonically");
        Check.Equal(
            ZodiacSkillGridUpgradePersistenceCodec.CommittedResultCode,
            ZodiacSkillGridUpgradePersistenceCodec.ResultCode(
                decoded.Status),
            "successful Zodiac upgrade is committed");
    }

    private static void CheckTerminalReceipts()
    {
        var receipts = new[]
        {
            CreateTerminalReceipt(
                ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid),
            CreateTerminalReceipt(
                ZodiacSkillGridUpgradeReceiptStatus.MaximumLevelReached),
            CreateTerminalReceipt(
                ZodiacSkillGridUpgradeReceiptStatus.ZodiacLevelTooLow),
            CreateTerminalReceipt(
                ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy),
            CreateTerminalReceipt(
                ZodiacSkillGridUpgradeReceiptStatus
                    .InsufficientTalentPoints)
        };

        foreach (var expected in receipts)
        {
            var payload =
                ZodiacSkillGridUpgradePersistenceCodec.Encode(expected);
            var decoded =
                ZodiacSkillGridUpgradePersistenceCodec.Decode(payload);
            CheckReceipt(
                expected,
                decoded,
                $"terminal {expected.Status} round-trip");
            Check.Equal(
                ZodiacSkillGridUpgradePersistenceCodec
                    .TerminalRejectedResultCode,
                ZodiacSkillGridUpgradePersistenceCodec.ResultCode(
                    expected.Status),
                $"terminal {expected.Status} result code");
            Check.True(
                !Encoding.UTF8.GetString(payload).Contains(
                    EventId.ToString(),
                    StringComparison.Ordinal),
                $"terminal {expected.Status} emits no outbox identity");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => ZodiacSkillGridUpgradePersistenceCodec.ResultCode(
                (ZodiacSkillGridUpgradeReceiptStatus)255),
            "unknown Zodiac receipt status has no durable result code");
    }

    private static void CheckStoredEvidenceVerification(
        ZodiacSkillGridUpgradeExecutionReceipt expected,
        byte[] payload)
    {
        var canonicalHash =
            ZodiacSkillGridUpgradePersistenceCodec.Hash(payload);
        var reordered =
            "{\"outboxEventId\":\"" + EventId + "\"," +
            "\"auditReference\":\"421\"," +
            "\"selectedSkillId\":10057,\"talentPointsAfter\":93," +
            "\"talentPointsBefore\":100,\"talentPointCost\":7," +
            "\"energyRemainderAfterX100\":25,\"energyAfter\":95," +
            "\"energyRemainderBeforeX100\":25," +
            "\"energyBefore\":100,\"energyCost\":5," +
            "\"requiredZodiacLevel\":5,\"currentZodiacLevel\":10," +
            "\"currentLevel\":2,\"previousLevel\":1,\"gridIndex\":6," +
            "\"status\":1,\"characterId\":13,\"contractVersion\":1}";
        var verified =
            ZodiacSkillGridUpgradePersistenceCodec.DecodeAndVerify(
                reordered,
                canonicalHash,
                ZodiacSkillGridUpgradePersistenceCodec
                    .CommittedResultCode,
                expectedAuditId: 421);
        CheckReceipt(
            expected,
            verified,
            "canonical verification of reordered JSONB evidence");

        var terminal = CreateTerminalReceipt(
            ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy);
        var terminalPayload =
            ZodiacSkillGridUpgradePersistenceCodec.Encode(terminal);
        var terminalVerified =
            ZodiacSkillGridUpgradePersistenceCodec.DecodeAndVerify(
                Encoding.UTF8.GetString(terminalPayload),
                ZodiacSkillGridUpgradePersistenceCodec.Hash(
                    terminalPayload),
                ZodiacSkillGridUpgradePersistenceCodec
                    .TerminalRejectedResultCode,
                expectedAuditId: 425);
        CheckReceipt(
            terminal,
            terminalVerified,
            "terminal-rejection evidence verification");

        var corruptedHash = (byte[])canonicalHash.Clone();
        corruptedHash[0] ^= 0x80;
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridUpgradePersistenceCodec
                .DecodeAndVerify(
                    Encoding.UTF8.GetString(payload),
                    corruptedHash,
                    ZodiacSkillGridUpgradePersistenceCodec
                        .CommittedResultCode,
                    expectedAuditId: 421),
            "Zodiac upgrade rejects a corrupted evidence hash");
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridUpgradePersistenceCodec
                .DecodeAndVerify(
                    Encoding.UTF8.GetString(payload),
                    canonicalHash,
                    ZodiacSkillGridUpgradePersistenceCodec
                        .TerminalRejectedResultCode,
                    expectedAuditId: 421),
            "Zodiac upgrade rejects a mismatched result code");
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridUpgradePersistenceCodec
                .DecodeAndVerify(
                    Encoding.UTF8.GetString(payload),
                    canonicalHash,
                    ZodiacSkillGridUpgradePersistenceCodec
                        .CommittedResultCode,
                    expectedAuditId: 422),
            "Zodiac upgrade rejects a mismatched audit reference");
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridUpgradePersistenceCodec
                .DecodeAndVerify(
                    Encoding.UTF8.GetString(payload),
                    canonicalHash.AsSpan(0, 31),
                    ZodiacSkillGridUpgradePersistenceCodec
                        .CommittedResultCode,
                    expectedAuditId: 421),
            "Zodiac upgrade rejects a truncated evidence hash");
    }

    private static async Task CheckOutboxConsumerAsync(
        ZodiacSkillGridUpgradeExecutionReceipt receipt,
        byte[] payload)
    {
        var consumer = new ZodiacSkillGridUpgradeOutboxConsumer();
        Check.Equal(
            ZodiacSkillGridUpgradePersistenceCodec.ConsumerKey,
            consumer.ConsumerKey,
            "Zodiac upgrade consumer key");
        Check.Equal(
            (int)OutboxOrderingPolicy.VersionedState,
            (int)consumer.OrderingPolicy,
            "Zodiac upgrade consumer uses latest-wins ordering");
        Check.Equal(
            (int)OutboxOrderingDecision.Deliver,
            (int)OutboxOrderingRules.Decide(
                consumer.OrderingPolicy,
                lastAppliedRevision: 1,
                incomingRevision: 10),
            "latest-wins projection accepts a revision gap");

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
                    ZodiacSkillGridUpgradePersistenceCodec
                        .EventAggregateKey(receipt.CharacterId + 1, 6)),
            "mismatched aggregate key");
        await CheckRejectedAsync(
            consumer,
            Copy(message, consumerKey: "zodiac_grid_upgrade_v2"),
            "mismatched consumer contract");
        await CheckRejectedAsync(
            consumer,
            Copy(message, aggregateType: "zodiac_grid_upgrade_bad"),
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
                    ZodiacSkillGridUpgradePersistenceCodec
                        .ContractVersion + 1),
            "mismatched schema contract");
        await CheckRejectedAsync(
            consumer,
            Copy(message, aggregateRevision: 3),
            "mismatched grid revision");

        var terminal = CreateTerminalReceipt(
            ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy);
        await CheckRejectedAsync(
            consumer,
            Copy(
                message,
                payload:
                    ZodiacSkillGridUpgradePersistenceCodec.Encode(terminal)),
            "terminal rejection in the outbox");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Check.Throws<OperationCanceledException>(
            () => consumer
                .ConsumeAsync(message, cancellation.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult(),
            "Zodiac upgrade consumer observes cancellation");
    }

    private static ZodiacSkillGridUpgradeExecutionReceipt
        CreateSuccessfulReceipt() =>
        new(
            characterId: 13,
            status: ZodiacSkillGridUpgradeReceiptStatus.Succeeded,
            gridIndex: 6,
            previousLevel: 1,
            currentLevel: 2,
            currentZodiacLevel: 10,
            requiredZodiacLevel: 5,
            energyCost: 5,
            energyBefore: 100,
            energyRemainderBeforeX100: 25,
            energyAfter: 95,
            energyRemainderAfterX100: 25,
            talentPointCost: 7,
            talentPointsBefore: 100,
            talentPointsAfter: 93,
            selectedSkillId: 10_057,
            auditReference: "421",
            outboxEventId: EventId);

    private static ZodiacSkillGridUpgradeExecutionReceipt
        CreateTerminalReceipt(
            ZodiacSkillGridUpgradeReceiptStatus status)
    {
        var (previousLevel, zodiacLevel, requiredLevel, energyCost,
            energy, energyRemainder, talentCost, talentPoints, audit) =
            status switch
            {
                ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid =>
                    (0, 10, 0, 0, 100, 25, 0, 100, "422"),
                ZodiacSkillGridUpgradeReceiptStatus.MaximumLevelReached =>
                    (50, 30, 0, 0, 100, 25, 0, 100, "423"),
                ZodiacSkillGridUpgradeReceiptStatus.ZodiacLevelTooLow =>
                    (1, 4, 5, 5, 100, 25, 7, 100, "424"),
                ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy =>
                    (1, 10, 5, 5, 4, 99, 7, 100, "425"),
                ZodiacSkillGridUpgradeReceiptStatus
                    .InsufficientTalentPoints =>
                    (1, 10, 5, 5, 100, 25, 7, 6, "426"),
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            };
        return new ZodiacSkillGridUpgradeExecutionReceipt(
            characterId: 13,
            status,
            gridIndex: 6,
            previousLevel: checked((byte)previousLevel),
            currentLevel: checked((byte)previousLevel),
            currentZodiacLevel: checked((byte)zodiacLevel),
            requiredZodiacLevel: checked((byte)requiredLevel),
            energyCost,
            energyBefore: energy,
            energyRemainderBeforeX100: energyRemainder,
            energyAfter: energy,
            energyRemainderAfterX100: energyRemainder,
            talentPointCost: talentCost,
            talentPointsBefore: talentPoints,
            talentPointsAfter: talentPoints,
            selectedSkillId:
                ZodiacSkillGridUpgradeCommandEnvelope.NoSelectedSkillId,
            auditReference: audit,
            outboxEventId: null);
    }

    private static OutboxEventMessage CreateMessage(
        ZodiacSkillGridUpgradeExecutionReceipt receipt,
        byte[] payload) =>
        new(
            receipt.OutboxEventId!.Value,
            ZodiacSkillGridUpgradePersistenceCodec.ConsumerKey,
            ZodiacSkillGridUpgradePersistenceCodec.EventAggregateType,
            ZodiacSkillGridUpgradePersistenceCodec.EventAggregateKey(
                receipt.CharacterId,
                receipt.GridIndex),
            receipt.AggregateRevision!.Value,
            ZodiacSkillGridUpgradePersistenceCodec.EventType,
            ZodiacSkillGridUpgradePersistenceCodec.ContractVersion,
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
        ZodiacSkillGridUpgradeExecutionReceipt expected,
        ZodiacSkillGridUpgradeExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected == actual,
            $"Zodiac upgrade receipt matches after {description}");
    }

    private static async Task CheckRejectedAsync(
        ZodiacSkillGridUpgradeOutboxConsumer consumer,
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
            $"Assertion failed: Zodiac upgrade rejects {description}.");
    }
}
