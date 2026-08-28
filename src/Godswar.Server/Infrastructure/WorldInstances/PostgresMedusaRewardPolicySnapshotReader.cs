using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

/// <summary>
/// Loads one repeatable-read snapshot of the adjustable Medusa completion
/// rewards. Schema creation remains exclusively migration-owned.
/// </summary>
internal sealed class PostgresMedusaRewardPolicySnapshotReader
{
    private const int MaximumTitles = 7;
    private const int MaximumRules = 101;
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMedusaRewardPolicySnapshotReader(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
    }

    public static async Task<MedusaRewardPolicySnapshot> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await new PostgresMedusaRewardPolicySnapshotReader(dataSource)
            .ReadAsync(cancellationToken);
    }

    public async Task<MedusaRewardPolicySnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var titles = await ReadTitlesAsync(
            connection,
            transaction,
            cancellationToken);
        var rules = await ReadRulesAsync(
            connection,
            transaction,
            cancellationToken);
        var snapshot = new MedusaRewardPolicySnapshot(titles, rules);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static async Task<IReadOnlyList<MedusaTitleDefinition>>
        ReadTitlesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT title, semantic_key, display_name, client_title_id,
                   physical_attack_basis_points,
                   magic_attack_basis_points,
                   physical_defense_basis_points,
                   magic_defense_basis_points
            FROM public.medusa_reward_title_definitions
            ORDER BY title
            LIMIT @maximumRows;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("maximumRows", MaximumTitles);
        var titles = new List<MedusaTitleDefinition>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            titles.Add(new(
                (MedusaEncounterTitle)reader.GetInt16(0),
                new MedusaTitleSemanticKey(reader.GetString(1)),
                reader.GetString(2),
                checked((uint)reader.GetInt32(3)),
                new(
                    reader.GetInt16(4),
                    reader.GetInt16(5),
                    reader.GetInt16(6),
                    reader.GetInt16(7))));
        }
        return titles.AsReadOnly();
    }

    private static async Task<IReadOnlyList<MedusaRewardRule>>
        ReadRulesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT difficulty, reward_kind, threshold, honor_points, title
            FROM public.medusa_completion_reward_rules
            ORDER BY difficulty, reward_kind, threshold
            LIMIT @maximumRows;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("maximumRows", MaximumRules);
        var rules = new List<MedusaRewardRule>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new(
                (MedusaEncounterDifficulty)reader.GetInt16(0),
                (MedusaRewardRuleKind)reader.GetInt16(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4)
                    ? null
                    : (MedusaEncounterTitle)reader.GetInt16(4)));
        }
        return rules.AsReadOnly();
    }
}
