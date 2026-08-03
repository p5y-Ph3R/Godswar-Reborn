using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private static async Task<bool> RelationExistsAsync(
        NpgsqlConnection connection,
        string qualifiedName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualifiedName) IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue(
            "qualifiedName",
            qualifiedName);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Relation check returned null."));
    }

    private static async Task<int> ReadInt32Async(
        NpgsqlConnection connection,
        string sql) =>
        Convert.ToInt32(
            await ReadScalarAsync(connection, sql));

    private static async Task<bool> ReadBooleanAsync(
        NpgsqlConnection connection,
        string sql) =>
        Convert.ToBoolean(
            await ReadScalarAsync(connection, sql));

    private static async Task<string> ReadTextAsync(
        NpgsqlConnection connection,
        string sql) =>
        Convert.ToString(
            await ReadScalarAsync(connection, sql))
        ?? throw new InvalidOperationException(
            "Text query returned null.");

    private static async Task<object> ReadScalarAsync(
        NpgsqlConnection connection,
        string sql)
    {
        await using var command =
            new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync()
               ?? throw new InvalidOperationException(
                   "Scalar query returned null.");
    }

    private sealed record SchemaReleaseSnapshot(
        int CoreMarkerCount,
        IReadOnlyList<AppliedPostgresSchemaMigration>
            AppliedMigrations,
        string? InventoryFingerprint,
        IReadOnlyList<InventoryRowSnapshot>? InventoryRows,
        string? AccountCharacterFingerprint,
        string? CheckpointFingerprint,
        string? LifecycleFingerprint,
        string? PacketPayloadFingerprint,
        string? PetFingerprint,
        string? EconomyFingerprint,
        int PacketRelationCount,
        bool HasOpcodeNameFunction,
        int OpcodeNameTriggerCount,
        int PacketCaptureForeignKeyCount,
        int CheckpointColumnCount,
        int CheckpointConstraintCount,
        int LifecycleColumnCount,
        int LifecycleConstraintCount,
        int LifecycleIndexCount,
        int AccountLifecycleColumnCount,
        int AccountLifecycleConstraintCount,
        int ClassSuitAttributeColumnCount,
        int UnvalidatedConstraintCount,
        int InvalidIndexCount,
        string ReleaseFingerprint);

    private sealed record InventoryRowSnapshot(
        long Id,
        string StateJson);
}
