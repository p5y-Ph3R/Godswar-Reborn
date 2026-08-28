namespace Godswar.Server.Domain.World.Content;

internal enum FactionCrierWireOperation : byte
{
    DailyClaim = 1,
    WeeklyReclaim = 2,
    RenewNameplate = 3,
    TurnInNameplates = 4
}

internal enum FactionCrierWireNameplateSet : byte
{
    None = 0,
    Single = 1,
    OddTriple = 2,
    EvenTriple = 3,
    AllSix = 4
}

internal enum FactionCrierWireRewardKind : byte
{
    None = 0,
    Experience = 1,
    TalentPoints = 2,
    ExperienceAndTalentPoints = 3
}

internal enum FactionCrierWireCurrency : byte
{
    None = 0,
    Silver = 1,
    BindingGold = 2,
    Gold = 3
}

internal readonly record struct FactionCrierWireIntent(
    FactionCrierWireOperation Operation,
    int ActionSubId,
    int NameplateOrdinal,
    FactionCrierWireNameplateSet NameplateSet,
    FactionCrierWireRewardKind RewardKind,
    FactionCrierWireCurrency PaymentCurrency,
    int RewardMultiplier,
    int SourceKitBagSlot = -1);

/// <summary>
/// Stock-client Faction Crier dialogue surface. This class recognizes only
/// the exact nested paths emitted by NpcFunSignact. Costs, weekday rewards,
/// item ownership, and progression remain authoritative server policy and
/// are deliberately absent from the wire parser.
/// </summary>
internal static class FactionCrierProtocol
{
    public const int DialogIndex = 15;
    public const int ActionPacketBytes = 92;
    public const int FunctionArgumentCount = 18;
    public const uint AthensNpcId = 5194;
    public const uint PublishedSpartaNpcId = 5052;
    public const uint SourceSpartaNpcId = 5054;
    public const int FirstItemArgumentIndex = 6;
    public const int FirstScratchArgumentIndex = 10;
    public const int LastScratchArgumentIndex = 12;
    public const int BagPageCount = 4;
    public const int BagSlotsPerPage = 24;

    public static IReadOnlyList<int> InitialMenuSubIds { get; } =
        Array.AsReadOnly(new[] { 1, 2, 3, 4, 1000 });

    public static bool IsEndpoint(string npcKey, uint npcId) =>
        (npcKey, npcId) is
            ("Athens_055", AthensNpcId) or
            ("Sparta_055", PublishedSpartaNpcId) or
            ("Sparta_055", SourceSpartaNpcId);

    public static bool TryGetNavigationPage(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out int[] responseSubIds)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        responseSubIds = [];
        if (dialogIndex != DialogIndex ||
            arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        responseSubIds = (subId, ReadPath(arguments)) switch
        {
            (2, "") => [10, 20, 1001],
            (2, "10") => [101, 102, 103, 104, 105, 106, 1002],
            (2, "20") => [107, 108, 109, 1003],
            (2, "20/107") =>
                [110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 1004],
            (2, "20/108") =>
                [120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 1005],
            (2, "20/109") => [130, 131, 132, 133, 134, 1006],
            (3, "") => [31, 32, 33, 34, 35, 36, 1200],
            (4, "") => [41, 42, 43, 44, 45, 46, 1300],
            _ => []
        };
        return responseSubIds.Length > 0;
    }

    public static bool TryResolveMutation(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out FactionCrierWireIntent intent)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        intent = default;
        if (dialogIndex != DialogIndex ||
            arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        if (subId == 1 && HasExactPath(arguments))
        {
            intent = new FactionCrierWireIntent(
                FactionCrierWireOperation.DailyClaim,
                subId,
                NameplateOrdinal: 0,
                FactionCrierWireNameplateSet.None,
                FactionCrierWireRewardKind.None,
                FactionCrierWireCurrency.None,
                RewardMultiplier: 0);
            return true;
        }

        if (subId == 3 &&
            arguments[0] is >= 31 and <= 36 &&
            HasExactPath(arguments, arguments[0]))
        {
            intent = new FactionCrierWireIntent(
                FactionCrierWireOperation.WeeklyReclaim,
                arguments[0],
                arguments[0] - 30,
                FactionCrierWireNameplateSet.None,
                FactionCrierWireRewardKind.None,
                FactionCrierWireCurrency.Gold,
                RewardMultiplier: 0);
            return true;
        }

        if (TryResolveRenewal(subId, arguments, out intent) ||
            TryResolveSingleTurnIn(subId, arguments, out intent) ||
            TryResolveMultipleTurnIn(subId, arguments, out intent))
        {
            return true;
        }

        intent = default;
        return false;
    }

    private static bool TryResolveRenewal(
        int subId,
        IReadOnlyList<int> arguments,
        out FactionCrierWireIntent intent)
    {
        intent = default;
        if (subId != 4 || arguments[0] is < 41 or > 46 ||
            !TryDecodeItemControlCoordinate(
                arguments[FirstItemArgumentIndex],
                out var kitBagSlot))
        {
            return false;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (index == 0 || index == FirstItemArgumentIndex ||
                index is >= FirstScratchArgumentIndex and
                    <= LastScratchArgumentIndex)
            {
                continue;
            }
            if (arguments[index] != -1)
            {
                return false;
            }
        }

        intent = new FactionCrierWireIntent(
            FactionCrierWireOperation.RenewNameplate,
            arguments[0],
            arguments[0] - 40,
            FactionCrierWireNameplateSet.None,
            FactionCrierWireRewardKind.None,
            FactionCrierWireCurrency.Gold,
            RewardMultiplier: 0,
            kitBagSlot);
        return true;
    }

