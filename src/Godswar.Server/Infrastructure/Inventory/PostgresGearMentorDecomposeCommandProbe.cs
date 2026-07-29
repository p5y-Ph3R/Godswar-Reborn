using System.Security.Cryptography;

namespace Godswar.Server.Infrastructure.Inventory;

internal enum PostgresGearMentorDecomposeCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    InventoryMutated = 3,
    LedgerInserted = 4,
    OutboxInserted = 5,
    BeforeCommit = 6,
    AfterCommit = 7
}

internal interface IPostgresGearMentorDecomposeCommandProbe
{
    ValueTask ReachedAsync(
        PostgresGearMentorDecomposeCommandStage stage,
        CancellationToken cancellationToken);
}

internal interface IGearMentorDecomposeRandomSource
{
    int NextIndex(int exclusiveUpperBound);
}

internal sealed class CryptographicGearMentorDecomposeRandomSource :
    IGearMentorDecomposeRandomSource
{
    public int NextIndex(int exclusiveUpperBound)
    {
        if (exclusiveUpperBound <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exclusiveUpperBound));
        }

        return RandomNumberGenerator.GetInt32(exclusiveUpperBound);
    }
}
