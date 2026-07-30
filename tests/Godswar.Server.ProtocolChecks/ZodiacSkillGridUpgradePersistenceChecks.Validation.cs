using System.Text;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Zodiac;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ZodiacSkillGridUpgradePersistenceChecks
{
    private static void CheckMalformedEvidence(byte[] payload)
    {
        var canonical = Encoding.UTF8.GetString(payload);
        CheckDecodeRejects(
            canonical.Replace(
                "\"contractVersion\":1",
                "\"contractVersion\":2",
                StringComparison.Ordinal),
            "unsupported contract version");
        CheckDecodeRejects(
            canonical.Replace(
                "\"status\":1,",
                string.Empty,
                StringComparison.Ordinal),
            "missing field");
        CheckDecodeRejects(
            canonical.Replace(
                "\"status\":1,",
                "\"status\":1,\"status\":1,",
                StringComparison.Ordinal),
            "duplicate field");
        CheckDecodeRejects(
            canonical[..^1] + ",\"extra\":1}",
            "unknown field");
        CheckDecodeRejects(
            canonical.Replace(
                "\"energyCost\":5",
                "\"energyCost\":\"5\"",
                StringComparison.Ordinal),
            "wrong scalar type");
        CheckDecodeRejects(
            canonical.Replace(
                "\"energyAfter\":95",
                "\"energyAfter\":96",
                StringComparison.Ordinal),
            "contradictory resource evidence");
        CheckDecodeRejects(
            canonical.Replace(
                "\"outboxEventId\":\"" + EventId + "\"",
                "\"outboxEventId\":null",
                StringComparison.Ordinal),
            "successful result without outbox identity");
        CheckDecodeRejects(
            canonical[..^1] + ",}",
            "trailing comma");
        CheckDecodeRejects("[]", "non-object payload");
    }

    private static void CheckOversizedStoredJsonAllocationBound()
    {
        var hash = new byte[32];
        var warmup = new string(
            'x',
            OutboxEventMessage.MaximumPayloadBytes + 1);
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridUpgradePersistenceCodec
                .DecodeAndVerify(
                    warmup,
                    hash,
                    ZodiacSkillGridUpgradePersistenceCodec
                        .CommittedResultCode,
                    expectedAuditId: 1),
            "oversized stored Zodiac upgrade JSON warmup rejects");

        var oversized = new string(
            'x',
            OutboxEventMessage.MaximumPayloadBytes * 64);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridUpgradePersistenceCodec
                .DecodeAndVerify(
                    oversized,
                    hash,
                    ZodiacSkillGridUpgradePersistenceCodec
                        .CommittedResultCode,
                    expectedAuditId: 1),
            "oversized stored Zodiac upgrade JSON rejects");
        var allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(oversized);
        Check.True(
            allocated < 64 * 1024,
            "oversized Zodiac upgrade rejection allocation is bounded " +
            $"({allocated} bytes)");

        var oversizedBytes = new byte[
            OutboxEventMessage.MaximumPayloadBytes + 1];
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridUpgradePersistenceCodec.Decode(
                oversizedBytes),
            "oversized Zodiac upgrade payload rejects before copying");
    }

    private static void CheckDecodeRejects(
        string json,
        string description) =>
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridUpgradePersistenceCodec.Decode(
                Encoding.UTF8.GetBytes(json)),
            $"Zodiac upgrade rejects {description}");
}