    private static bool TryResolveSingleTurnIn(
        int subId,
        IReadOnlyList<int> arguments,
        out FactionCrierWireIntent intent)
    {
        intent = default;
        if (subId != 2 || arguments[0] != 10 ||
            arguments[1] is < 101 or > 106 ||
            !HasExactPath(arguments, 10, arguments[1]))
        {
            return false;
        }

        intent = new FactionCrierWireIntent(
            FactionCrierWireOperation.TurnInNameplates,
            arguments[1],
            arguments[1] - 100,
            FactionCrierWireNameplateSet.Single,
            FactionCrierWireRewardKind.Experience,
            FactionCrierWireCurrency.None,
            RewardMultiplier: 1);
        return true;
    }

    private static bool TryResolveMultipleTurnIn(
        int subId,
        IReadOnlyList<int> arguments,
        out FactionCrierWireIntent intent)
    {
        intent = default;
        if (subId != 2 || arguments[0] != 20)
        {
            return false;
        }

        var set = arguments[1] switch
        {
            107 => FactionCrierWireNameplateSet.OddTriple,
            108 => FactionCrierWireNameplateSet.EvenTriple,
            109 => FactionCrierWireNameplateSet.AllSix,
            _ => FactionCrierWireNameplateSet.None
        };
        var actionSubId = arguments[2];
        if (set == FactionCrierWireNameplateSet.None ||
            !HasExactPath(arguments, 20, arguments[1], actionSubId) ||
            !TryResolveOffer(
                set,
                actionSubId,
                out var reward,
                out var currency,
                out var multiplier))
        {
            return false;
        }

        intent = new FactionCrierWireIntent(
            FactionCrierWireOperation.TurnInNameplates,
            actionSubId,
            NameplateOrdinal: 0,
            set,
            reward,
            currency,
            multiplier);
        return true;
    }

    private static bool TryResolveOffer(
        FactionCrierWireNameplateSet set,
        int actionSubId,
        out FactionCrierWireRewardKind reward,
        out FactionCrierWireCurrency currency,
        out int multiplier)
    {
        reward = default;
        currency = default;
        multiplier = 0;
        if (set == FactionCrierWireNameplateSet.AllSix)
        {
            (currency, multiplier) = actionSubId switch
            {
                130 => (FactionCrierWireCurrency.Silver, 12),
                131 => (FactionCrierWireCurrency.BindingGold, 18),
                132 => (FactionCrierWireCurrency.Gold, 18),
                133 => (FactionCrierWireCurrency.BindingGold, 24),
                134 => (FactionCrierWireCurrency.Gold, 24),
                _ => default
            };
            reward = FactionCrierWireRewardKind.ExperienceAndTalentPoints;
            return multiplier > 0;
        }

        var firstSubId = set == FactionCrierWireNameplateSet.OddTriple
            ? 110
            : 120;
        var option = actionSubId - firstSubId;
        if (option is < 0 or > 9)
        {
            return false;
        }

        reward = option % 2 == 0
            ? FactionCrierWireRewardKind.Experience
            : FactionCrierWireRewardKind.TalentPoints;
        (currency, multiplier) = (option / 2) switch
        {
            0 => (FactionCrierWireCurrency.Silver, 6),
            1 => (FactionCrierWireCurrency.BindingGold, 9),
            2 => (FactionCrierWireCurrency.Gold, 9),
            3 => (FactionCrierWireCurrency.BindingGold, 12),
            4 => (FactionCrierWireCurrency.Gold, 12),
            _ => default
        };
        return multiplier > 0;
    }

    private static bool TryDecodeItemControlCoordinate(
        int coordinate,
        out int absoluteKitBagSlot)
    {
        absoluteKitBagSlot = -1;
        if (coordinate < 0)
        {
            return false;
        }

        var page = coordinate / 100;
        var pageSlot = coordinate % 100;
        if (page >= BagPageCount || pageSlot >= BagSlotsPerPage)
        {
            return false;
        }

        absoluteKitBagSlot = checked(page * BagSlotsPerPage + pageSlot);
        return true;
    }

    private static string? ReadPath(IReadOnlyList<int> arguments)
    {
        var path = new List<int>();
        var paddingStarted = false;
        foreach (var argument in arguments)
        {
            if (argument == -1)
            {
                paddingStarted = true;
                continue;
            }
            if (paddingStarted)
            {
                return null;
            }
            path.Add(argument);
        }
        return string.Join('/', path);
    }

    private static bool HasExactPath(
        IReadOnlyList<int> arguments,
        params int[] path)
    {
        if (path.Length > arguments.Count)
        {
            return false;
        }
        for (var index = 0; index < arguments.Count; index++)
        {
            var expected = index < path.Length ? path[index] : -1;
            if (arguments[index] != expected)
            {
                return false;
            }
        }
        return true;
    }
}
