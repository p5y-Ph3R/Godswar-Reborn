namespace Godswar.Server.Domain.World.Content;

internal enum PetGrowthResetRequestOperation : byte
{
    Preview = 1,
    Accept = 2
}

internal enum PetBasicSavvyResetRequestOperation : byte
{
    Preview = 1,
    Accept = 2
}

/// <summary>
/// Stock-client Pet Manager dialogue surface. Skill removal and Phoenix
/// Growth reset are admitted only through their exact fixed-frame requests
/// and durable operation identities; other mutating pages remain
/// capture-gated.
/// </summary>
internal static partial class PetManagerProtocol
{
    public const int DialogIndex = 31;
    public const int PointResetDialogIndex = 36;
    public const int FunctionArgumentCount = 18;
    public const int SkillUnlearnMenuSubId = 6;
    public const int SkillUnlearnPageTitleSubId = 16;
    public const int PetBindMenuSubId = 7;
    public const int PetBindActionSubId = 112;
    public const int PetBindAlreadyBoundResultSubId = 1072;
    public const int PetBindSucceededResultSubId = 1073;
    public const int PetBindNoPetResultSubId = 1075;
    public const int AppearanceChangeMenuSubId = 8;
    public const int AppearanceChangeDescriptionSubId = 113;
    public const int AppearanceChangeActionArgumentValue = 0;
    public const int AppearanceChangeItemArgumentIndex = 6;
    public const int AppearanceChangeFirstScratchArgumentIndex = 10;
    public const int AppearanceChangeLastScratchArgumentIndex = 12;
    public const int AppearanceChangeSucceededResultSubId = 130;
    public const int AppearanceChangeMissingJadeResultSubId = 137;
    public const int AppearanceChangeIncompatibleJadeResultSubId = 138;
    public const int AppearanceChangeNoPetResultSubId = 139;
    public const int AppearanceChangeUnboundPetResultSubId = 140;
    public const int BagPageCount = 4;
    public const int BagSlotsPerPage = 24;
    public const int MaximumSkillSlots = 12;
    public const uint AthensNpcId = 5227;
    public const uint PublishedSpartaNpcId = 5085;
    public const uint SourceSpartaNpcId = 5087;

    public const int NoSummonedPetResultSubId = 1011;
    public const int MissingStrongPurgePotionResultSubId = 1061;
    public const int EmptySkillSlotResultSubId = 1062;
    public const int SkillUnlearnedResultSubId = 1063;
    public const int GrowthResetMenuSubId = 101;
    public const int GrowthResetDescriptionSubId = 112;
    public const int GrowthResetActionSubId = 117;
    public const int GrowthResetMissingFeatherResultSubId = 127;
    public const int GrowthResetNoPetResultSubId = 128;
    public const int GrowthResetPreviewUnavailableResultSubId = 129;
    public const int GrowthResetSucceededResultSubId = 130;
    public const int GrowthResetFirstStatSuffix = 8;
    public const int GrowthResetFirstCurrentStatSuffix = 20;
    public const int BasicSavvyResetMenuSubId = 100;
    public const int BasicSavvyResetActionSubId = 116;
    public const int BasicSavvyResetMissingFeatherResultSubId = 127;
    public const int BasicSavvyResetNoPetResultSubId = 128;
    public const int BasicSavvyResetPreviewUnavailableResultSubId = 129;
    public const int BasicSavvyResetSucceededResultSubId = 120;
    public const int BasicSavvyResetFirstStatSuffix = 2;

    public static IReadOnlyList<int> InitialMenuSubIds { get; } =
        Array.AsReadOnly(Enumerable.Range(1, 11).ToArray());

    public static IReadOnlyList<int> PointResetInitialMenuSubIds { get; } =
        Array.AsReadOnly(new[] { 100, 101 });

    public static bool IsEndpoint(string npcKey, uint npcId) =>
        (npcKey, npcId) is
            ("Athens_088", AthensNpcId) or
            ("Sparta_088", PublishedSpartaNpcId) or
            ("Sparta_088", SourceSpartaNpcId);

    public static bool TryGetInformationPage(
        int menuSubId,
        out int[] responseSubIds) =>
        TryGetInformationPage(
            DialogIndex,
            menuSubId,
            out responseSubIds);

    public static bool TryGetInformationPage(
        int dialogIndex,
        int menuSubId,
        out int[] responseSubIds)
    {
        responseSubIds = dialogIndex switch
        {
            DialogIndex => PetManagerInformationPage(menuSubId),
            PointResetDialogIndex => menuSubId switch
            {
                100 => [111, 116],
                101 =>
                    [GrowthResetDescriptionSubId, GrowthResetActionSubId],
                _ => []
            },
            _ => []
        };
        return responseSubIds.Length > 0;
    }

    public static bool IsExactNavigationArguments(
        IReadOnlyList<int> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Count == FunctionArgumentCount &&
            arguments.All(static value => value == -1);
    }

