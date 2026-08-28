using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.WorldInstances;

internal static class MedusaDurableAdmissionPolicy
{
    public const int Sha256HexLength = 64;

    public static bool IsAllowedTransition(
        MedusaAdmissionState current,
        MedusaAdmissionState target) =>
        (current, target) switch
        {
            (MedusaAdmissionState.Reserved,
                MedusaAdmissionState.RuntimeReady) => true,
            (MedusaAdmissionState.RuntimeReady,
                MedusaAdmissionState.RosterTransferCommitted) => true,
            (MedusaAdmissionState.RosterTransferCommitted,
                MedusaAdmissionState.ConsumedRunning) => true,
            (MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Completed) => true,
            (MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.Abandoned) => true,
            (MedusaAdmissionState.ConsumedRunning,
                MedusaAdmissionState.TimedOut) => true,
            (MedusaAdmissionState.Reserved,
                MedusaAdmissionState.Released) => true,
            (MedusaAdmissionState.RuntimeReady,
                MedusaAdmissionState.Released) => true,
            (MedusaAdmissionState.Completed,
                MedusaAdmissionState.CompletedCleaned) => true,
            (MedusaAdmissionState.Abandoned,
                MedusaAdmissionState.AbandonedCleaned) => true,
            (MedusaAdmissionState.TimedOut,
                MedusaAdmissionState.TimedOutCleaned) => true,
            (MedusaAdmissionState.Released,
                MedusaAdmissionState.ReleasedCleaned) => true,
            _ => false
        };

    public static bool IsCleanupCompletedState(MedusaAdmissionState state) =>
        state is
            MedusaAdmissionState.CompletedCleaned or
            MedusaAdmissionState.AbandonedCleaned or
            MedusaAdmissionState.TimedOutCleaned or
            MedusaAdmissionState.ReleasedCleaned;

    public static string ComputeRosterHash(PartyAdmissionLease party)
    {
        ArgumentNullException.ThrowIfNull(party);
        using var writer = new CanonicalHashWriter("medusa-roster-v1");
        writer.Write(party.LeaseId);
        writer.Write(party.PartyId);
        writer.Write(party.PartyRevision);
        writer.Write(party.LeaderAccountId);
        writer.Write(party.LeaderCharacterId);
        writer.Write(party.IssuedAtUtc.UtcTicks);
        writer.Write(party.ExpiresAtUtc.UtcTicks);
        writer.Write(party.Members.Length);
        foreach (var member in party.Members)
        {
            writer.Write(member.AccountId);
            writer.Write(member.CharacterId);
            writer.Write(member.Ownership.OwnerId);
            writer.Write(member.Ownership.Generation);
            writer.Write(member.RealmId.Value);
            writer.Write(member.Level);
            writer.Write(member.SourceWorldInstanceId.Value);
            writer.Write(member.SourceMapId.Value);
        }
        return writer.Complete();
    }

    public static string ComputeRequestHash(
        MedusaAdmissionReservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var writer = new CanonicalHashWriter("medusa-admission-v1");
        writer.Write(request.AdmissionId.Value);
        writer.Write(request.WorldInstanceId.Value);
        writer.Write(request.RealmDay.RealmId.Value);
        writer.Write(request.RealmDay.Day.DayNumber);
        writer.Write(request.RealmDay.CalendarTimeZoneId!);
        writer.Write(request.RealmDay.TimeZoneRulesFingerprint!);
        writer.Write(request.RealmDay.CalendarRevision);
        writer.Write((byte)request.Difficulty);
        writer.Write(request.ContentMapId.Value);
        writer.Write(request.Source.WorldInstanceId.Value);
        writer.Write(request.Source.MapId.Value);
        writer.Write(request.Source.NpcId);
        writer.Write(request.RequestedAtUtc.UtcTicks);
        writer.Write(request.EncounterContentFingerprint);
        writer.Write(request.RosterHash);
        return writer.Complete();
    }

    public static string ComputeTransitionHash(
        MedusaAdmissionTransitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var writer = new CanonicalHashWriter("medusa-transition-v1");
        writer.Write(request.TransitionId);
        writer.Write(request.AdmissionId.Value);
        writer.Write((short)request.ExpectedState);
        writer.Write((short)request.TargetState);
        writer.Write(request.OccurredAtUtc.UtcTicks);
        writer.Write(request.BarrierEvidence is null ? (byte)0 : (byte)1);
        if (request.BarrierEvidence is { } barrier)
        {
            writer.Write(barrier.StageId);
            writer.Write(barrier.PreparationHash);
        }
        writer.Write(request.CleanupEvidence is null ? (byte)0 : (byte)1);
        if (request.CleanupEvidence is { } cleanup)
        {
            writer.Write((byte)cleanup.Kind);
            writer.Write(cleanup.RosterOperationId);
            writer.Write(cleanup.RuntimeOperationId);
        }
        return writer.Complete();
    }

