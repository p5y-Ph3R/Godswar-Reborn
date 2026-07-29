using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static class GearEnhancerProtocol
{
    // The shipped client identifies its Gear Mentor as numbered city NPC 070.
    // The city object-ID convention is Sparta=4997+number and Athens=5139+number.
    // It uses NPC_FLAG_SYS_BREAK / NpcFunBreak (dialog 4). NPC 143 is the
    // distinct physical Origin Enhancer and owns dialog 118; the helpers below
    // keep those key/dialog identities separate even though their transactions
    // share the same authoritative operation engine. There is deliberately no
    // virtual NPC-zero/standalone launcher route.
    public const uint SpartaEnhancerNpcId = 5067;
    public const uint AthensEnhancerNpcId = 5209;
    public const uint SpartaOriginEnhancerNpcId = 5140;
    public const uint AthensOriginEnhancerNpcId = 5282;
    public const int DialogIndex = 4;
    public const int OriginDialogIndex = 118;
    public const int InitialMenuRequestSubId = -1;
    public const int EnhanceAttributeSubId = 2;
    public const int AddAttributeSubId = 3;
    public const int DeleteAttributesSubId = 6;
    public const int DecomposeGearSubId = 1;
    public const int MakeAttributeStoneSubId = 4;
    public const int InstructionsSubId = 5;
    public const int WashDustSubId = 7;
    public const int TransformCrystalSubId = 8;
    public const int CombineGemPiecesMenuSubId = 9;
    public const int CombineGemPiecesActionSubId = 201;
    public const int FirstGearMentorMenuSubId = 1;
    public const int LastGearMentorMenuSubId = 9;
    public const int FunctionActionPayloadLength = 88;
    public const int FunctionActionArgumentCount = 18;
    public const int GearArgumentIndex = 6;
    public const int CatalystArgumentIndex = 7;
    public const int AttributeStoneArgumentIndex = 8;
    public const int TemporarilyDisabledResultSubId = 999;
    public const int NothingSelectedResultSubId = 1001;
    public const int SelectedItemMissingResultSubId = 1002;
    public const int MissingGearResultSubId = 1006;
    public const int MissingAttributeStoneResultSubId = 1007;
    public const int MissingQuartzPlateResultSubId = 1008;
    public const int QuartzLevelMismatchResultSubId = 1009;
    public const int EnhanceSucceededResultSubId = 1010;
    public const int AttributeSlotsFullResultSubId = 1011;
    public const int AttributeAlreadyPresentResultSubId = 1012;
    public const int AddSucceededResultSubId = 1013;
    public const int AttributeNotAllowedResultSubId = 1018;
    public const int InvalidSelectionResultSubId = 1019;
    public const int MissingFlameSparkResultSubId = 1021;
    public const int MissingEnhanceAttributeResultSubId = 1023;
    public const int InsufficientEnhanceMaterialsResultSubId = 1026;
    public const int InsufficientAddMaterialsResultSubId = 1027;
    public const int MissingWaterGrainResultSubId = 1028;
    public const int MissingDeleteAttributeResultSubId = 1029;
    public const int DeleteSucceededResultSubId = 1030;
    public const int AttributeNotEnhanceableResultSubId = 1031;
    public const int DecomposeInvalidEquipmentResultSubId = 1003;
    public const int DecomposeInsufficientQualityResultSubId = 1004;
    public const int DecomposeSucceededResultSubId = 1005;
    public const int DecomposeEquipmentLevelResultSubId = 1014;
    public const int DecomposePlayerLevelResultSubId = 1015;
    public const int MakeStoneInsufficientDustResultSubId = 1016;
    public const int MakeStoneSucceededResultSubId = 1017;
    public const int BagFullResultSubId = 1020;
    public const int MakeStoneInvalidDustResultSubId = 1022;
    public const int DecomposeNothingSelectedResultSubId = 1024;
    public const int MakeStoneNothingSelectedResultSubId = 1025;
    public const int ClassSuitResultSubId = 1032;
    public const int TransformInvalidCrystalResultSubId = 1822;
    public const int TransformSucceededResultSubId = 1823;
    public const int CombineInvalidPiecesResultSubId = 301;
    public const int CombineInsufficientPiecesResultSubId = 302;
    public const int CombineBagFullResultSubId = 303;
    public const int CombineSucceededResultSubId = 304;
    public static readonly TimeSpan SelectionContextLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan NativeClearCommitCorrelationLifetime = TimeSpan.FromSeconds(1);
    public static GearEnhancerEndpoint ResolveEndpoint(byte camp)
    {
        return camp == GameDefaults.SpartaCamp
            ? new GearEnhancerEndpoint(SpartaEnhancerNpcId, "Sparta_070")
            : new GearEnhancerEndpoint(AthensEnhancerNpcId, "Athens_070");
    }

    public static bool IsEnhancerNpcKey(string npcKey)
    {
        return npcKey is "Sparta_070" or "Athens_070";
    }

    public static bool IsOriginEnhancerNpcKey(string npcKey)
    {
        return npcKey is "Sparta_143" or "Athens_143";
    }

    public static bool IsOriginEnhancerEndpoint(string npcKey, uint npcId)
    {
        return (npcKey, npcId) is
            ("Sparta_143", SpartaOriginEnhancerNpcId) or
            ("Athens_143", AthensOriginEnhancerNpcId);
    }

    public static bool IsOperationSubId(int subId)
    {
        return subId is EnhanceAttributeSubId or AddAttributeSubId or
            DeleteAttributesSubId;
    }

    public static bool IsGearMentorTransactionSubId(int subId)
    {
        return subId is DecomposeGearSubId or MakeAttributeStoneSubId or
            TransformCrystalSubId or CombineGemPiecesActionSubId;
    }

    public static bool IsGearMentorMenuSubId(int subId)
    {
        return subId is >= FirstGearMentorMenuSubId and <= LastGearMentorMenuSubId;
    }

    public static bool IsUnavailableGearMentorMenuSubId(int subId)
    {
        return IsGearMentorMenuSubId(subId) &&
               !IsOperationSubId(subId) &&
               !IsGearMentorTransactionSubId(subId) &&
               subId != CombineGemPiecesMenuSubId;
    }

    public static byte[] BuildOperationPageResponse(
        uint npcId,
        int dialogIndex,
        int subId)
    {
        if (!IsOperationSubId(subId))
        {
            throw new ArgumentOutOfRangeException(nameof(subId));
        }

        if (dialogIndex is not DialogIndex and not OriginDialogIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(dialogIndex));
        }

        return PacketBuilder.NpcFunctionActionResponse(npcId, dialogIndex, subId);
    }

    public static byte[] BuildGearMentorOperationPageResponse(
        uint npcId,
        int subId)
    {
        if (subId is not (
                DecomposeGearSubId or
                MakeAttributeStoneSubId or
                TransformCrystalSubId))
        {
            throw new ArgumentOutOfRangeException(nameof(subId));
        }

        return PacketBuilder.NpcFunctionActionResponse(npcId, DialogIndex, subId);
    }

    public static byte[] BuildGemPieceCombinationPageResponse(uint npcId)
    {
        return PacketBuilder.NpcFunctionActionResponse(
            npcId,
            DialogIndex,
            CombineGemPiecesActionSubId);
    }

    public static int ResolveGearMentorResultSubId(GearMentorResult? result)
    {
        if (result?.Operation is null)
        {
            return SelectedItemMissingResultSubId;
        }

        return result.Operation.Value switch
        {
            GearMentorOperation.Decompose => result.Status switch
            {
                GearMentorStatus.Succeeded => DecomposeSucceededResultSubId,
                GearMentorStatus.SelectionMissing or
                    GearMentorStatus.RequestMissing => DecomposeNothingSelectedResultSubId,
                GearMentorStatus.PlayerLevelTooLow => DecomposePlayerLevelResultSubId,
                GearMentorStatus.InvalidEquipment => DecomposeInvalidEquipmentResultSubId,
                GearMentorStatus.EquipmentLevelTooLow => DecomposeEquipmentLevelResultSubId,
                GearMentorStatus.InsufficientEquipmentQuality => DecomposeInsufficientQualityResultSubId,
                GearMentorStatus.ClassSuit => ClassSuitResultSubId,
                GearMentorStatus.InsufficientCapacity => BagFullResultSubId,
                GearMentorStatus.StaleSelection or
                    GearMentorStatus.InvalidKitBagSlot => SelectedItemMissingResultSubId,
                _ => InvalidSelectionResultSubId
            },
            GearMentorOperation.MakeAttributeStone => result.Status switch
            {
                GearMentorStatus.Succeeded => MakeStoneSucceededResultSubId,
                GearMentorStatus.SelectionMissing or
                    GearMentorStatus.RequestMissing => MakeStoneNothingSelectedResultSubId,
                GearMentorStatus.InvalidDust => MakeStoneInvalidDustResultSubId,
                GearMentorStatus.InsufficientDust => MakeStoneInsufficientDustResultSubId,
                GearMentorStatus.InsufficientCapacity => BagFullResultSubId,
                GearMentorStatus.StaleSelection or
                    GearMentorStatus.InvalidKitBagSlot => SelectedItemMissingResultSubId,
                _ => InvalidSelectionResultSubId
            },
            GearMentorOperation.TransformCrystal => result.Status switch
            {
                GearMentorStatus.Succeeded => TransformSucceededResultSubId,
                GearMentorStatus.InsufficientCapacity => BagFullResultSubId,
                _ => TransformInvalidCrystalResultSubId
            },
            GearMentorOperation.CombineGemPieces => result.Status switch
            {
                GearMentorStatus.Succeeded => CombineSucceededResultSubId,
                GearMentorStatus.InsufficientGemPieces => CombineInsufficientPiecesResultSubId,
                GearMentorStatus.InsufficientCapacity => CombineBagFullResultSubId,
                _ => CombineInvalidPiecesResultSubId
            },
            _ => InvalidSelectionResultSubId
        };
    }

    public static GearEnhancerSelectionShape ReadSelection(
        IReadOnlyList<int> args,
        out int gearKitBagSlot,
        out int catalystKitBagSlot,
        out int attributeStoneKitBagSlot)
    {
        gearKitBagSlot = -1;
        catalystKitBagSlot = -1;
        attributeStoneKitBagSlot = -1;

        if (args.Count != FunctionActionArgumentCount)
        {
            return GearEnhancerSelectionShape.MenuSelection;
        }

        // The confirmed native enhancer packet has fixed records at 6/7/8 and
        // -1 everywhere else. Menu-navigation packets use a shorter initialized
        // prefix and may contain scratch values in this region, so any non-fixed
        // tail is treated only as navigation and can never mutate inventory.
        for (var index = 0; index < args.Count; index++)
        {
            if (index is GearArgumentIndex or CatalystArgumentIndex or
                AttributeStoneArgumentIndex)
            {
                continue;
            }

            if (args[index] != -1)
            {
                return GearEnhancerSelectionShape.MenuSelection;
            }
        }

        var gearRef = args[GearArgumentIndex];
        var catalystRef = args[CatalystArgumentIndex];
        var stoneRef = args[AttributeStoneArgumentIndex];
        if (gearRef == -1 && catalystRef == -1 && stoneRef == -1)
        {
            return GearEnhancerSelectionShape.MenuSelection;
        }

        if (!TryDecodeOptionalKitBagReference(gearRef, out gearKitBagSlot) ||
            !TryDecodeOptionalKitBagReference(catalystRef, out catalystKitBagSlot) ||
            !TryDecodeOptionalKitBagReference(stoneRef, out attributeStoneKitBagSlot))
        {
            return GearEnhancerSelectionShape.MalformedCommit;
        }

        return GearEnhancerSelectionShape.Commit;
    }

    public static int MissingCatalystResultSubId(GearEnhancementOperation operation)
    {
        return operation switch
        {
            GearEnhancementOperation.Enhance => MissingQuartzPlateResultSubId,
            GearEnhancementOperation.Add => MissingFlameSparkResultSubId,
            GearEnhancementOperation.Delete => MissingWaterGrainResultSubId,
            _ => InvalidSelectionResultSubId
        };
    }

    public static int ResolveResultSubId(
        GearEnhancementOperation operation,
        GearEnhancementResult? result,
        GearEnhancementRequest? request = null)
    {
        if (result is null)
        {
            return InvalidSelectionResultSubId;
        }

        if (result.Status == GearEnhancementStatus.SelectionMissing && request is not null)
        {
            if (request.Gear.ExpectedItem.IsEmpty)
            {
                return MissingGearResultSubId;
            }

            if (request.AttributeStone.ExpectedItem.IsEmpty)
            {
                return MissingAttributeStoneResultSubId;
            }

            if (request.Catalyst.ExpectedItem.IsEmpty)
            {
                return MissingCatalystResultSubId(operation);
            }
        }

        return result.Status switch
        {
            GearEnhancementStatus.Succeeded => operation switch
            {
                GearEnhancementOperation.Enhance => EnhanceSucceededResultSubId,
                GearEnhancementOperation.Add => AddSucceededResultSubId,
                GearEnhancementOperation.Delete => DeleteSucceededResultSubId,
                _ => InvalidSelectionResultSubId
            },
            GearEnhancementStatus.RequestMissing => NothingSelectedResultSubId,
            GearEnhancementStatus.UnsupportedOperation => TemporarilyDisabledResultSubId,
            GearEnhancementStatus.InvalidKitBagSlot => SelectedItemMissingResultSubId,
            GearEnhancementStatus.StaleSelection => SelectedItemMissingResultSubId,
            GearEnhancementStatus.InvalidEquipment or
                GearEnhancementStatus.UnsupportedEquipment => MissingGearResultSubId,
            GearEnhancementStatus.InvalidAttributeStone => MissingAttributeStoneResultSubId,
            GearEnhancementStatus.InvalidCatalyst => MissingCatalystResultSubId(operation),
            GearEnhancementStatus.InsufficientMaterial => operation switch
            {
                GearEnhancementOperation.Enhance => InsufficientEnhanceMaterialsResultSubId,
                GearEnhancementOperation.Add => InsufficientAddMaterialsResultSubId,
                _ => InvalidSelectionResultSubId
            },
            GearEnhancementStatus.AttributeNotAllowed => AttributeNotAllowedResultSubId,
            GearEnhancementStatus.AttributeAlreadyPresent => AttributeAlreadyPresentResultSubId,
            GearEnhancementStatus.AttributeSlotsFull => AttributeSlotsFullResultSubId,
            GearEnhancementStatus.AttributeMissing => operation == GearEnhancementOperation.Delete
                ? MissingDeleteAttributeResultSubId
                : MissingEnhanceAttributeResultSubId,
            GearEnhancementStatus.AttributeAmbiguous => operation switch
            {
                GearEnhancementOperation.Enhance => AttributeNotEnhanceableResultSubId,
                GearEnhancementOperation.Delete => MissingDeleteAttributeResultSubId,
                _ => InvalidSelectionResultSubId
            },
            GearEnhancementStatus.AttributeNotEnhanceable or
                GearEnhancementStatus.AttributeMaximumLevel => AttributeNotEnhanceableResultSubId,
            GearEnhancementStatus.AttributeLevelMismatch => QuartzLevelMismatchResultSubId,
            GearEnhancementStatus.QuartzLevelMismatch => QuartzLevelMismatchResultSubId,
            _ => InvalidSelectionResultSubId
        };
    }

    private static bool TryDecodeOptionalKitBagReference(int value, out int slot)
    {
        if (value == -1)
        {
            slot = -1;
            return true;
        }

        if (value is >= 100 and < 196)
        {
            slot = value - 100;
            return true;
        }

        slot = -1;
        return false;
    }
}

internal enum GearEnhancerSelectionShape
{
    MenuSelection,
    Commit,
    MalformedCommit
}

internal readonly record struct GearEnhancerEndpoint(uint NpcId, string NpcKey);
