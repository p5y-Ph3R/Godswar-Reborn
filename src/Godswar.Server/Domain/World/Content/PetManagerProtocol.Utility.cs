namespace Godswar.Server.Domain.World.Content;

internal enum PetManagerUtilityRequestOperation : byte
{
    CheckGrowth = 1,
    Seal = 2,
    ClaimPetCall = 4,
    ClaimMerge = 5,
    ChangeGender = 6
}

internal static partial class PetManagerProtocol
{
    public const int GrowthCheckMenuSubId = 4;
    public const int GrowthCheckActionSubId = 104;
    public const int SealMenuSubId = 5;
    public const int SealActionSubId = 105;
    public const int ClaimPetCallMenuSubId = 9;
    public const int ClaimMergeMenuSubId = 10;
    public const int ChangeGenderMenuSubId = 11;
    public const int ChangeGenderActionArgumentValue = 0;
    public const int UtilityFirstScratchArgumentIndex = 10;
    public const int UtilityLastScratchArgumentIndex = 12;

    public const int GrowthCheckMissingTearResultSubId = 1041;
    public const int GrowthCheckTearSpentResultSubId = 1071;
    public const int SealMissingJadeResultSubId = 1051;
    public const int SealBagFullResultSubId = 1052;
    public const int SealSucceededResultSubId = 1053;
    public const int SealBoundPetResultSubId = 1072;
    public const int CharmBagFullResultSubId = 10000;
    public const int CharmAlreadyHeldResultSubId = 10001;
    public const int PetCallClaimedResultSubId = 10002;
    public const int MergeClaimedResultSubId = 10003;
    public const int GenderUnboundPetResultSubId = 150;
    public const int GenderNoPetResultSubId = 160;
    public const int GenderUnavailableResultSubId = 161;
    public const int GenderMissingReverserResultSubId = 210;
    public const int GenderChangedMaleResultSubId = 220;
    public const int GenderChangedFemaleResultSubId = 230;

    /// <summary>
    /// Resolves the exact stock utility actions. CNpcFun's shared fixed-frame
    /// sender copies three numeric UI scratch fields into arguments 10..12;
    /// those values are non-authoritative and may persist between pages. All
    /// other padding remains exact, and the authenticated summoned pet is the
    /// only possible target.
    /// </summary>
    public static bool TryResolveUtilityMutation(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out PetManagerUtilityRequestOperation operation)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        operation = default;
        if (dialogIndex != DialogIndex ||
            arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        operation = subId switch
        {
            GrowthCheckMenuSubId when arguments[0] ==
                    GrowthCheckActionSubId =>
                PetManagerUtilityRequestOperation.CheckGrowth,
            SealMenuSubId when arguments[0] == SealActionSubId =>
                PetManagerUtilityRequestOperation.Seal,
            ClaimPetCallMenuSubId when arguments[0] == -1 =>
                PetManagerUtilityRequestOperation.ClaimPetCall,
            ClaimMergeMenuSubId when arguments[0] == -1 =>
                PetManagerUtilityRequestOperation.ClaimMerge,
            ChangeGenderMenuSubId when arguments[0] ==
                    ChangeGenderActionArgumentValue =>
                PetManagerUtilityRequestOperation.ChangeGender,
            _ => default
        };
        if (!Enum.IsDefined(operation))
        {
            return false;
        }

        for (var index = 1; index < arguments.Count; index++)
        {
            if (index is >= UtilityFirstScratchArgumentIndex and <=
                    UtilityLastScratchArgumentIndex)
            {
                continue;
            }
            if (arguments[index] != -1)
            {
                operation = default;
                return false;
            }
        }
        return true;
    }

    public static bool IsGenderPreviewRequest(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments) =>
        dialogIndex == DialogIndex &&
        subId == ChangeGenderMenuSubId &&
        HasOnlyUtilityScratch(arguments, expectedArgumentZero: -1);

    private static bool HasOnlyUtilityScratch(
        IReadOnlyList<int> arguments,
        int expectedArgumentZero)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != FunctionArgumentCount ||
            arguments[0] != expectedArgumentZero)
        {
            return false;
        }
        for (var index = 1; index < arguments.Count; index++)
        {
            if (index is not (>= UtilityFirstScratchArgumentIndex and <=
                    UtilityLastScratchArgumentIndex) &&
                arguments[index] != -1)
            {
                return false;
            }
        }
        return true;
    }

    public static int BuildGenderPreviewSubId(
        int petLevel,
        byte currentSex)
    {
        if (petLevel is < 1 or > 255 || currentSex > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(petLevel));
        }
        return checked(petLevel * 1000 + currentSex * 100 + 21);
    }

    public static int[] BuildGrowthCheckSuccessPage(
        long petId,
        IReadOnlyList<decimal> growth)
    {
        ArgumentNullException.ThrowIfNull(growth);
        if (growth.Count != 6 ||
            growth.Any(static value => value is < 0 or > 100m) ||
            petId is <= 0 or > int.MaxValue / 1000)
        {
            throw new ArgumentException(
                "Growth-check evidence is not representable by the stock page.",
                nameof(growth));
        }

        var response = new int[growth.Count + 2];
        response[0] = checked((int)petId * 1000 + 1);
        for (var index = 0; index < growth.Count; index++)
        {
            var hundredths = decimal.ToInt32(
                decimal.Truncate(growth[index] * 100m));
            response[index + 1] = checked(
                hundredths * 1000 + index + 2);
        }
        response[^1] = GrowthCheckTearSpentResultSubId;
        return response;
    }
}
