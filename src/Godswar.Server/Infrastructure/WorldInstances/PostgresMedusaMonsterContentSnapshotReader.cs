using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed class PostgresMedusaMonsterContentSnapshotReader(
    NpgsqlDataSource dataSource)
{
    private const int MaximumMonsterRows = 256;
    private const int MaximumLootRows = 512;
    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));

    public static async Task<MedusaMonsterContentSnapshot> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await new PostgresMedusaMonsterContentSnapshotReader(
            dataSource).ReadAsync(cancellationToken);
    }

    public async Task<MedusaMonsterContentSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        var monsters = await ReadMonstersAsync(
            connection,
            transaction,
            cancellationToken);
        var loot = await ReadLootAsync(
            connection,
            transaction,
            cancellationToken);
        var snapshot = new MedusaMonsterContentSnapshot(monsters, loot);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static async Task<IReadOnlyList<MedusaMonsterRule>>
        ReadMonstersAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT difficulty, template_alias, monster_level,
                   maximum_health, score, movement_speed_basis_points,
                   corpse_without_loot_ms, corpse_with_loot_ms,
                   pet_experience
            FROM public.medusa_monster_rules
            WHERE enabled
            ORDER BY difficulty, template_alias
            LIMIT @maximumRows;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("maximumRows", MaximumMonsterRows);
        var result = new List<MedusaMonsterRule>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                (MedusaEncounterDifficulty)reader.GetInt16(0),
                reader.GetString(1),
                checked((uint)reader.GetInt16(2)),
                checked((uint)reader.GetInt32(3)),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetInt32(8)));
        }
        return result.AsReadOnly();
    }

    private static async Task<IReadOnlyList<MedusaMonsterLootRule>>
        ReadLootAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT difficulty, template_alias, loot_index, item_id,
                   chance_basis_points, minimum_quantity, maximum_quantity
            FROM public.medusa_monster_loot_rules
            WHERE enabled
            ORDER BY difficulty, template_alias, loot_index
            LIMIT @maximumRows;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("maximumRows", MaximumLootRows);
        var result = new List<MedusaMonsterLootRule>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                (MedusaEncounterDifficulty)reader.GetInt16(0),
                reader.GetString(1),
                reader.GetInt16(2),
                checked((uint)reader.GetInt32(3)),
                reader.GetInt32(4),
                reader.GetInt16(5),
                reader.GetInt16(6)));
        }
        return result.AsReadOnly();
    }
}
