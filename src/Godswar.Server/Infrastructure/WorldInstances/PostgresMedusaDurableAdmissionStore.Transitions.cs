using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresMedusaDurableAdmissionStore
{
    public async Task<MedusaAdmissionReceipt> TransitionAsync(
        MedusaAdmissionTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var current = await ReadSnapshotAsync(
            connection,
            transaction,
            request.AdmissionId,
            lockAdmission: true,
            cancellationToken);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MedusaAdmissionReceipt(
                MedusaAdmissionReceiptStatus.NotFound,
                request.AdmissionId,
                null,
                null,
                null);
        }

        var replay = await ReadTransitionReceiptAsync(
            connection,
            transaction,
            request,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return string.Equals(
                replay.RequestHash,
                request.RequestHash,
                StringComparison.Ordinal)
                ? Duplicate(
                    current,
                    replay.TargetState,
                    replay.ResultingRevision)
                : new MedusaAdmissionReceipt(
                    MedusaAdmissionReceiptStatus.RequestConflict,
                    request.AdmissionId,
                    null,
                    null,
                    current);
        }

        if (current.State != request.ExpectedState ||
            !MedusaDurableAdmissionPolicy.IsAllowedTransition(
                current.State,
                request.TargetState) ||
            request.OccurredAtUtc < current.LastChangedAtUtc)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MedusaAdmissionReceipt(
                MedusaAdmissionReceiptStatus.InvalidTransition,
                request.AdmissionId,
                null,
                null,
                current);
        }

        if (request.TargetState == MedusaAdmissionState.ConsumedRunning)
        {
            await ConsumeClaimsAsync(
                connection,
                transaction,
                request,
                current.Party.Members.Length,
                cancellationToken);
        }
        else if (request.TargetState == MedusaAdmissionState.Released)
        {
            await ReleaseClaimsAsync(
                connection,
                transaction,
                request.AdmissionId,
                current.Party.Members.Length,
                cancellationToken);
        }
        if (MedusaDurableAdmissionPolicy.IsCleanupCompletedState(
                request.TargetState))
        {
            await ReleaseActiveMemberClaimsAsync(
                connection,
                transaction,
                request.AdmissionId,
                current.Party.Members.Length,
                cancellationToken);
        }

        var updated = ApplyTransition(current, request);
        await UpdateAdmissionAsync(
            connection,
            transaction,
            request,
            current.Revision,
            cancellationToken);
        await InsertTransitionReceiptAsync(
            connection,
            transaction,
            request,
            updated.Revision,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Applied(updated);
    }

    private static MedusaAdmissionSnapshot ApplyTransition(
        MedusaAdmissionSnapshot current,
        MedusaAdmissionTransitionRequest request)
    {
        var runtimeReady = current.RuntimeReadyAtUtc;
        var rosterTransferCommitted = current.RosterTransferCommittedAtUtc;
        var consumed = current.ConsumedAtUtc;
        var terminal = current.TerminalAtUtc;
        DateTimeOffset? released = current.ReleasedAtUtc;
        var barrierEvidence = current.BarrierEvidence;
        var cleanupEvidence = current.CleanupEvidence;
        var cleanupCompleted = current.CleanupCompletedAtUtc;
        switch (request.TargetState)
        {
            case MedusaAdmissionState.RuntimeReady:
                runtimeReady = request.OccurredAtUtc;
                break;
            case MedusaAdmissionState.RosterTransferCommitted:
                rosterTransferCommitted = request.OccurredAtUtc;
                barrierEvidence = request.BarrierEvidence;
                break;
            case MedusaAdmissionState.ConsumedRunning:
                consumed = request.OccurredAtUtc;
                break;
            case MedusaAdmissionState.Completed:
            case MedusaAdmissionState.Abandoned:
            case MedusaAdmissionState.TimedOut:
                terminal = request.OccurredAtUtc;
                break;
            case MedusaAdmissionState.Released:
                released = request.OccurredAtUtc;
                break;
            case MedusaAdmissionState.CompletedCleaned:
            case MedusaAdmissionState.AbandonedCleaned:
            case MedusaAdmissionState.TimedOutCleaned:
            case MedusaAdmissionState.ReleasedCleaned:
                cleanupEvidence = request.CleanupEvidence;
                cleanupCompleted = request.OccurredAtUtc;
                break;
            default:
                throw new InvalidOperationException(
                    "The transition target is not a mutable admission state.");
        }

        return new MedusaAdmissionSnapshot(
            current.AdmissionId,
            current.WorldInstanceId,
            current.RealmDay,
            current.Difficulty,
            current.ContentMapId,
            current.Source,
            current.Party,
            current.EncounterContentFingerprint,
            current.RosterHash,
            current.RequestHash,
            request.TargetState,
            checked(current.Revision + 1),
            barrierEvidence,
            current.ReservedAtUtc,
            runtimeReady,
            rosterTransferCommitted,
            consumed,
            terminal,
            released,
            cleanupEvidence,
            cleanupCompleted);
    }

    private static async Task UpdateAdmissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionTransitionRequest request,
        long currentRevision,
        CancellationToken cancellationToken)
    {
        var timestampColumn = request.TargetState switch
        {
            MedusaAdmissionState.RuntimeReady => "runtime_ready_at",
            MedusaAdmissionState.RosterTransferCommitted =>
                "roster_transfer_committed_at",
            MedusaAdmissionState.ConsumedRunning => "consumed_at",
            MedusaAdmissionState.Completed or
                MedusaAdmissionState.Abandoned or
                MedusaAdmissionState.TimedOut => "terminal_at",
            MedusaAdmissionState.Released => "released_at",
            MedusaAdmissionState.CompletedCleaned or
                MedusaAdmissionState.AbandonedCleaned or
                MedusaAdmissionState.TimedOutCleaned or
                MedusaAdmissionState.ReleasedCleaned =>
                "cleanup_completed_at",
            _ => throw new InvalidOperationException(
                "The transition target has no durable timestamp column.")
        };
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE medusa_admission_foundation.admissions
            SET state = @targetState,
                revision = revision + 1,
                {timestampColumn} = @occurredAt
                {(request.BarrierEvidence is null ? string.Empty :
                    ", roster_transfer_stage_id = @stageId, " +
                    "roster_transfer_preparation_hash = @preparationHash")}
                {(request.CleanupEvidence is null ? string.Empty :
                    ", cleanup_kind = @cleanupKind, " +
                    "cleanup_roster_operation_id = @rosterOperationId, " +
                    "cleanup_runtime_operation_id = @runtimeOperationId")}
            WHERE admission_id = @admissionId
              AND state = @expectedState
              AND revision = @currentRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "targetState",
            (short)request.TargetState);
        command.Parameters.AddWithValue(
            "expectedState",
            (short)request.ExpectedState);
        command.Parameters.AddWithValue("currentRevision", currentRevision);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        AddTimestamp(command, "occurredAt", request.OccurredAtUtc);
        if (request.BarrierEvidence is { } barrier)
        {
            command.Parameters.AddWithValue("stageId", barrier.StageId);
            command.Parameters.AddWithValue(
                "preparationHash",
                barrier.PreparationHash);
        }
        if (request.CleanupEvidence is { } cleanup)
        {
            command.Parameters.AddWithValue(
                "cleanupKind",
                (short)cleanup.Kind);
            command.Parameters.AddWithValue(
                "rosterOperationId",
                cleanup.RosterOperationId);
            command.Parameters.AddWithValue(
                "runtimeOperationId",
                cleanup.RuntimeOperationId);
        }
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "A locked Medusa admission changed during its transition.");
        }
    }

    private static async Task ConsumeClaimsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionTransitionRequest request,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE medusa_admission_foundation.attempt_claims
            SET claim_state = 2,
                consumed_at = @consumedAt
            WHERE admission_id = @admissionId
              AND claim_state = 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        AddTimestamp(command, "consumedAt", request.OccurredAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != expectedCount)
        {
            throw new InvalidDataException(
                "A Medusa admission could not consume its complete frozen roster.");
        }
    }

    private static async Task ReleaseClaimsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionId admissionId,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM medusa_admission_foundation.attempt_claims
            WHERE admission_id = @admissionId
              AND claim_state = 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", admissionId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != expectedCount)
        {
            throw new InvalidDataException(
                "A Medusa admission could not release its complete reserved roster.");
        }
    }

    private static async Task ReleaseActiveMemberClaimsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionId admissionId,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM medusa_admission_foundation.active_member_claims
            WHERE admission_id = @admissionId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", admissionId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != expectedCount)
        {
            throw new InvalidDataException(
                "A Medusa admission could not release its complete active roster.");
        }
    }

    private static async Task<TransitionReceiptRow?>
        ReadTransitionReceiptAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            MedusaAdmissionTransitionRequest request,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT request_hash, target_state, resulting_revision
            FROM medusa_admission_foundation.transition_receipts
            WHERE admission_id = @admissionId
              AND transition_id = @transitionId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        command.Parameters.AddWithValue("transitionId", request.TransitionId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TransitionReceiptRow(
                reader.GetString(0),
                checked((MedusaAdmissionState)reader.GetInt16(1)),
                reader.GetInt64(2))
            : null;
    }

    private static async Task InsertTransitionReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionTransitionRequest request,
        long resultingRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO medusa_admission_foundation.transition_receipts (
                transition_id, admission_id, request_hash,
                expected_state, target_state, resulting_revision, occurred_at)
            VALUES (
                @transitionId, @admissionId, @requestHash,
                @expectedState, @targetState, @resultingRevision, @occurredAt);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("transitionId", request.TransitionId);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        command.Parameters.AddWithValue("requestHash", request.RequestHash);
        command.Parameters.AddWithValue(
            "expectedState",
            (short)request.ExpectedState);
        command.Parameters.AddWithValue("targetState", (short)request.TargetState);
        command.Parameters.AddWithValue("resultingRevision", resultingRevision);
        command.Parameters.Add(
            "occurredAt",
            NpgsqlDbType.TimestampTz).Value = request.OccurredAtUtc.UtcDateTime;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "A Medusa admission transition receipt was not inserted.");
        }
    }

    private sealed record TransitionReceiptRow(
        string RequestHash,
        MedusaAdmissionState TargetState,
        long ResultingRevision);
}
