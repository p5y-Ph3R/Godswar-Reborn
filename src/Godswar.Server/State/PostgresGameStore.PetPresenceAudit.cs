using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private static string ToPetPresenceAuditOperation(
        PetPresenceOperation operation) =>
        operation switch
        {
            PetPresenceOperation.Take => "take",
            PetPresenceOperation.CallOut => "summon",
            PetPresenceOperation.Recall => "dismiss",
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unknown pet-presence operation.")
        };

    private static async Task WritePetPresenceAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        int characterId,
        long petId,
        string operation,
        PetPresenceTransitionResult result,
        int? userId,
        long? referencedPetId,
        IReadOnlyList<PetPresenceRow>? before,
        IReadOnlyList<PetPresenceRow>? after,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pet_operation_audit (
                request_id,
                user_id,
                user_id_snapshot,
                pet_id,
                pet_id_snapshot,
                operation,
                outcome,
                before_state,
                after_state,
                reason_code
            )
            VALUES (
                @requestId,
                @userId,
                @userIdSnapshot,
                @petId,
                @petIdSnapshot,
                @operation,
                @outcome,
                @beforeState,
                @afterState,
                @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("requestId", requestId);
        command.Parameters.Add(
                "userId",
                NpgsqlDbType.Integer)
            .Value = (object?)userId ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "userIdSnapshot",
            characterId);
        command.Parameters.Add(
                "petId",
                NpgsqlDbType.Bigint)
            .Value = (object?)referencedPetId ?? DBNull.Value;
        command.Parameters.AddWithValue("petIdSnapshot", petId);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue(
            "outcome",
            result.Succeeded ? "committed" : "rejected");
        command.Parameters.Add(
                "beforeState",
                NpgsqlDbType.Jsonb)
            .Value = SerializePetPresenceAuditState(before)
                ?? (object)DBNull.Value;
        command.Parameters.Add(
                "afterState",
                NpgsqlDbType.Jsonb)
            .Value = SerializePetPresenceAuditState(after)
                ?? (object)DBNull.Value;
        command.Parameters.Add(
                "reasonCode",
                NpgsqlDbType.Varchar)
            .Value = result.Succeeded
                ? DBNull.Value
                : result.Status.ToString();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? SerializePetPresenceAuditState(
        IReadOnlyList<PetPresenceRow>? rows)
    {
        if (rows is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(
            rows.Select(static row => new PetPresenceAuditRow(
                row.PetId,
                row.ActivityState,
                row.IsCarried,
                row.IsSummoned,
                row.ContributesToCharacter)));
    }

    private sealed record PetPresenceAuditRow(
        long PetId,
        string ActivityState,
        bool IsCarried,
        bool IsSummoned,
        bool ContributesToCharacter);
}
