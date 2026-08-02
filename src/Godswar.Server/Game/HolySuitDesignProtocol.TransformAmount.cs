namespace Godswar.Server.Game;

internal static partial class HolySuitDesignProtocol
{
    /// <summary>
    /// The stock client's Lua text prefill is visual only. On confirmation,
    /// the native NPC packet leaves the amount at its -1 blank sentinel and
    /// writes zero to its first scratch argument. Treat that exact commit
    /// shape as the displayed mouse-only default; all--1 remains navigation.
    /// </summary>
    public const int MouseOnlyTransformPrismCount = 20;

    private const int StockCommitScratchArgumentIndex = 0;

    private static bool TryReadTransformPrismCount(
        IReadOnlyList<int> arguments,
        out long amount)
    {
        var rawAmount = arguments[AmountArgumentIndex];
        if (rawAmount > 0)
        {
            amount = rawAmount;
            return true;
        }

        if (rawAmount == -1 &&
            arguments[StockCommitScratchArgumentIndex] == 0)
        {
            amount = MouseOnlyTransformPrismCount;
            return true;
        }

        amount = 0;
        return false;
    }
}
