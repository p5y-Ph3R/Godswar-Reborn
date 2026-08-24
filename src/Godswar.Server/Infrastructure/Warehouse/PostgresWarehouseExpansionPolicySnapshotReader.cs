using System.Data;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Warehouse;
using Npgsql;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed class PostgresWarehouseExpansionPolicySnapshotReader
{
    private const int MaximumRows =
        WarehouseCapacityPolicy.MaximumSupportedBoxCount + 1;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IItemTemplateCatalog _templates;

    public PostgresWarehouseExpansionPolicySnapshotReader(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog templates)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _templates = templates ??
            throw new ArgumentNullException(nameof(templates));
    }

    public static async Task<WarehouseExpansionPolicySnapshot> LoadAsync(
        string connectionString,
        IItemTemplateCatalog templates,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        return await new PostgresWarehouseExpansionPolicySnapshotReader(
            dataSource,
            templates).ReadAsync(cancellationToken);
    }

    public async Task<WarehouseExpansionPolicySnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT release.revision,
                   release.sha256,
                   release.level_count,
                   release.sealed_at IS NOT NULL,
                   level.capacity,
                   level.key_cost,
                   level.key_item_id
            FROM public.warehouse_expansion_policy_publication publication
            JOIN public.warehouse_expansion_policy_revisions release
              ON release.revision = publication.revision
             AND release.sha256 = publication.policy_sha256
            JOIN public.warehouse_expansion_policy_levels level
              ON level.revision = release.revision
            WHERE publication.family = 'warehouse-expansion'
            ORDER BY level.capacity
            LIMIT @maximumRows;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("maximumRows", MaximumRows);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        long revision = 0;
        string? sha256 = null;
        short expectedCount = 0;
        var levels = new List<WarehouseExpansionPolicyLevel>(
            WarehouseCapacityPolicy.MaximumSupportedBoxCount);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (revision == 0)
            {
                revision = reader.GetInt64(0);
                sha256 = reader.GetString(1);
                expectedCount = reader.GetInt16(2);
                if (!reader.GetBoolean(3))
                {
                    throw new InvalidDataException(
                        "The warehouse expansion policy is unsealed.");
                }
            }
            else if (revision != reader.GetInt64(0) ||
                     !string.Equals(
                         sha256,
                         reader.GetString(1),
                         StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The warehouse expansion publication is ambiguous.");
            }

            levels.Add(new(
                reader.GetInt16(4),
                reader.GetInt16(5),
                reader.GetInt32(6)));
        }
        await reader.DisposeAsync();

        var snapshot = new WarehouseExpansionPolicySnapshot(
            revision,
            sha256 ?? string.Empty,
            levels);
        snapshot.Validate();
        if (levels.Count != expectedCount ||
            !WarehousePinnedItemPolicy.IsValid(_templates, snapshot))
        {
            throw new InvalidDataException(
                "The warehouse policy does not match pinned item content.");
        }
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }
}