    /// <summary>
    /// Resolves the stock Change Appearance confirmation. Page 113 is a text
    /// plus item-control page, not a selectable child: its A1 action appends
    /// literal zero in argument 0 and encodes the item control in argument 6
    /// as <c>bagPage * 100 + pageSlot</c>. Native arguments 10 through 12 are
    /// runtime scratch and are never interpreted; no pet ID is sent because
    /// the server targets the authenticated character's summoned pet.
    /// </summary>
    public static bool TryResolveAppearanceChangeMutation(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out int absoluteBagSlot)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        absoluteBagSlot = -1;
        if (dialogIndex != DialogIndex ||
            subId != AppearanceChangeMenuSubId ||
            arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        if (arguments[0] != AppearanceChangeActionArgumentValue)
        {
            return false;
        }

        for (var index = 1; index < arguments.Count; index++)
        {
            if (index != AppearanceChangeItemArgumentIndex &&
                index is not (>=
                    AppearanceChangeFirstScratchArgumentIndex and <=
                    AppearanceChangeLastScratchArgumentIndex) &&
                arguments[index] != -1)
            {
                return false;
            }
        }

        var coordinate = arguments[AppearanceChangeItemArgumentIndex];
        if (coordinate < 0)
        {
            return false;
        }

        var bagPage = coordinate / 100;
        var pageSlot = coordinate % 100;
        if (bagPage >= BagPageCount || pageSlot >= BagSlotsPerPage)
        {
            return false;
        }

        absoluteBagSlot = checked(
            bagPage * BagSlotsPerPage + pageSlot);
        return true;
    }

    /// <summary>
    /// Resolves the stock nested Bind confirmation. The client retains the
    /// parent choice (7) as the packet sub-ID and sends child choice 112 in
    /// argument zero. No captured stock evidence supports a flattened
    /// sub-ID 112 request, so that shape is deliberately not admitted.
    /// </summary>
    public static bool TryResolvePetBindMutation(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return dialogIndex == DialogIndex &&
            subId == PetBindMenuSubId &&
            arguments.Count == FunctionArgumentCount &&
            arguments[0] == PetBindActionSubId &&
            arguments.Skip(1).All(static value => value == -1);
    }

    public static bool TryResolveSkillUnlearnMutation(
        int subId,
        IReadOnlyList<int> arguments,
        out int zeroBasedSlot)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        zeroBasedSlot = -1;
        if (arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        // A stock nested NPC choice preserves the parent choice (6) in the
        // packet's sub-ID and appends the selected erase entry as argument 0.
        // Retain the direct shape as a compatibility boundary for clients
        // which flatten the same dialogue path.
        if (subId == SkillUnlearnMenuSubId)
        {
            return TryResolveSkillUnlearnSlot(arguments[0], out zeroBasedSlot) &&
                arguments.Skip(1).All(static value => value == -1);
        }

        return TryResolveSkillUnlearnSlot(subId, out zeroBasedSlot) &&
            arguments.All(static value => value == -1);
    }

    public static bool TryResolveGrowthResetMutation(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments) =>
        TryResolveGrowthResetMutation(
            dialogIndex,
            subId,
            arguments,
            out _);

    public static bool TryResolveGrowthResetMutation(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out PetGrowthResetRequestOperation operation)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        operation = default;
        if (dialogIndex != PointResetDialogIndex ||
            arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        if (subId == GrowthResetMenuSubId)
        {
            if (arguments[0] != GrowthResetActionSubId)
            {
                return false;
            }
            if (arguments.Skip(1).All(static value => value == -1))
            {
                operation = PetGrowthResetRequestOperation.Preview;
                return true;
            }
            if (arguments[1] == 0 &&
                arguments.Skip(2).All(static value => value == -1))
            {
                operation = PetGrowthResetRequestOperation.Accept;
                return true;
            }
            return false;
        }

        if (subId != GrowthResetActionSubId)
        {
            return false;
        }
        if (arguments.All(static value => value == -1))
        {
            operation = PetGrowthResetRequestOperation.Preview;
            return true;
        }
        if (arguments[0] == 0 &&
            arguments.Skip(1).All(static value => value == -1))
        {
            operation = PetGrowthResetRequestOperation.Accept;
            return true;
        }
        return false;
    }

    public static bool TryResolveBasicSavvyResetMutation(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments) =>
        TryResolveBasicSavvyResetMutation(
            dialogIndex,
            subId,
            arguments,
            out _);

    public static bool TryResolveBasicSavvyResetMutation(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out PetBasicSavvyResetRequestOperation operation)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        operation = default;
        if (dialogIndex != PointResetDialogIndex ||
            arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        if (subId == BasicSavvyResetMenuSubId)
        {
            if (arguments[0] != BasicSavvyResetActionSubId)
            {
                return false;
            }
            if (arguments.Skip(1).All(static value => value == -1))
            {
                operation = PetBasicSavvyResetRequestOperation.Preview;
                return true;
            }
            if (arguments[1] == 0 &&
                arguments.Skip(2).All(static value => value == -1))
            {
                operation = PetBasicSavvyResetRequestOperation.Accept;
                return true;
            }
            return false;
        }

        if (subId != BasicSavvyResetActionSubId)
        {
            return false;
        }
        if (arguments.All(static value => value == -1))
        {
            operation = PetBasicSavvyResetRequestOperation.Preview;
            return true;
        }
        if (arguments[0] == 0 &&
            arguments.Skip(1).All(static value => value == -1))
        {
            operation = PetBasicSavvyResetRequestOperation.Accept;
            return true;
        }
        return false;
    }

