using System.Data;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

/// <summary>
/// Atomic completion settlement for the unwired Medusa foundation. The same
/// transaction terminalizes one ConsumedRunning admission, records its exact
/// best-only result, and grants ownership to every frozen roster member.
/// Construction never creates schema and this store never selects a title.
/// </summary>
internal sealed partial class PostgresMedusaTitleAwardStore :
    IMedusaTitleAwardStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMedusaTitleAwardStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<MedusaTitleSettlementReceipt> SettleCompletionAsync(
        MedusaTitleSettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var admission = await ReadAdmissionAsync(
            connection,
            transaction,
            request.AdmissionId,
            lockRow: true,
            cancellationToken);
        if (admission is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Receipt(MedusaTitleSettlementStatus.NotFound, request, null);
        }

        var roster = await ReadRosterAsync(
            connection,
            transaction,
            request.AdmissionId,
            cancellationToken);
        var existing = await ReadSettlementRowAsync(
            connection,
            transaction,
            request.AdmissionId,
            cancellationToken);
        if (existing is not null)
        {
            RequireSettlementAdmissionCoherence(admission, existing);
            var snapshot = CreateSnapshot(existing, roster);
            var status = existing.OperationId == request.OperationId &&
                string.Equals(
                    existing.RequestHash,
                    request.RequestHash,
                    StringComparison.Ordinal)
                ? MedusaTitleSettlementStatus.Duplicate
                : MedusaTitleSettlementStatus.RequestConflict;
            await transaction.CommitAsync(cancellationToken);
            return Receipt(status, request, snapshot);
        }

        if (admission.State != MedusaAdmissionState.ConsumedRunning)
        {
            await transaction.CommitAsync(cancellationToken);
            return Receipt(
                MedusaTitleSettlementStatus.TerminalConflict,
                request,
                null);
        }
        if (!AdmissionEvidenceMatches(admission, roster, request))
        {
            await transaction.CommitAsync(cancellationToken);
            return Receipt(
                MedusaTitleSettlementStatus.AdmissionEvidenceConflict,
                request,
                null);
        }

        var completionTransition = new MedusaAdmissionTransitionRequest(
            request.OperationId,
            request.AdmissionId,
            MedusaAdmissionState.ConsumedRunning,
            MedusaAdmissionState.Completed,
            request.CompletedAtUtc);

        MedusaTitleSemanticKey? title = null;
        if (MedusaTitleAwardPolicy.TryResolveBestAward(
                request.Difficulty,
                request.FinalScore,
                request.Elapsed,
                out var definition))
        {
            title = definition.SemanticKey;
        }

        await InsertSettlementAsync(
            connection,
            transaction,
            request,
            admission.ContentMapId,
            title,
            cancellationToken);
        if (title is { } awardedTitle)
        {
            await GrantFrozenRosterAsync(
                connection,
                transaction,
                request,
                awardedTitle,
                cancellationToken);
            await RequireCompleteOwnershipAsync(
                connection,
                transaction,
                request,
                awardedTitle,
                cancellationToken);
        }
        await TerminalizeAdmissionAsync(
            connection,
            transaction,
            completionTransition,
            admission.Revision,
            cancellationToken);
        await InsertTransitionReceiptAsync(
            connection,
            transaction,
            completionTransition,
            checked(admission.Revision + 1),
            cancellationToken);

        var applied = new MedusaTitleSettlementSnapshot(
            request.AdmissionId,
            request.OperationId,
            request.WorldInstanceId,
            request.Difficulty,
            admission.ContentMapId,
            request.EncounterContentFingerprint,
            request.RosterHash,
            request.AdmissionRequestHash,
            request.CompletedAtUtc,
            request.Elapsed,
            request.FinalScore,
            request.RequestHash,
            title,
            roster);
        await transaction.CommitAsync(cancellationToken);
        return Receipt(MedusaTitleSettlementStatus.Applied, request, applied);
    }

    private static bool AdmissionEvidenceMatches(
        LockedAdmission admission,
        IReadOnlyList<MedusaTitleSettlementMember> roster,
        MedusaTitleSettlementRequest request) =>
        admission.Revision == 4 &&
        admission.WorldInstanceId == request.WorldInstanceId &&
        admission.Difficulty == request.Difficulty &&
        admission.ContentMapId == request.ContentMapId &&
        string.Equals(
            admission.EncounterContentFingerprint,
            request.EncounterContentFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(
            admission.RosterHash,
            request.RosterHash,
            StringComparison.Ordinal) &&
        string.Equals(
            admission.RequestHash,
            request.AdmissionRequestHash,
            StringComparison.Ordinal) &&
        roster.SequenceEqual(request.FrozenMembers) &&
        admission.ConsumedAtUtc is { } startedAt &&
        request.CompletedAtUtc >= startedAt &&
        request.CompletedAtUtc - startedAt == request.Elapsed;

    private static void RequireSettlementAdmissionCoherence(
        LockedAdmission admission,
        SettlementRow settlement) =>
        RequireSettlementAdmissionCoherence(
            admission,
            new SettlementCoherenceEvidence(
                settlement.WorldInstanceId,
                settlement.Difficulty,
                settlement.ContentMapId,
                settlement.EncounterContentFingerprint,
                settlement.RosterHash,
                settlement.AdmissionRequestHash,
                settlement.CompletedAtUtc,
                admission.ConsumedAtUtc,
                settlement.ElapsedMicroseconds));

    private static void RequireSettlementAdmissionCoherence(
        LockedAdmission admission,
        SettlementCoherenceEvidence settlement)
    {
        if (settlement.ElapsedMicroseconds < 0 ||
            settlement.ElapsedMicroseconds >
                TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMicrosecond)
        {
            throw new InvalidDataException(
                "A Medusa title settlement has invalid elapsed time.");
        }
        var elapsed = TimeSpan.FromTicks(checked(
            settlement.ElapsedMicroseconds * TimeSpan.TicksPerMicrosecond));
        var expectedRevision = admission.State switch
        {
            MedusaAdmissionState.Completed => 5,
            MedusaAdmissionState.CompletedCleaned => 6,
            _ => 0
        };
        if (admission.Revision != expectedRevision ||
            admission.ConsumedAtUtc != settlement.AdmissionConsumedAtUtc ||
            settlement.AdmissionConsumedAtUtc is not { } consumedAt ||
            consumedAt.Offset != TimeSpan.Zero ||
            consumedAt.UtcTicks % TimeSpan.TicksPerMicrosecond != 0 ||
            settlement.CompletedAtUtc.Offset != TimeSpan.Zero ||
            settlement.CompletedAtUtc.UtcTicks %
                TimeSpan.TicksPerMicrosecond != 0 ||
            settlement.CompletedAtUtc < consumedAt ||
            settlement.CompletedAtUtc - consumedAt != elapsed ||
            admission.TerminalAtUtc != settlement.CompletedAtUtc ||
            admission.WorldInstanceId != settlement.WorldInstanceId ||
            admission.Difficulty != settlement.Difficulty ||
            admission.ContentMapId != settlement.ContentMapId ||
            !string.Equals(
                admission.EncounterContentFingerprint,
                settlement.EncounterContentFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                admission.RosterHash,
                settlement.RosterHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                admission.RequestHash,
                settlement.AdmissionRequestHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A Medusa title settlement disagrees with its terminal admission.");
        }
    }

    private static MedusaTitleSettlementReceipt Receipt(
        MedusaTitleSettlementStatus status,
        MedusaTitleSettlementRequest request,
        MedusaTitleSettlementSnapshot? snapshot) =>
        new(status, request.AdmissionId, snapshot);

    private sealed record LockedAdmission(
        WorldInstanceId WorldInstanceId,
        MedusaEncounterDifficulty Difficulty,
        MapId ContentMapId,
        string EncounterContentFingerprint,
        string RosterHash,
        string RequestHash,
        MedusaAdmissionState State,
        long Revision,
        DateTimeOffset? ConsumedAtUtc,
        DateTimeOffset? TerminalAtUtc);

    private sealed record SettlementRow(
        MedusaAdmissionId AdmissionId,
        Guid OperationId,
        WorldInstanceId WorldInstanceId,
        MedusaEncounterDifficulty Difficulty,
        MapId ContentMapId,
        string EncounterContentFingerprint,
        string RosterHash,
        string AdmissionRequestHash,
        DateTimeOffset CompletedAtUtc,
        long ElapsedMicroseconds,
        int FinalScore,
        string RequestHash,
        string? TitleKey);

    private sealed record SettlementCoherenceEvidence(
        WorldInstanceId WorldInstanceId,
        MedusaEncounterDifficulty Difficulty,
        MapId ContentMapId,
        string EncounterContentFingerprint,
        string RosterHash,
        string AdmissionRequestHash,
        DateTimeOffset CompletedAtUtc,
        DateTimeOffset? AdmissionConsumedAtUtc,
        long ElapsedMicroseconds);
}
