using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal enum HolySuitWireOperation
{
    StoreExperience = 30,
    TransferExperience = 31,
    ConsumeWare = 32,
    TransformExperience = 33
}

internal enum HolySuitWireRejectionReason : byte
{
    None = 0,
    ActionShape = 1,
    UnknownOperation = 2,
    InvalidItemReference = 3,
    MissingAmount = 4,
    DuplicateItemReference = 5,
    UnexpectedArgument = 6
}

internal readonly record struct HolySuitWireRejection(
    HolySuitWireRejectionReason Reason,
    int ArgumentIndex = -1);

internal readonly record struct HolySuitWireIntent(
    HolySuitWireOperation Operation,
    int EquipmentKitBagSlot,
    int HolyBoxKitBagSlot,
    int WareKitBagSlot,
    long Amount);

internal static partial class HolySuitDesignProtocol
{
    // City NPC object IDs follow Sparta=4997+number and Athens=5139+number.
    public const uint SpartaNpcId = 5082;
    public const uint AthensNpcId = 5224;
    public const int DialogIndex = 29;
    public const int InitialMenuRequestSubId = -1;

    public const int StoreExperienceSubId = 101;
    public const int TransferExperienceSubId = 201;
    public const int ConsumeEquipmentSubId = 301;
    public const int ConsumeWareSubId = ConsumeEquipmentSubId;
    public const int TransformExperienceSubId = 401;

    public const int StoreExperiencePageSubId = 106;
    public const int TransferExperiencePageSubId = 206;
    public const int ConsumeWarePageSubId = 306;
    public const int ConsumeWareEquipmentInstructionSubId = 406;
    public const int ConsumeWareMaterialInstructionSubId = 506;
    public const int ConsumeWareSpacerSubId = 606;
    public const int TransformExperiencePageSubId = 706;

    public const int StoreAmountRequiredResultSubId = 999;
    public const int PrismAmountRequiredResultSubId = 888;
    public const int WrongStoreSelectionResultSubId = 100;
    public const int InsufficientExperienceResultSubId = 200;
    public const int StoreRequiresHolyBoxResultSubId = 300;
    public const int StoreSucceededResultSubId = 400;
    public const int WrongTransferSelectionResultSubId = 500;
    public const int TransferRequiresEquipmentResultSubId = 600;
    public const int HolyBoxEmptyResultSubId = 700;
    public const int TransferSucceededResultSubId = 800;
    public const int TransferFailedOrEquipmentFullResultSubId = 900;
    public const int InsufficientEquipmentExperienceResultSubId = 1000;
    public const int EquipmentMaximumTierResultSubId = 1100;
    public const int WareRequiredResultSubId = 1200;
    public const int ConsumeWareSucceededResultSubId = 1300;
    public const int ConsumeWareFailedResultSubId = 1400;
    public const int WareTierMismatchResultSubId = 1500;
    public const int InsufficientWareResultSubId = 1600;
    public const int TransferRequiresHolyBoxResultSubId = 1700;
    public const int HolyBoxFullResultSubId = 1800;
    public const int StoreOperationLimitResultSubId = 1900;
    public const int StoreDailyLimitResultSubId = 2000;
    public const int TransformSucceededResultSubId = 2100;
    public const int InsufficientBoundGoldResultSubId = 2200;
    public const int BagFullResultSubId = 2300;
    public const int StoreLevelTooLowResultSubId = 2101;
    public const int TransformLevelTooLowResultSubId = 2102;
    public const int EquipmentExperienceMaximumResultSubId = 10001;

    public const int TemporarilyDisabledResultSubId =
        StoreAmountRequiredResultSubId;

    public const int PacketBytes = 92;
    public const int FunctionArgumentCount = 18;
    public const int FirstItemArgumentIndex = 6;
    public const int SecondItemArgumentIndex = 7;
    public const int AmountArgumentIndex = 10;
    public const int NoKitBagSlot = -1;