    public static DateTimeOffset CanonicalUtc(
        DateTimeOffset value,
        string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Medusa admission timestamps must be non-default UTC.",
                parameterName);
        }

        // PostgreSQL timestamptz has microsecond precision. Canonicalizing at
        // the contract boundary keeps request hashes stable after round-trip.
        var ticks = value.UtcTicks;
        return new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
    }

    public static void ValidateSnapshot(MedusaAdmissionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.AdmissionId.IsValid ||
            !snapshot.WorldInstanceId.IsValid ||
            !snapshot.RealmDay.IsValid ||
            !snapshot.Source.IsValid ||
            !snapshot.ContentMapId.IsValid ||
            snapshot.Revision <= 0)
        {
            throw new InvalidDataException(
                "A durable Medusa admission snapshot has invalid identity data.");
        }
        ValidateHash(snapshot.RosterHash, nameof(snapshot.RosterHash));
        ValidateHash(snapshot.RequestHash, nameof(snapshot.RequestHash));
        ValidateHash(
            snapshot.EncounterContentFingerprint,
            nameof(snapshot.EncounterContentFingerprint));
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                snapshot.Difficulty,
                out var definition) ||
            definition.ContentMapId != snapshot.ContentMapId ||
            !string.Equals(
                snapshot.RosterHash,
                ComputeRosterHash(snapshot.Party),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A durable Medusa admission snapshot disagrees with policy or roster evidence.");
        }

        var reserved = CanonicalUtc(
            snapshot.ReservedAtUtc,
            nameof(snapshot.ReservedAtUtc));
        var runtime = CanonicalNullable(snapshot.RuntimeReadyAtUtc);
        var transferCommitted = CanonicalNullable(
            snapshot.RosterTransferCommittedAtUtc);
        var consumed = CanonicalNullable(snapshot.ConsumedAtUtc);
        var terminal = CanonicalNullable(snapshot.TerminalAtUtc);
        var released = CanonicalNullable(snapshot.ReleasedAtUtc);
        var cleanup = CanonicalNullable(snapshot.CleanupCompletedAtUtc);
        if (reserved != snapshot.ReservedAtUtc ||
            runtime != snapshot.RuntimeReadyAtUtc ||
            transferCommitted != snapshot.RosterTransferCommittedAtUtc ||
            consumed != snapshot.ConsumedAtUtc ||
            terminal != snapshot.TerminalAtUtc ||
            released != snapshot.ReleasedAtUtc ||
            cleanup != snapshot.CleanupCompletedAtUtc)
        {
            throw new InvalidDataException(
                "A durable Medusa admission snapshot contains non-canonical timestamps.");
        }

        var shapeIsValid = snapshot.State switch
        {
            MedusaAdmissionState.Reserved =>
                runtime is null && transferCommitted is null && consumed is null &&
                terminal is null && released is null,
            MedusaAdmissionState.RuntimeReady =>
                runtime is not null && transferCommitted is null && consumed is null &&
                terminal is null && released is null,
            MedusaAdmissionState.RosterTransferCommitted =>
                runtime is not null && transferCommitted is not null &&
                consumed is null && terminal is null && released is null,
            MedusaAdmissionState.ConsumedRunning =>
                runtime is not null && transferCommitted is not null &&
                consumed is not null && terminal is null && released is null,
            MedusaAdmissionState.Completed or
                MedusaAdmissionState.Abandoned or
                MedusaAdmissionState.TimedOut =>
                runtime is not null && transferCommitted is not null &&
                consumed is not null && terminal is not null &&
                released is null,
            MedusaAdmissionState.Released =>
                transferCommitted is null && consumed is null &&
                terminal is null && released is not null,
            MedusaAdmissionState.CompletedCleaned or
                MedusaAdmissionState.AbandonedCleaned or
                MedusaAdmissionState.TimedOutCleaned =>
                runtime is not null && transferCommitted is not null &&
                consumed is not null && terminal is not null &&
                released is null && cleanup is not null,
            MedusaAdmissionState.ReleasedCleaned =>
                transferCommitted is null && consumed is null &&
                terminal is null && released is not null && cleanup is not null,
            _ => false
        };
        if (!shapeIsValid ||
            (runtime is not null && runtime < reserved) ||
            (transferCommitted is not null &&
                (runtime is null || transferCommitted < runtime)) ||
            (consumed is not null &&
                (transferCommitted is null || consumed < transferCommitted)) ||
            (terminal is not null &&
                (consumed is null || terminal < consumed)) ||
            (released is not null && released <
                (runtime ?? reserved)) ||
            (cleanup is not null && cleanup <
                (released ?? terminal ?? reserved)))
        {
            throw new InvalidDataException(
                "A durable Medusa admission snapshot has an impossible state history.");
        }
        var requiresBarrierEvidence = snapshot.State is
            MedusaAdmissionState.RosterTransferCommitted or
            MedusaAdmissionState.ConsumedRunning or
            MedusaAdmissionState.Completed or
            MedusaAdmissionState.Abandoned or
            MedusaAdmissionState.TimedOut or
            MedusaAdmissionState.CompletedCleaned or
            MedusaAdmissionState.AbandonedCleaned or
            MedusaAdmissionState.TimedOutCleaned;
        if (requiresBarrierEvidence != (snapshot.BarrierEvidence is not null))
        {
            throw new InvalidDataException(
                "A durable Medusa admission snapshot has invalid transfer-barrier evidence.");
        }
        var requiresCleanupEvidence = IsCleanupCompletedState(snapshot.State);
        if (requiresCleanupEvidence !=
                (snapshot.CleanupEvidence is not null) ||
            requiresCleanupEvidence != (cleanup is not null))
        {
            throw new InvalidDataException(
                "A durable Medusa admission has invalid cleanup evidence.");
        }
        if (snapshot.CleanupEvidence is { } cleanupEvidence &&
            ((snapshot.State == MedusaAdmissionState.ReleasedCleaned) !=
             (cleanupEvidence.Kind ==
                MedusaAdmissionCleanupKind.PreBarrierRelease)))
        {
            throw new InvalidDataException(
                "A durable Medusa cleanup kind disagrees with its outcome.");
        }

        var expectedRevision = 1L +
            (runtime is null ? 0 : 1) +
            (transferCommitted is null ? 0 : 1) +
            (consumed is null ? 0 : 1) +
            (terminal is null ? 0 : 1) +
            (released is null ? 0 : 1) +
            (cleanup is null ? 0 : 1);
        if (snapshot.Revision != expectedRevision)
        {
            throw new InvalidDataException(
                "A durable Medusa admission snapshot revision disagrees with its history.");
        }

        MedusaAdmissionReservationRequest reconstructed;
        try
        {
            reconstructed = new MedusaAdmissionReservationRequest(
                snapshot.AdmissionId,
                snapshot.WorldInstanceId,
                snapshot.RealmDay,
                snapshot.Difficulty,
                snapshot.Source,
                snapshot.Party,
                snapshot.EncounterContentFingerprint,
                snapshot.ReservedAtUtc);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A durable Medusa admission snapshot cannot reconstruct its request.",
                exception);
        }
        if (!string.Equals(
                snapshot.RequestHash,
                reconstructed.RequestHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A durable Medusa admission snapshot request hash is not exact.");
        }
    }

    public static void ValidateHash(string value, string parameterName)
    {
        if (value.Length != Sha256HexLength ||
            value.Any(static value => value is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "Medusa admission hashes must be uppercase SHA-256 hex.",
                parameterName);
        }
    }

    private static DateTimeOffset? CanonicalNullable(DateTimeOffset? value) =>
        value is null ? null : CanonicalUtc(value.Value, nameof(value));

    private sealed class CanonicalHashWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private bool _completed;

        public CanonicalHashWriter(string domain)
        {
            Write(domain);
        }

        public void Write(byte value) => _stream.WriteByte(value);

        public void Write(short value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Write(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Write(uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Write(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Write(Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) ||
                written != bytes.Length)
            {
                throw new InvalidOperationException("A GUID could not be encoded.");
            }
            _stream.Write(bytes);
        }

        public void Write(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = Encoding.UTF8.GetBytes(value);
            Write(bytes.Length);
            _stream.Write(bytes);
        }

        public string Complete()
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            _completed = true;
            return Convert.ToHexString(SHA256.HashData(_stream.GetBuffer()
                .AsSpan(0, checked((int)_stream.Length))));
        }

        public void Dispose()
        {
            _completed = true;
            _stream.Dispose();
        }
    }
}
