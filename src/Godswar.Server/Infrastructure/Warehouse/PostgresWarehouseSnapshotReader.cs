using System.Data;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Infrastructure.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed class PostgresWarehouseSnapshotReader :
    IWarehouseSnapshotReader
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;

    public PostgresWarehouseSnapshotReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(dataSource);
    }

    public async Task<WarehouseSnapshot?> ReadAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        var validation = await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            subject,
            ownership,
            cancellationToken);
        if (validation.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        validation.RequireCurrent();

        var header = await ReadHeaderAsync(
            connection,
            transaction,
            subject,
            cancellationToken);
        if (header is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var items = await ReadItemsAsync(
            connection,
            transaction,
            subject.CharacterId,
            header.Capacity,
            cancellationToken);
        var snapshot = new WarehouseSnapshot(
            subject.AccountId,
            subject.CharacterId,
            header.Capacity,
            header.WarehouseRevision,
            header.InventoryRevision,
            items);
        snapshot.Validate();
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static async Task<WarehouseHeader?> ReadHeaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT warehouse_capacity, warehouse_revision,
                   inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
              AND lifecycle_state = 'active';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var header = new WarehouseHeader(
            reader.GetInt16(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
        if (!WarehouseCapacityPolicy.IsValidCapacity(header.Capacity) ||
            header.WarehouseRevision < 0 ||
            header.InventoryRevision < 0 ||
            await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The character warehouse header is invalid.");
        }
        return header;
    }

    private static async Task<IReadOnlyList<WarehouseItemSnapshot>>
        ReadItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            int capacity,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT slot_index, {WarehouseItemStateCodec.SelectCompactColumns}
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 3
            ORDER BY slot_index;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        var items = new List<WarehouseItemSnapshot>(capacity);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(0);
            if (slot < 0 || slot >= capacity)
            {
                throw new InvalidDataException(
                    "A warehouse item is outside the accessible capacity.");
            }
            items.Add(new(
                slot,
                WarehouseItemStateCodec.ReadCompactItem(reader, 1)
                    .ToCompactString()));
        }
        return items;
    }

    private sealed record WarehouseHeader(
        int Capacity,
        long WarehouseRevision,
        long InventoryRevision);
}
