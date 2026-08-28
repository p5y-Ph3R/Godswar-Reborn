using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMedusaDurableAdmissionStoreChecks
{
    private static MedusaRosterTransferBarrierEvidence Barrier() =>
        new(
            Guid.NewGuid(),
            new string('B', MedusaDurableAdmissionPolicy.Sha256HexLength));

    private static MedusaAdmissionCleanupEvidence CleanupEvidence(
        MedusaAdmissionId admissionId,
        MedusaAdmissionCleanupKind kind) =>
        kind == MedusaAdmissionCleanupKind.PreBarrierRelease
            ? new MedusaAdmissionCleanupEvidence(
                admissionId,
                kind,
                MedusaAdmissionSagaOperationIds.TransferAbort(admissionId),
                MedusaAdmissionSagaOperationIds.RuntimeRelease(admissionId))
            : new MedusaAdmissionCleanupEvidence(
                admissionId,
                kind,
                MedusaAdmissionSagaOperationIds.RosterEgress(admissionId),
                MedusaAdmissionSagaOperationIds.RuntimeRetire(admissionId));

    private static async Task<MedusaAdmissionId> FindByCharacterAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT admission_id
            FROM medusa_admission_foundation.attempt_claims
            WHERE character_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        return new MedusaAdmissionId(
            (Guid)(await command.ExecuteScalarAsync() ??
                throw new InvalidDataException("Admission fixture is missing.")));
    }

    private static async Task<long> CountClaimsAsync(
        NpgsqlDataSource dataSource,
        MedusaAdmissionId admissionId,
        short claimState)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT COUNT(*)
            FROM medusa_admission_foundation.attempt_claims
            WHERE admission_id = @admissionId AND claim_state = @claimState;
            """);
        command.Parameters.AddWithValue("admissionId", admissionId.Value);
        command.Parameters.AddWithValue("claimState", claimState);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> CountClaimsForCharactersAsync(
        NpgsqlDataSource dataSource,
        params int[] characterIds)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT COUNT(*)
            FROM medusa_admission_foundation.attempt_claims
            WHERE character_id = ANY(@characterIds);
            """);
        command.Parameters.AddWithValue("characterIds", characterIds);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task DeleteClaimAsync(
        NpgsqlDataSource dataSource,
        MedusaAdmissionId admissionId,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            DELETE FROM medusa_admission_foundation.attempt_claims
            WHERE admission_id = @admissionId AND character_id = @characterId;
            """);
        command.Parameters.AddWithValue("admissionId", admissionId.Value);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "corruption fixture removes one claim");
    }

    private static async Task AssertCleanupKindConstraintAsync(
        NpgsqlDataSource dataSource,
        MedusaAdmissionId admissionId) =>
        await AssertThrowsAsync<PostgresException>(
            async () =>
            {
                await using var command = dataSource.CreateCommand(
                    """
                    UPDATE medusa_admission_foundation.admissions
                    SET state = 9,
                        revision = revision + 1,
                        cleanup_kind = 1,
                        cleanup_roster_operation_id = @rosterOperationId,
                        cleanup_runtime_operation_id = @runtimeOperationId,
                        cleanup_completed_at = terminal_at
                    WHERE admission_id = @admissionId;
                    """);
                command.Parameters.AddWithValue(
                    "admissionId",
                    admissionId.Value);
                command.Parameters.AddWithValue(
                    "rosterOperationId",
                    Guid.NewGuid());
                command.Parameters.AddWithValue(
                    "runtimeOperationId",
                    Guid.NewGuid());
                await command.ExecuteNonQueryAsync();
            },
            "terminal cleanup rejects pre-barrier cleanup evidence kind");

    private static async Task<string> ReadDatabaseAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException("PostgreSQL returned no database name.");
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {typeof(TException).Name}.");
    }

    private static void CheckStatus(
        MedusaAdmissionReceiptStatus expected,
        MedusaAdmissionReceiptStatus actual,
        string description) =>
        Check.True(
            expected == actual,
            $"{description}: expected {expected}, actual {actual}");

    private static void CheckState(
        MedusaAdmissionState expected,
        MedusaAdmissionState actual,
        string description) =>
        Check.True(
            expected == actual,
            $"{description}: expected {expected}, actual {actual}");
}