    public const int TransferredTodayCounterSuffix = 4;
    public const int TransferCreditCounterSuffix = 5;
    public const int RemainingCreditCounterSuffix = 6;
    public const int UpdatedTransferredCounterSuffix = 7;
    public const int MaximumEncodedCounter =
        (int.MaxValue - UpdatedTransferredCounterSuffix) / 10;

    public static bool IsNpcKey(string npcKey) =>
        npcKey is "Sparta_085" or "Athens_085";

    public static bool IsEndpoint(uint npcId, int dialogIndex) =>
        dialogIndex == DialogIndex &&
        npcId is SpartaNpcId or AthensNpcId;

    public static bool IsMenuSubId(int subId) =>
        TryResolveOperation(subId, out _);

    public static bool TryResolveOperation(
        int subId,
        out HolySuitWireOperation operation)
    {
        operation = subId switch
        {
            StoreExperienceSubId =>
                HolySuitWireOperation.StoreExperience,
            TransferExperienceSubId =>
                HolySuitWireOperation.TransferExperience,
            ConsumeWareSubId => HolySuitWireOperation.ConsumeWare,
            TransformExperienceSubId =>
                HolySuitWireOperation.TransformExperience,
            _ => default
        };
        return Enum.IsDefined(operation);
    }

    public static bool IsExactNavigation(
        GamePacket packet,
        int expectedSubId)
    {
        if (!TryReadAction(
                packet,
                out _,
                out var subId,
                out var arguments) ||
            subId != expectedSubId ||
            !IsMenuSubId(subId))
        {
            return false;
        }

        return arguments.All(static argument => argument == -1);
    }

    public static bool TryReadMutation(
        GamePacket packet,
        out uint npcId,
        out int dialogIndex,
        out HolySuitWireIntent intent)
    {
        return TryReadMutation(
            packet,
            out npcId,
            out dialogIndex,
            out intent,
            out _);
    }

    public static bool TryReadMutation(
        GamePacket packet,
        out uint npcId,
        out int dialogIndex,
        out HolySuitWireIntent intent,
        out HolySuitWireRejection rejection)
    {
        npcId = 0;
        dialogIndex = 0;
        intent = default;
        rejection = default;
        if (!TryReadAction(
                packet,
                out npcId,
                out var subId,
                out var arguments))
        {
            rejection = new HolySuitWireRejection(
                HolySuitWireRejectionReason.ActionShape);
            return false;
        }
        if (!TryResolveOperation(subId, out var operation))
        {
            rejection = new HolySuitWireRejection(
                HolySuitWireRejectionReason.UnknownOperation);
            return false;
        }

        dialogIndex = DialogIndex;
        var equipmentSlot = NoKitBagSlot;
        var holyBoxSlot = NoKitBagSlot;
        var wareSlot = NoKitBagSlot;
        long amount = 0;

        switch (operation)
        {
            case HolySuitWireOperation.StoreExperience:
                if (!TryDecodeKitBagReference(
                        arguments[FirstItemArgumentIndex],
                        out holyBoxSlot))
                {
                    rejection = InvalidItemReference(
                        FirstItemArgumentIndex);
                    return false;
                }
                if (!TryReadStoreAmount(arguments, out amount))
                {
                    rejection = MissingAmount();
                    return false;
                }
                if (!OnlyArgumentsUsed(
                        arguments,
                        out var unexpectedStoreArgument,
                        FirstItemArgumentIndex,
                        AmountArgumentIndex))
                {
                    rejection = UnexpectedArgument(
                        unexpectedStoreArgument);
                    return false;
                }
                break;

            case HolySuitWireOperation.TransferExperience:
                if (!TryDecodeKitBagReference(
                        arguments[FirstItemArgumentIndex],
                        out equipmentSlot))
                {
                    rejection = InvalidItemReference(
                        FirstItemArgumentIndex);
                    return false;
                }
                if (!TryDecodeKitBagReference(
                        arguments[SecondItemArgumentIndex],
                        out holyBoxSlot))
                {
                    rejection = InvalidItemReference(
                        SecondItemArgumentIndex);
                    return false;
                }
                if (equipmentSlot == holyBoxSlot)
                {
                    rejection = DuplicateItemReference();
                    return false;
                }
                if (!OnlyArgumentsUsed(
                        arguments,
                        out var unexpectedTransferArgument,
                        FirstItemArgumentIndex,
                        SecondItemArgumentIndex))
                {
                    rejection = UnexpectedArgument(
                        unexpectedTransferArgument);
                    return false;
                }
                break;

            case HolySuitWireOperation.ConsumeWare:
                if (!TryDecodeKitBagReference(
                        arguments[FirstItemArgumentIndex],
                        out equipmentSlot))
                {
                    rejection = InvalidItemReference(
                        FirstItemArgumentIndex);
                    return false;
                }
                if (!TryDecodeKitBagReference(
                        arguments[SecondItemArgumentIndex],
                        out wareSlot))
                {
                    rejection = InvalidItemReference(
                        SecondItemArgumentIndex);
                    return false;
                }
                if (equipmentSlot == wareSlot)
                {
                    rejection = DuplicateItemReference();
                    return false;
                }
                if (!OnlyArgumentsUsed(
                        arguments,
                        out var unexpectedWareArgument,
                        FirstItemArgumentIndex,
                        SecondItemArgumentIndex))
                {
                    rejection = UnexpectedArgument(
                        unexpectedWareArgument);
                    return false;
                }
                break;

            case HolySuitWireOperation.TransformExperience:
                if (!TryReadTransformPrismCount(arguments, out amount))
                {
                    rejection = MissingAmount();
                    return false;
                }
                if (!OnlyArgumentsUsed(
                        arguments,
                        out var unexpectedTransformArgument,
                        AmountArgumentIndex))
                {
                    rejection = UnexpectedArgument(
                        unexpectedTransformArgument);
                    return false;
                }
                break;

            default:
                rejection = new HolySuitWireRejection(
                    HolySuitWireRejectionReason.UnknownOperation);
                return false;
        }

        intent = new HolySuitWireIntent(
            operation,
            equipmentSlot,
            holyBoxSlot,
            wareSlot,
            amount);
        return true;
    }

