namespace Godswar.Server.Game;

internal static partial class HolySuitDesignProtocol
{
    private static bool TryReadStoreAmount(
        IReadOnlyList<int> arguments,
        out long amount)
    {
        var rawAmount = arguments[AmountArgumentIndex];

        // The stock Store-EXP input writes -1 when its text field is blank.
        // That otherwise ambiguous UInt32.MaxValue bit pattern is reserved as
        // a mouse-only request for the server to choose the safe maximum.
        if (rawAmount == -1)
        {
            amount = 0;
            return true;
        }

        // A typed zero is still invalid. Other negative Int32 bit patterns
        // retain their UInt32 wire meaning through 0xFFFFFFFE.
        if (rawAmount == 0)
        {
            amount = 0;
            return false;
        }

        amount = unchecked((uint)rawAmount);
        return true;
    }
}
