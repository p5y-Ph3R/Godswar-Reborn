using Godswar.Server.Application.Reconciliation;
using Npgsql;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal sealed partial class PostgresReconciliationSnapshot
{
    public async Task<ReconciliationPage> ReadCharacterPageAsync(
        long afterCharacterKey,
        int limit,
        CancellationToken cancellationToken)
    {
        if (afterCharacterKey < 0 || limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var counts = new Dictionary<ReconciliationCategory, long>();
        var selectedKeys = new List<long>(limit);
        var rows = 0;
        var nextKey = afterCharacterKey;
        await using (var command = CreateCommand(CharacterPageSql))
        {
            command.Parameters.AddWithValue("after_key", afterCharacterKey);
            command.Parameters.AddWithValue("limit", limit);
            command.Parameters.AddWithValue(
                "itemContentRevision",
                _itemContentRevision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows++;
                nextKey = reader.GetInt64(0);
                selectedKeys.Add(nextKey);
                AddCharacterFindings(reader, counts);
            }
        }

        Add(
            counts,
            await ReadLedgerChainFindingsAsync(
                selectedKeys,
                cancellationToken));
        var reachedEnd =
            rows < limit ||
            !await HasCharacterAfterAsync(nextKey, cancellationToken);
        return new ReconciliationPage(
            nextKey,
            rows,
            reachedEnd,
            ToCounts(counts));
    }

    private static void AddCharacterFindings(
        NpgsqlDataReader reader,
        IDictionary<ReconciliationCategory, long> counts)
    {
        var categories = new[]
        {
            ReconciliationCategory.WalletBaselineMissing,
            ReconciliationCategory.WalletCharacterMissing,
            ReconciliationCategory.WalletIdentityMismatch,
            ReconciliationCategory.WalletRevisionSequenceGap,
            ReconciliationCategory.WalletRevisionMismatch,
            ReconciliationCategory.WalletBalanceMismatch,
            ReconciliationCategory.InventoryBaselineMissing,
            ReconciliationCategory.InventoryCharacterMissing,
            ReconciliationCategory.InventoryIdentityMismatch,
            ReconciliationCategory.InventoryBaselineSnapshotMismatch,
            ReconciliationCategory.InventoryRevisionSequenceGap,
            ReconciliationCategory.InventoryRevisionMismatch,
            ReconciliationCategory.InventoryItemsMismatch,
            ReconciliationCategory.DuplicateInventorySlot,
            ReconciliationCategory.OrphanItemTemplate,
            ReconciliationCategory.ProgressionRewardRevisionGap,
            ReconciliationCategory.ProgressionRewardEvidenceGap,
            ReconciliationCategory.PetPresenceConflict,
            ReconciliationCategory.PetStreamEvidenceGap,
            ReconciliationCategory.RetainedCharacterWithoutPurgeEvidence
        };
        for (var index = 0; index < categories.Length; index++)
        {
            Add(counts, categories[index], reader.GetBoolean(index + 1));
        }
    }

    private static void Add(
        IDictionary<ReconciliationCategory, long> target,
        IEnumerable<ReconciliationCategoryCount> additions)
    {
        foreach (var addition in additions)
        {
            target.TryGetValue(addition.Category, out var current);
            target[addition.Category] =
                checked(current + addition.Count);
        }
    }

    private async Task<bool> HasCharacterAfterAsync(
        long key,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.character_economy_baseline
                WHERE character_id > @key
                UNION ALL
                SELECT 1
                FROM public.character_base
                WHERE id > @key
            );
            """;
        await using var command = CreateCommand(sql);
        command.Parameters.AddWithValue("key", key);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken));
    }
}
