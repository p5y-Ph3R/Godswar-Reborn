using System.Data;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Warehouse;
using Npgsql;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed class PostgresWarehouseExpansionPolicySettingsStore :
    IWarehouseExpansionPolicySettingsStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IItemTemplateCatalog _templates;

    public PostgresWarehouseExpansionPolicySettingsStore(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog templates)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _templates = templates ??
            throw new ArgumentNullException(nameof(templates));
    }

    public async Task<WarehouseExpansionPolicyUpdateResult>
        TryPublishSuccessorAsync(
            WarehouseExpansionPolicyUpdate update,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.ExpectedRevision <= 0 ||
            !IsValidActor(update.UpdatedBy) ||
            !TryNormalize(update.Levels, out var levels))
        {
            return new(WarehouseExpansionPolicyUpdateStatus.Invalid, null);
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var current = await ReadCurrentAsync(
            connection,
            transaction,
            cancellationToken);
        if (current.Revision != update.ExpectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                WarehouseExpansionPolicyUpdateStatus.RevisionConflict,
                null);
        }

        var sha256 = WarehouseExpansionPolicySnapshot.ComputeSha256(levels);
        if (string.Equals(sha256, current.Sha256, StringComparison.Ordinal))
        {
            var unchanged = new WarehouseExpansionPolicySnapshot(
                current.Revision,
                current.Sha256,
                levels);
            await transaction.RollbackAsync(cancellationToken);
            return new(
                WarehouseExpansionPolicyUpdateStatus.Unchanged,
                unchanged);
        }

        var snapshot = new WarehouseExpansionPolicySnapshot(
            checked(current.Revision + 1),
            sha256,
            levels);
        snapshot.Validate();
        if (!WarehousePinnedItemPolicy.IsValid(_templates, snapshot))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(WarehouseExpansionPolicyUpdateStatus.Invalid, null);
        }

        await InsertRevisionAsync(
            connection,
            transaction,
            snapshot,
            update.UpdatedBy,
            cancellationToken);
        if (!await CompareAndSwapAsync(
                connection,
                transaction,
                current,
                snapshot,
                update.UpdatedBy,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                WarehouseExpansionPolicyUpdateStatus.RevisionConflict,
                null);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(WarehouseExpansionPolicyUpdateStatus.Updated, snapshot);
    }

    private bool TryNormalize(
        IReadOnlyList<WarehouseExpansionPolicyLevel>? source,
        out WarehouseExpansionPolicyLevel[] levels)
    {
        levels = source?.OrderBy(static level => level.Capacity).ToArray() ??
            [];
        try
        {
            var candidate = new WarehouseExpansionPolicySnapshot(
                1,
                WarehouseExpansionPolicySnapshot.ComputeSha256(levels),
                levels);
            candidate.Validate();
            return WarehousePinnedItemPolicy.IsValid(_templates, candidate);
        }
        catch (Exception exception) when (exception is
            InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsValidActor(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        !value.Any(char.IsControl);

    private static async Task<CurrentPublication> ReadCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT revision, policy_sha256, publication_version
            FROM public.warehouse_expansion_policy_publication
            WHERE family = 'warehouse-expansion'
            FOR UPDATE;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The warehouse policy publication pointer is missing.");
        }
        var publication = new CurrentPublication(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2));
        if (publication.Revision <= 0 ||
            publication.Version <= 0 ||
            publication.Sha256.Length != 64 ||
            await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The warehouse policy publication pointer is invalid.");
        }
        return publication;
    }

    private static async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarehouseExpansionPolicySnapshot snapshot,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        await using (var header = new NpgsqlCommand(
            """
            INSERT INTO public.warehouse_expansion_policy_revisions (
                revision, sha256, level_count, source, created_by)
            VALUES (@revision, @sha256, @levelCount,
                    'management-successor', @createdBy);
            """,
            connection,
            transaction))
        {
            header.Parameters.AddWithValue("revision", snapshot.Revision);
            header.Parameters.AddWithValue("sha256", snapshot.Sha256);
            header.Parameters.AddWithValue(
                "levelCount",
                checked((short)snapshot.Levels.Count));
            header.Parameters.AddWithValue("createdBy", updatedBy);
            if (await header.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The warehouse policy header insert was not exact.");
            }
        }

        await using var level = new NpgsqlCommand(
            """
            INSERT INTO public.warehouse_expansion_policy_levels (
                revision, capacity, key_cost, key_item_id)
            VALUES (@revision, @capacity, @keyCost, @keyItemId);
            """,
            connection,
            transaction);
        foreach (var value in snapshot.Levels)
        {
            level.Parameters.Clear();
            level.Parameters.AddWithValue("revision", snapshot.Revision);
            level.Parameters.AddWithValue("capacity", value.Capacity);
            level.Parameters.AddWithValue("keyCost", value.KeyCost);
            level.Parameters.AddWithValue("keyItemId", value.KeyItemId);
            if (await level.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "A warehouse policy level insert was not exact.");
            }
        }
    }

    private static async Task<bool> CompareAndSwapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CurrentPublication current,
        WarehouseExpansionPolicySnapshot successor,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.warehouse_expansion_policy_publication
            SET revision = @successorRevision,
                policy_sha256 = @successorSha256,
                publication_version = publication_version + 1,
                updated_by = @updatedBy,
                updated_at = now()
            WHERE family = 'warehouse-expansion'
              AND revision = @expectedRevision
              AND policy_sha256 = @expectedSha256
              AND publication_version = @expectedVersion;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "successorRevision",
            successor.Revision);
        command.Parameters.AddWithValue(
            "successorSha256",
            successor.Sha256);
        command.Parameters.AddWithValue("updatedBy", updatedBy);
        command.Parameters.AddWithValue(
            "expectedRevision",
            current.Revision);
        command.Parameters.AddWithValue(
            "expectedSha256",
            current.Sha256);
        command.Parameters.AddWithValue(
            "expectedVersion",
            current.Version);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private sealed record CurrentPublication(
        long Revision,
        string Sha256,
        long Version);
}
