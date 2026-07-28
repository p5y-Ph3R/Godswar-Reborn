using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetLevelMigrationIntegrationChecks
{
    private static async Task AssertOperationConstraintAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationFixture fixture)
    {
        var definition = await ReadConstraintDefinitionAsync(
            connection,
            transaction);
        Check.True(
            definition.Contains("'level_up'", StringComparison.Ordinal) &&
            definition.Contains("'hatch'", StringComparison.Ordinal) &&
            definition.Contains("'take'", StringComparison.Ordinal),
            "migration permits level_up without dropping prior operations");

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.pet_operation_audit (
                request_id,
                user_id,
                user_id_snapshot,
                pet_id,
                pet_id_snapshot,
                operation,
                outcome,
                reason_code
            )
            VALUES (
                @requestId,
                @characterId,
                @characterId,
                @petId,
                @petId,
                'level_up',
                'rejected',
                'integration_probe'
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("requestId", Guid.NewGuid());
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue("petId", fixture.PetId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "migration accepts a level_up audit row");
    }

    private static async Task AssertOpcodeMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT opcode, direction, name, category, confidence
            FROM public.packet_opcodes
            WHERE (opcode = 10285 AND direction = 'C2S')
               OR (opcode = 10286 AND direction = 'S2C')
            ORDER BY opcode;
            """,
            connection,
            transaction);
        var rows = new List<OpcodeMetadata>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OpcodeMetadata(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        Check.Equal(2, rows.Count, "migration registers two pet opcodes");
        Check.Equal(
            new OpcodeMetadata(
                10285,
                "C2S",
                "PetLevelUpgradeRequest",
                "pets",
                "known"),
            rows[0],
            "native pet level-up request metadata");
        Check.Equal(
            new OpcodeMetadata(
                10286,
                "S2C",
                "PetLevelUpgrade",
                "pets",
                "known"),
            rows[1],
            "native pet level-up response metadata");
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record OpcodeMetadata(
        int Opcode,
        string Direction,
        string Name,
        string Category,
        string Confidence);
}
