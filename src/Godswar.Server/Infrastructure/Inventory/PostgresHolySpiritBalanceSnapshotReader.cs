using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class PostgresHolySpiritBalanceSnapshotReader
{
    public static async Task<HolySpiritBalanceSnapshot> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        return await LoadAsync(dataSource, cancellationToken);
    }

    internal static async Task<HolySpiritBalanceSnapshot> LoadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var command = dataSource.CreateCommand(
            """
            SELECT setting_id,
                   cooled_physical_reduction_grade_one_maximum,
                   cooled_magic_reduction_grade_one_maximum,
                   cooled_critical_reduction_grade_one_maximum,
                   revision,
                   updated_at,
                   updated_by
            FROM public.holy_spirit_balance_settings
            ORDER BY setting_id;
            """);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt16(0) != 1)
        {
            throw new InvalidDataException(
                "The Holy Spirit balance singleton is missing.");
        }

        var snapshot = new HolySpiritBalanceSnapshot(
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt16(3),
            reader.GetInt64(4),
            new DateTimeOffset(reader.GetDateTime(5).ToUniversalTime()),
            reader.GetString(6));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The Holy Spirit balance singleton is ambiguous.");
        }

        snapshot.Validate();
        return snapshot;
    }
}
