using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class LegacyTalentCommandEnvelopeChecks
{
    private static readonly byte[] CapturedRequest = Convert.FromHexString(
        "1C004127481400000000000000000000000000000A00000000000000");

    public static Task RunAsync()
    {
        CheckCapturedRequestAndServerSubject();
        CheckReconnectAndLegitimateNextOperation();
        CheckCanonicalRequestConflict();
        CheckBoundedAttemptCorrelation();
        CheckStrictMalformedRejection();
        CheckLegacyIdentityPolicy();
        CheckLowCardinalityMetrics();
        return Task.CompletedTask;
    }

    private static void CheckCapturedRequestAndServerSubject()
    {
        var connectionId = Guid.NewGuid();
        var subject = new CommandSubject(347, 7);
        Check.True(
            LegacyTalentUpgradeCommandAdapter.TryAdapt(
                CapturedRequest.AsSpan(4),
                subject,
                connectionId,
                CommandTransportKind.LegacyTcp,
                DateTimeOffset.UtcNow,
                out var adapted),
            "captured talent command adapts");
        Check.True(adapted is not null, "adapted command exists");
        var envelope = adapted!.Envelope;
        Check.Equal(
            subject,
            envelope.Subject,
            "command subject comes from authenticated server inputs");
        Check.Equal(
            0,
            envelope.Command.TalentId,
            "talent ID zero remains valid");
        Check.Equal(
            0,
            envelope.Command.ExpectedRank,
            "captured expected rank");
        Check.Equal(
            10,
            adapted.ClientTalentPoints,
            "captured points echo");

        var spoofedActor = CapturedRequest.AsSpan(4).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            spoofedActor.AsSpan(0, sizeof(uint)),
            uint.MaxValue);
        Check.True(
            LegacyTalentUpgradeCommandAdapter.TryAdapt(
                spoofedActor,
                subject,
                connectionId,
                CommandTransportKind.LegacyTcp,
                DateTimeOffset.UtcNow,
                out var spoofed),
            "untrusted actor echo does not replace server identity");
        Check.Equal(
            envelope.OperationId,
            spoofed!.Envelope.OperationId,
            "actor echo is excluded from business identity");
        Check.Equal(
            envelope.RequestHash,
            spoofed.Envelope.RequestHash,
            "actor echo is excluded from canonical request");
    }

    private static void CheckReconnectAndLegitimateNextOperation()
    {
        var subject = new CommandSubject(347, 7);
        var original = Adapt(
            CapturedRequest.AsSpan(4),
            subject,
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var reconnected = Adapt(
            CapturedRequest.AsSpan(4),
            subject,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        Check.Equal(
            original.OperationId,
            reconnected.OperationId,
            "operation identity survives connection replacement");
        Check.Equal(
            original.RequestHash,
            reconnected.RequestHash,
            "request hash survives transport replacement");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)TalentUpgradeCommandEnvelope.Validate(reconnected),
            "the reconnect envelope remains valid");

        var nextRankPayload = CapturedRequest.AsSpan(4).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            nextRankPayload.AsSpan(8, sizeof(int)),
            1);
        var nextRank = Adapt(
            nextRankPayload,
            subject,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        Check.True(
            original.OperationId != nextRank.OperationId,
            "rank N+1 is a legitimate new operation");

        var otherTalentPayload = CapturedRequest.AsSpan(4).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            otherTalentPayload.AsSpan(4, sizeof(int)),
            1);
        var otherTalent = Adapt(
            otherTalentPayload,
            subject,
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        Check.True(
            original.OperationId != otherTalent.OperationId,
            "another talent has another operation identity");
        var wrongOperation = otherTalent with
        {
            OperationId = original.OperationId
        };
        Check.Equal(
            (int)CommandEnvelopeValidation.OperationIdentityConflict,
            (int)TalentUpgradeCommandEnvelope.Validate(wrongOperation),
            "another command cannot reuse an operation identity");
    }

    private static void CheckCanonicalRequestConflict()
    {
        var subject = new CommandSubject(347, 7);
        var original = Adapt(
            CapturedRequest.AsSpan(4),
            subject,
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);

        var changedPointsPayload = CapturedRequest.AsSpan(4).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            changedPointsPayload.AsSpan(16, sizeof(int)),
            11);
        var changedPoints = Adapt(
            changedPointsPayload,
            subject,
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        Check.Equal(
            original.OperationId,
            changedPoints.OperationId,
            "points echo does not change transition identity");
        Check.Equal(
            original.RequestHash,
            changedPoints.RequestHash,
            "non-authoritative points echo is excluded from request equivalence");

        var reservedPayload = CapturedRequest.AsSpan(4).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            reservedPayload.AsSpan(12, sizeof(int)),
            123);
        BinaryPrimitives.WriteInt32LittleEndian(
            reservedPayload.AsSpan(20, sizeof(int)),
            456);
        var reservedChanged = Adapt(
            reservedPayload,
            subject,
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        Check.Equal(
            original.RequestHash,
            reservedChanged.RequestHash,
            "reserved legacy bytes do not alter canonical semantics");

        Check.Equal(
            (int)CommandEnvelopeValidation.UnsupportedVersion,
            (int)TalentUpgradeCommandEnvelope.Validate(
                original with
                {
                    ContractVersion =
                        CommandEnvelopeContract.CurrentVersion + 1
                }),
            "unsupported envelope version is rejected");
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidDigest,
            (int)TalentUpgradeCommandEnvelope.Validate(
                original with { OperationId = "not-a-digest" }),
            "malformed operation digest is rejected");

        Check.Throws<ArgumentOutOfRangeException>(
            () => CommandEnvelopeContract.Create(
                CommandFamily.TalentUpgrade,
                CommandIdentityStrength.LegacyAggregateVersion,
                subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.LegacyTcp),
                DateTimeOffset.UtcNow,
                new byte[
                    CommandEnvelopeContract.MaximumOperationScopeBytes + 1],
                Array.Empty<byte>(),
                original.Command),
            "operation identity input is bounded");
    }

    private static void CheckBoundedAttemptCorrelation()
    {
        var subject = new CommandSubject(347, 7);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureCommand);
        var now = DateTimeOffset.UtcNow;
        var first = CommandEnvelopeContract.Create(
            CommandFamily.TalentUpgrade,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            now,
            operationScope: new byte[] { 1 },
            canonicalRequest: new byte[] { 10 },
            command: "first");
        var changed = CommandEnvelopeContract.Create(
            CommandFamily.TalentUpgrade,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            now,
            operationScope: new byte[] { 1 },
            canonicalRequest: new byte[] { 11 },
            command: "changed");
        Check.Equal(
            first.OperationId,
            changed.OperationId,
            "generic client operation keeps its explicit operation scope");
        Check.True(
            first.RequestHash != changed.RequestHash,
            "different canonical semantics produce another request hash");

        var attempts = new BoundedCommandAttemptRegistry(
            capacity: 2,
            retention: TimeSpan.FromMinutes(1));
        Check.Equal(
            (int)CommandAttemptDecision.Accepted,
            (int)attempts.TryBegin(
                first.OperationId,
                first.RequestHash,
                now),
            "first command attempt is accepted");
        Check.Equal(
            (int)CommandAttemptDecision.DuplicatePending,
            (int)attempts.TryBegin(
                first.OperationId,
                first.RequestHash,
                now),
            "same pending attempt is classified as a duplicate");
        Check.Equal(
            (int)CommandAttemptDecision.RequestHashConflict,
            (int)attempts.TryBegin(
                changed.OperationId,
                changed.RequestHash,
                now),
            "same operation with changed canonical semantics is a conflict");

        attempts.Complete(
            first.OperationId,
            first.RequestHash,
            now);
        Check.Equal(
            (int)CommandAttemptDecision.DuplicateCompleted,
            (int)attempts.TryBegin(
                first.OperationId,
                first.RequestHash,
                now),
            "completed same-process retry is a duplicate");

        var retryable = CommandEnvelopeContract.Create(
            CommandFamily.TalentUpgrade,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            now,
            operationScope: new byte[] { 2 },
            canonicalRequest: new byte[] { 12 },
            command: "retryable");
        Check.Equal(
            (int)CommandAttemptDecision.Accepted,
            (int)attempts.TryBegin(
                retryable.OperationId,
                retryable.RequestHash,
                now),
            "new operation is admitted");
        attempts.Release(
            retryable.OperationId,
            retryable.RequestHash);
        Check.Equal(
            (int)CommandAttemptDecision.Accepted,
            (int)attempts.TryBegin(
                retryable.OperationId,
                retryable.RequestHash,
                now),
            "failed precondition releases the operation for retry");

        var afterExpiry = now + TimeSpan.FromMinutes(2);
        Check.Equal(
            (int)CommandAttemptDecision.Accepted,
            (int)attempts.TryBegin(
                first.OperationId,
                first.RequestHash,
                afterExpiry),
            "process-local correlation expires within a bounded retention");
        Check.True(
            attempts.Count <= 2,
            "attempt correlation never exceeds configured capacity");
    }

    private static void CheckStrictMalformedRejection()
    {
        var payload = CapturedRequest.AsSpan(4).ToArray();
        var subject = new CommandSubject(347, 7);
        CheckRejected(payload[..^1], subject, "truncated request");
        CheckRejected([.. payload, 0], subject, "oversized request");

        var negativeTalent = payload.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            negativeTalent.AsSpan(4, sizeof(int)),
            -1);
        CheckRejected(negativeTalent, subject, "negative talent");

        var negativeRank = payload.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            negativeRank.AsSpan(8, sizeof(int)),
            -1);
        CheckRejected(negativeRank, subject, "negative rank");

        var cappedRank = payload.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            cappedRank.AsSpan(8, sizeof(int)),
            100);
        CheckRejected(cappedRank, subject, "rank cap");

        var negativePoints = payload.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            negativePoints.AsSpan(16, sizeof(int)),
            -1);
        CheckRejected(negativePoints, subject, "negative points");

        CheckRejected(
            payload,
            new CommandSubject(0, 7),
            "invalid server account");
        CheckRejected(
            payload,
            new CommandSubject(347, 0),
            "invalid server character");
    }

    private static void CheckLegacyIdentityPolicy()
    {
        Check.Equal(
            (int)CommandIdentityStrength.LegacyAggregateVersion,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.TalentUpgrade),
            "talent has a natural legacy aggregate version");
        Check.Equal(
            (int)CommandIdentityStrength.UnsupportedLegacyRetry,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.PetLevelUpgrade),
            "pet level cannot fabricate a retry identity");
        Check.Equal(
            (int)CommandIdentityStrength.UnsupportedLegacyRetry,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.EquipmentForge),
            "forge cannot fabricate a retry identity");
    }

    private static void CheckLowCardinalityMetrics()
    {
        var measurements = new List<CapturedMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == CommandMetrics.MeterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) =>
            {
                measurements.Add(
                    new CapturedMeasurement(
                        instrument.Name,
                        tags.ToArray()));
            });
        listener.Start();

        CommandMetrics.Record(
            CommandFamily.TalentUpgrade,
            CommandIdentityStrength.LegacyAggregateVersion,
            CommandOutcome.Accepted);
        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.PetLevelUpgrade);

        var command = measurements.Single(value =>
            value.Name == CommandMetrics.CommandInstrumentName);
        Check.True(
            command.Tags.Select(static tag => tag.Key)
                .Order()
                .SequenceEqual(
                    new[]
                    {
                        "family",
                        "identity_strength",
                        "outcome"
                    }.Order()),
            "command metric uses only bounded dimensions");
        Check.True(
            command.Tags.All(static tag =>
                tag.Value is string),
            "command metric never labels player or operation identifiers");

        var unsupported = measurements.Single(value =>
            value.Name == CommandMetrics.UnsupportedIdentityInstrumentName);
        Check.Equal(
            1,
            unsupported.Tags.Count,
            "unsupported retry metric has one bounded family tag");
    }

    private static CommandEnvelope<TalentUpgradeCommand> Adapt(
        ReadOnlySpan<byte> payload,
        CommandSubject subject,
        Guid connectionId,
        CommandTransportKind transport)
    {
        if (!LegacyTalentUpgradeCommandAdapter.TryAdapt(
                payload,
                subject,
                connectionId,
                transport,
                DateTimeOffset.UtcNow,
                out var envelope))
        {
            throw new InvalidOperationException(
                "Expected the talent command to adapt.");
        }

        return envelope!.Envelope;
    }

    private static void CheckRejected(
        ReadOnlySpan<byte> payload,
        CommandSubject subject,
        string description)
    {
        Check.True(
            !LegacyTalentUpgradeCommandAdapter.TryAdapt(
                payload,
                subject,
                Guid.NewGuid(),
                CommandTransportKind.LegacyTcp,
                DateTimeOffset.UtcNow,
                out _),
            description);
    }

    private sealed record CapturedMeasurement(
        string Name,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
