namespace Godswar.Server.Phase5A;

internal sealed class OperationBudget
{
    private long _consumed;

    public OperationBudget(long limit)
    {
        if (limit is <= 0 or > Phase5AOptions.MaximumTotalOperations)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        Limit = limit;
    }

    public long Limit { get; }

    public long Consumed => _consumed;

    public long Remaining => Limit - _consumed;

    public void Consume(int operations)
    {
        if (operations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operations));
        }

        var next = checked(_consumed + operations);
        if (next > Limit)
        {
            throw new InvalidOperationException(
                "The workload attempted to exceed its prevalidated operation budget.");
        }

        _consumed = next;
    }
}
