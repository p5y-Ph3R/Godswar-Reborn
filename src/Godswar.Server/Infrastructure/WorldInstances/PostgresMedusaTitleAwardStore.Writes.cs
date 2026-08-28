using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresMedusaTitleAwardStore
{
    private static async Task InsertSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaTitleSettlementRequest request,
        MapId contentMapId,
        MedusaTitleSemanticKey? title,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO medusa_admission_foundation.medusa_completion_settlements (
                admission_id, completion_operation_id, world_instance_id,
                difficulty, content_map_id,
                encounter_content_fingerprint, roster_hash,
                admission_request_hash, completed_at,
                elapsed_microseconds, final_score, request_hash, title_key)
            VALUES (
                @admissionId, @operationId, @worldInstanceId,
                @difficulty, @contentMapId,
                @encounterContentFingerprint, @rosterHash,
                @admissionRequestHash, @completedAt,
                @elapsedMicroseconds, @finalScore, @requestHash, @titleKey);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        command.Parameters.AddWithValue("operationId", request.OperationId);
        command.Parameters.AddWithValue(
            "worldInstanceId",
            request.WorldInstanceId.Value);
        command.Parameters.AddWithValue("difficulty", (short)request.Difficulty);
        command.Parameters.AddWithValue("contentMapId", contentMapId.Value);
        command.Parameters.AddWithValue(
            "encounterContentFingerprint",
            request.EncounterContentFingerprint);
        command.Parameters.AddWithValue("rosterHash", request.RosterHash);
        command.Parameters.AddWithValue(
            "admissionRequestHash",
            request.AdmissionRequestHash);
        AddTimestamp(command, "completedAt", request.CompletedAtUtc);
        command.Parameters.AddWithValue(
            "elapsedMicroseconds",
            request.Elapsed.Ticks / TimeSpan.TicksPerMicrosecond);
        command.Parameters.AddWithValue("finalScore", request.FinalScore);
        command.Parameters.AddWithValue("requestHash", request.RequestHash);
        command.Parameters.Add(
            "titleKey",
            NpgsqlDbType.Varchar).Value = title?.Value ?? (object)DBNull.Value;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "A Medusa completion settlement was not inserted.");
        }
    }

    private static async Task GrantFrozenRosterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaTitleSettlementRequest request,
        MedusaTitleSemanticKey title,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO medusa_admission_foundation.character_title_ownership (
                character_id, title_key, source_admission_id,
                source_completion_operation_id, acquired_at)
            SELECT member.character_id, @titleKey, member.admission_id,
                   @operationId, @acquiredAt
            FROM medusa_admission_foundation.members AS member
            WHERE member.admission_id = @admissionId
            ORDER BY member.character_id
            ON CONFLICT (character_id, title_key) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("titleKey", title.Value!);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        command.Parameters.AddWithValue("operationId", request.OperationId);
        AddTimestamp(command, "acquiredAt", request.CompletedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RequireCompleteOwnershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaTitleSettlementRequest request,
        MedusaTitleSemanticKey title,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM medusa_admission_foundation.character_title_ownership
            WHERE title_key = @titleKey
              AND character_id = ANY(@characterIds);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("titleKey", title.Value!);
        command.Parameters.AddWithValue(
            "characterIds",
            request.FrozenCharacterIds.ToArray());
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (count != request.FrozenCharacterIds.Count)
        {
            throw new InvalidDataException(
                "Medusa title ownership did not cover the complete frozen roster.");
        }
    }

    private static async Task TerminalizeAdmissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionTransitionRequest transition,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE medusa_admission_foundation.admissions
            SET state = 5,
                revision = revision + 1,
                terminal_at = @completedAt
            WHERE admission_id = @admissionId
              AND state = 4
              AND revision = @expectedRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "admissionId",
            transition.AdmissionId.Value);
        command.Parameters.AddWithValue("expectedRevision", expectedRevision);
        AddTimestamp(command, "completedAt", transition.OccurredAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The locked Medusa admission could not be terminalized.");
        }
    }

    private static async Task InsertTransitionReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionTransitionRequest transition,
        long resultingRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO medusa_admission_foundation.transition_receipts (
                transition_id, admission_id, request_hash,
                expected_state, target_state, resulting_revision, occurred_at)
            VALUES (
                @operationId, @admissionId, @requestHash,
                4, 5, @resultingRevision, @completedAt);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("operationId", transition.TransitionId);
        command.Parameters.AddWithValue(
            "admissionId",
            transition.AdmissionId.Value);
        command.Parameters.AddWithValue("requestHash", transition.RequestHash);
        command.Parameters.AddWithValue("resultingRevision", resultingRevision);
        AddTimestamp(command, "completedAt", transition.OccurredAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Medusa completion transition receipt was not inserted.");
        }
    }

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value.UtcDateTime;
}