    /// <summary>
    /// NpcFunPett.lua renders Basic/Savvy results on page 120. Stat rows end
    /// in 02..07 and encode hundredths in the preceding decimal digits.
    /// </summary>
    public static int[] BuildBasicSavvyResetSuccessPage(
        IReadOnlyList<decimal> basicSavvyValues) =>
        BuildSixStatResetSuccessPage(
            basicSavvyValues,
            BasicSavvyResetSucceededResultSubId,
            BasicSavvyResetFirstStatSuffix,
            "A Basic/Savvy reset page requires six non-negative values.",
            nameof(basicSavvyValues));

    /// <summary>
    /// NpcFunPett.lua renders Growth results on page three. Sub-ID 130 is the
    /// success heading. Rolled stat rows end in 08..13 and current stat rows
    /// end in 20..25. Both encode hundredths in the preceding decimal digits:
    /// (subId - suffix) / 10000. The client derives each total from its six
    /// displayed rows so this page stays within its twelve native row slots.
    /// </summary>
    public static int[] BuildGrowthResetSuccessPage(
        IReadOnlyList<decimal> growthRates,
        IReadOnlyList<decimal> currentGrowthRates)
    {
        var rolled = BuildSixStatResetSuccessPage(
            growthRates,
            GrowthResetSucceededResultSubId,
            GrowthResetFirstStatSuffix,
            "A Growth reset page requires six non-negative rates.",
            nameof(growthRates));
        var current = BuildSixStatResetSuccessPage(
            currentGrowthRates,
            GrowthResetSucceededResultSubId,
            GrowthResetFirstCurrentStatSuffix,
            "A Growth reset comparison requires six non-negative current rates.",
            nameof(currentGrowthRates));

        var response = new int[rolled.Length + current.Length - 1];
        rolled.CopyTo(response, 0);
        Array.Copy(
            current,
            1,
            response,
            rolled.Length,
            current.Length - 1);
        return response;
    }

    private static int[] BuildSixStatResetSuccessPage(
        IReadOnlyList<decimal> values,
        int succeededResultSubId,
        int firstStatSuffix,
        string validationMessage,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count != 6 ||
            values.Any(static value => value < 0))
        {
            throw new ArgumentException(
                validationMessage,
                parameterName);
        }

        var response = new int[values.Count + 1];
        response[0] = succeededResultSubId;
        for (var index = 0; index < values.Count; index++)
        {
            var hundredths = decimal.ToInt32(decimal.Round(
                values[index] * 100m,
                0,
                MidpointRounding.AwayFromZero));
            response[index + 1] = checked(
                hundredths * 100 + firstStatSuffix + index);
        }
        return response;
    }

    public static bool TryResolveSkillUnlearnSlot(
        int subId,
        out int zeroBasedSlot)
    {
        zeroBasedSlot = subId switch
        {
            >= 106 and <= 111 => subId - 106,
            >= 114 and <= 119 => subId - 108,
            _ => -1
        };
        return zeroBasedSlot >= 0;
    }

    public static bool TryBuildSkillUnlearnPage(
        IReadOnlyList<int> activeSkillSlots,
        out int[] responseSubIds)
    {
        ArgumentNullException.ThrowIfNull(activeSkillSlots);
        responseSubIds = [];
        if (activeSkillSlots.Count is < 1 or > MaximumSkillSlots)
        {
            return false;
        }

        var orderedSlots = activeSkillSlots.Order().ToArray();
        if (orderedSlots.Distinct().Count() != orderedSlots.Length)
        {
            return false;
        }

        responseSubIds = new int[orderedSlots.Length + 1];
        responseSubIds[0] = SkillUnlearnPageTitleSubId;
        for (var index = 0; index < orderedSlots.Length; index++)
        {
            if (!TryGetSkillUnlearnSubId(
                    orderedSlots[index],
                    out responseSubIds[index + 1]))
            {
                responseSubIds = [];
                return false;
            }
        }
        return true;
    }

    private static bool TryGetSkillUnlearnSubId(
        int zeroBasedSlot,
        out int subId)
    {
        subId = zeroBasedSlot switch
        {
            >= 0 and <= 5 => 106 + zeroBasedSlot,
            >= 6 and < MaximumSkillSlots =>
                108 + zeroBasedSlot,
            _ => -1
        };
        return subId >= 0;
    }

    private static int[] PetManagerInformationPage(int menuSubId) =>
        menuSubId switch
        {
            1 => [11, 101],
            2 => [12, 102],
            3 => [13, 103],
            4 => [14, 104],
            5 => [15, 105],
            PetBindMenuSubId => [17, PetBindActionSubId],
            AppearanceChangeMenuSubId =>
                [AppearanceChangeDescriptionSubId],
            _ => []
        };
}
