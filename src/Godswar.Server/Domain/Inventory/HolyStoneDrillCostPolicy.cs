using System.Globalization;

namespace Godswar.Server.Domain.Inventory;

internal static class HolyStoneDrillCostPolicy
{
    public const int FirstSocketGoldCost = 230;
    public const int SecondSocketGoldCost = 2_300;
    private const int SocketCountFieldIndex = 17;

    public static bool TryGetGoldCost(
        short socketCount,
        out int goldCost)
    {
        goldCost = socketCount switch
        {
            0 => FirstSocketGoldCost,
            1 => SecondSocketGoldCost,
            _ => 0
        };
        return goldCost > 0;
    }

    public static bool TryGetGoldCostFromCompactTargetState(
        string compactTargetState,
        out int goldCost)
    {
        goldCost = 0;
        if (string.IsNullOrWhiteSpace(compactTargetState) ||
            compactTargetState.Length < 2 ||
            compactTargetState[0] != '[' ||
            compactTargetState[^1] != ']')
        {
            return false;
        }

        var fields = compactTargetState[1..^1].Split(
            ',',
            StringSplitOptions.None);
        return fields.Length > SocketCountFieldIndex &&
            short.TryParse(
                fields[SocketCountFieldIndex],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var socketCount) &&
            TryGetGoldCost(socketCount, out goldCost);
    }
}
