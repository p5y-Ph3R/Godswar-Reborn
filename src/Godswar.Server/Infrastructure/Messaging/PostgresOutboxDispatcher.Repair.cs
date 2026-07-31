using System.Data;

namespace Godswar.Server.Infrastructure.Messaging;

internal sealed partial class PostgresOutboxDispatcher
{
    internal async Task<int> RecoverExpiredLeasesAsync(
        int maximumRepairs,
        CancellationToken cancellationToken = default)
    {
        if (maximumRepairs is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRepairs),
                "Expired lease repair is bounded to 1..500 rows.");
        }

        var outcomes = new List<DeferredOutcome>(maximumRepairs);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        while (outcomes.Count < maximumRepairs)
        {
            var expired = await ReadExpiredLeaseAsync(
                connection,
                transaction,
                cancellationToken);
            if (expired is null)
            {
                break;
            }

            outcomes.Add(await RecoverExpiredLeaseAsync(
                connection,
                transaction,
                expired.Value,
                cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        RecordDeferredOutcomes(outcomes);
        return outcomes.Count;
    }
}