    public static byte[] BuildInitialMenuResponse(uint npcId)
    {
        EnsureEndpoint(npcId);
        return PacketBuilder.NpcFunctionActionResponse(
            npcId,
            DialogIndex,
            StoreExperienceSubId,
            TransferExperienceSubId,
            ConsumeWareSubId,
            TransformExperienceSubId);
    }

    public static byte[] BuildOperationPageResponse(
        uint npcId,
        HolySuitWireOperation operation)
    {
        EnsureEndpoint(npcId);
        return operation switch
        {
            HolySuitWireOperation.StoreExperience =>
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    DialogIndex,
                    StoreExperiencePageSubId),
            HolySuitWireOperation.TransferExperience =>
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    DialogIndex,
                    TransferExperiencePageSubId),
            HolySuitWireOperation.ConsumeWare =>
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    DialogIndex,
                    ConsumeWarePageSubId,
                    ConsumeWareEquipmentInstructionSubId,
                    ConsumeWareMaterialInstructionSubId,
                    ConsumeWareSpacerSubId),
            HolySuitWireOperation.TransformExperience =>
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    DialogIndex,
                    TransformExperiencePageSubId),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    public static byte[] BuildStorePageResponse(
        uint npcId,
        int transferredToday,
        int transferCredit)
    {
        EnsureEndpoint(npcId);
        var today = EncodeCounter(
            transferredToday,
            TransferredTodayCounterSuffix,
            nameof(transferredToday));
        var credit = EncodeCounter(
            transferCredit,
            TransferCreditCounterSuffix,
            nameof(transferCredit));
        return PacketBuilder.NpcFunctionActionResponse(
            npcId,
            DialogIndex,
            StoreExperiencePageSubId,
            today,
            credit);
    }

    public static byte[] BuildResultResponse(
        uint npcId,
        int resultSubId)
    {
        EnsureEndpoint(npcId);
        if (!IsResultSubId(resultSubId))
        {
            throw new ArgumentOutOfRangeException(nameof(resultSubId));
        }

        return PacketBuilder.NpcFunctionActionResponse(
            npcId,
            DialogIndex,
            resultSubId);
    }

    public static bool IsResultSubId(int subId) =>
        subId is StoreAmountRequiredResultSubId or
            PrismAmountRequiredResultSubId or
            StoreLevelTooLowResultSubId or
            TransformLevelTooLowResultSubId or
            EquipmentExperienceMaximumResultSubId ||
        subId is >= WrongStoreSelectionResultSubId and
            <= BagFullResultSubId &&
        subId % 100 == 0;

    public static bool TryEncodeCounter(
        int value,
        int suffix,
        out int subId)
    {
        if (value is < 0 or > MaximumEncodedCounter ||
            suffix is < TransferredTodayCounterSuffix or
                > UpdatedTransferredCounterSuffix)
        {
            subId = 0;
            return false;
        }

        subId = checked((value * 10) + suffix);
        return true;
    }

    /// <summary>
    /// The stock client embeds displayed EXP counters in a signed 32-bit
    /// sub-ID. Larger authoritative values remain enforced server-side and
    /// are displayed as the largest representable counter.
    /// </summary>
    public static int ClampDisplayCounter(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return checked((int)Math.Min(value, MaximumEncodedCounter));
    }

    private static bool TryReadAction(
        GamePacket packet,
        out uint npcId,
        out int subId,
        out int[] arguments)
    {
        npcId = 0;
        subId = 0;
        arguments = [];
        if (packet.Opcode != Opcodes.NpcFunctionAction ||
            packet.Length != PacketBytes ||
            packet.Buffer.Length != PacketBytes)
        {
            return false;
        }

        var payload = packet.Payload;
        npcId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var dialogIndex = BinaryPrimitives.ReadInt32LittleEndian(
            payload.Slice(sizeof(uint), sizeof(int)));
        var duplicateDialog = BinaryPrimitives.ReadInt32LittleEndian(
            payload.Slice(sizeof(uint) + sizeof(int), sizeof(int)));
        subId = BinaryPrimitives.ReadInt32LittleEndian(
            payload.Slice(sizeof(uint) + (sizeof(int) * 2), sizeof(int)));
        if (!IsEndpoint(npcId, dialogIndex) ||
            duplicateDialog != dialogIndex)
        {
            return false;
        }

        arguments = new int[FunctionArgumentCount];
        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = BinaryPrimitives.ReadInt32LittleEndian(
                payload.Slice(
                    (sizeof(int) * 4) + (index * sizeof(int)),
                    sizeof(int)));
        }
        return true;
    }

    private static bool OnlyArgumentsUsed(
        IReadOnlyList<int> arguments,
        out int unexpectedIndex,
        params int[] usedIndexes)
    {
        unexpectedIndex = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (usedIndexes.Contains(index) || arguments[index] == -1)
            {
                continue;
            }

            // The stock NPC UI writes its unchecked first button as zero.
            // It is presentation scratch, not an operation parameter.
            if (index == 0 && arguments[index] == 0)
            {
                continue;
            }

            unexpectedIndex = index;
            return false;
        }
        return true;
    }

    private static HolySuitWireRejection InvalidItemReference(
        int argumentIndex) =>
        new(
            HolySuitWireRejectionReason.InvalidItemReference,
            argumentIndex);

    private static HolySuitWireRejection MissingAmount() =>
        new(
            HolySuitWireRejectionReason.MissingAmount,
            AmountArgumentIndex);

    private static HolySuitWireRejection DuplicateItemReference() =>
        new(HolySuitWireRejectionReason.DuplicateItemReference);

    private static HolySuitWireRejection UnexpectedArgument(
        int argumentIndex) =>
        new(
            HolySuitWireRejectionReason.UnexpectedArgument,
            argumentIndex);

    private static int EncodeCounter(
        int value,
        int suffix,
        string parameterName)
    {
        if (!TryEncodeCounter(value, suffix, out var subId))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return subId;
    }

    private static void EnsureEndpoint(uint npcId)
    {
        if (!IsEndpoint(npcId, DialogIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(npcId));
        }
    }
}
