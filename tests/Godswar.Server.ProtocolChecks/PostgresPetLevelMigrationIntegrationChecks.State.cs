using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetLevelMigrationIntegrationChecks
{
    private static async Task<DatabaseState> ReadDatabaseStateAsync(
        string connectionString)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return new DatabaseState(
            await ReadConstraintDefinitionAsync(
                connection,
                transaction: null),
            await ReadOpcodeStateAsync(connection));
    }

    private static async Task<string> ReadConstraintDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_constraintdef(constraint_row.oid)
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'public.pet_operation_audit'::regclass
              AND constraint_row.conname =
                    'pet_operation_audit_operation_check';
            """,
            connection,
            transaction);
        return (string)(await command.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException(
                            "Pet operation constraint is missing."));
    }

    private static async Task<string> ReadOpcodeStateAsync(
        NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(
                jsonb_agg(to_jsonb(opcode_row) ORDER BY opcode, direction),
                '[]'::jsonb
            )::text
            FROM public.packet_opcodes opcode_row
            WHERE opcode IN (10285, 10286);
            """,
            connection);
        return (string)(await command.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException(
                            "Pet opcode snapshot returned null."));
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        string connectionString,
        string migrationId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.schema_migrations
                WHERE migration_id = @migrationId
            );
            """,
            connection);
        command.Parameters.AddWithValue(
            "migrationId",
            migrationId);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Migration-presence check returned null."));
    }

    private static async Task<long> CountFixtureAccountsAsync(
        string connectionString,
        string username)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM public.accounts
            WHERE username = @username;
            """,
            connection);
        command.Parameters.AddWithValue("username", username);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Fixture-cleanup check returned null."));
    }

    private sealed record DatabaseState(
        string ConstraintDefinition,
        string OpcodeState);
}
