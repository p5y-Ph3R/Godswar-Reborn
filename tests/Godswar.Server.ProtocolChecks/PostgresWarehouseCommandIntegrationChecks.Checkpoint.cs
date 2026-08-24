using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Characters;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWarehouseCommandIntegrationChecks
{
    private static async Task AssertCheckpointLifecyclePreservesWarehouseAsync(
        string connectionString,
        NpgsqlDataSource dataSource)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "checkp",
            new ItemPlacement(3, 0, 7));
        var before = await ReadWarehouseRowJsonAsync(
            connectionString,
            fixture);
        await using var checkpoints =
            new PostgresCharacterCheckpointStore(dataSource);
        var acquired = await checkpoints.AcquireAsync(
            fixture.AccountId,
            fixture.CharacterId,
            Guid.NewGuid()) ??
            throw new InvalidOperationException(
                "Warehouse checkpoint fixture could not acquire ownership.");

        var position = await checkpoints.WritePositionAsync(
            new CharacterPositionCheckpoint(
                fixture.AccountId,
                fixture.CharacterId,
                acquired.Owner,
                1,
                12.5f,
                -9.25f,
                acquired.PositionRevision + 1));
        var vitals = await checkpoints.WriteVitalsAsync(
            new CharacterVitalsCheckpoint(
                fixture.AccountId,
                fixture.CharacterId,
                acquired.Owner,
                100,
                80,
                acquired.VitalsRevision + 1));
        var released = await checkpoints.ReleaseAsync(
            fixture.AccountId,
            fixture.CharacterId,
            acquired.Owner);

        Check.Equal(
            (int)CharacterCheckpointWriteStatus.Applied,
            (int)position.Status,
            "logout position checkpoint applies");
        Check.Equal(
            (int)CharacterCheckpointWriteStatus.Applied,
            (int)vitals.Status,
            "logout vitals checkpoint applies");
        Check.Equal(
            (int)CharacterCheckpointReleaseStatus.Released,
            (int)released,
            "logout checkpoint ownership releases");
        Check.Equal(
            before,
            await ReadWarehouseRowJsonAsync(connectionString, fixture),
            "checkpoint flush and release preserve the exact warehouse row");
    }

    private static async Task<string> ReadWarehouseRowJsonAsync(
        string connectionString,
        WarehouseFixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT to_jsonb(item_row)::text
            FROM public.character_items item_row
            WHERE user_id = @characterId
              AND item_location = 3
              AND slot_index = 0;
            """,
            connection);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        return await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException(
                "Warehouse checkpoint fixture row disappeared.");
    }
}
