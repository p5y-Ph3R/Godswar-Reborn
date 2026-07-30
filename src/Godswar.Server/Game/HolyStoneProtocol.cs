using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal readonly record struct HolyStoneWireIntent(
    HolyStoneCommandOperation Operation,
    HolyStoneTargetLocation TargetLocation,
    int TargetSlot,
    int SocketIndex,
    int StoneKitBagSlot);

internal static class HolyStoneProtocol
{
    public const uint SpartaNpcId = 5083;
    public const uint AthensNpcId = 5225;
    public const int DialogIndex = 30;
    public const int MountSubId = 101;
    public const int RemoveSubId = 201;
    public const int DrillSubId = 301;
    public const int AdvancedDrillSubId = 701;
    public const int MountAliasOneSubId = 106;
    public const int MountAliasTwoSubId = 206;
    public const int MountAliasThreeSubId = 306;
    public const int MountAliasFourSubId = 406;
    public const int FunctionArgumentCount = 18;
    public const int MountScratchArgumentIndex = 0;
    public const int TargetArgumentIndex = 6;
    public const int StoneArgumentIndex = 7;
    public const int RemoveOrdinalArgumentIndex = 10;
    public const int ClientKitBagReferenceBase = 100;
    public const int ClientEquippedWeaponReference = 205;
    public const int PacketBytes = 92;

    public static bool IsEndpoint(uint npcId, int dialogIndex) =>
        dialogIndex == DialogIndex &&
        npcId is SpartaNpcId or AthensNpcId;

    public static bool IsMountNavigation(
        int subId,
        IReadOnlyList<int> args) =>
        subId == MountSubId &&
        args.Count == FunctionArgumentCount &&
        args.All(static value => value == -1);

    public static bool IsExactMountNavigation(GamePacket packet) =>
        IsExactNavigation(packet, MountSubId);

    public static bool IsExactAdvancedDrillNavigation(
        GamePacket packet) =>
        IsExactNavigation(packet, AdvancedDrillSubId);

    public static bool IsAdvancedDrillSubId(int subId) =>
        subId == AdvancedDrillSubId;

    private static bool IsExactNavigation(
        GamePacket packet,
        int expectedSubId)
    {
        if (packet.Opcode != Opcodes.NpcFunctionAction ||
            packet.Length != PacketBytes ||
            packet.Buffer.Length != PacketBytes)
        {
            return false;
        }

        var payload = packet.Payload;
        var npcId =
            BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var dialogIndex =
            BinaryPrimitives.ReadInt32LittleEndian(
                payload.Slice(sizeof(uint), sizeof(int)));
        var duplicateDialog =
            BinaryPrimitives.ReadInt32LittleEndian(
                payload.Slice(
                    sizeof(uint) + sizeof(int),
                    sizeof(int)));
        var subId = BinaryPrimitives.ReadInt32LittleEndian(
            payload.Slice(
                sizeof(uint) + (sizeof(int) * 2),
                sizeof(int)));
        if (!IsEndpoint(npcId, dialogIndex) ||
            duplicateDialog != dialogIndex ||
            subId != expectedSubId)
        {
            return false;
        }

        for (var index = 0;
             index < FunctionArgumentCount;
             index++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(
                    payload.Slice(
                        (sizeof(int) * 4) +
                        (index * sizeof(int)),
                        sizeof(int))) != -1)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsMutationSubId(int subId) =>
        TryResolveBoundaryOperation(subId, out _);

    public static bool TryResolveBoundaryOperation(
        int subId,
        out HolyStoneCommandOperation operation)
    {
        operation = subId switch
        {
            MountSubId or
            MountAliasOneSubId or
            MountAliasTwoSubId or
            MountAliasThreeSubId or
            MountAliasFourSubId =>
                HolyStoneCommandOperation.Mount,
            RemoveSubId => HolyStoneCommandOperation.Remove,
            DrillSubId => HolyStoneCommandOperation.Drill,
            _ => default
        };
        return Enum.IsDefined(operation);
    }

    public static bool TryReadMutation(
        GamePacket packet,
        out uint npcId,
        out int dialogIndex,
        out HolyStoneWireIntent intent)
    {
        npcId = 0;
        dialogIndex = 0;
        intent = default;
        if (packet.Opcode != Opcodes.NpcFunctionAction ||
            packet.Length != PacketBytes ||
            packet.Buffer.Length != PacketBytes)
        {
            return false;
        }

        var payload = packet.Payload;
        npcId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        dialogIndex = BinaryPrimitives.ReadInt32LittleEndian(
            payload.Slice(sizeof(uint), sizeof(int)));
        var duplicateDialog =
            BinaryPrimitives.ReadInt32LittleEndian(
                payload.Slice(sizeof(uint) + sizeof(int), sizeof(int)));
        var subId = BinaryPrimitives.ReadInt32LittleEndian(
            payload.Slice(
                sizeof(uint) + (sizeof(int) * 2),
                sizeof(int)));
        if (dialogIndex != DialogIndex ||
            duplicateDialog != dialogIndex)
        {
            return false;
        }

        var args = new int[FunctionArgumentCount];
        for (var index = 0; index < args.Length; index++)
        {
            args[index] = BinaryPrimitives.ReadInt32LittleEndian(
                payload.Slice(
                    (sizeof(int) * 4) + (index * sizeof(int)),
                    sizeof(int)));
        }

        return TryReadMutation(
            npcId,
            dialogIndex,
            subId,
            args,
            out intent);
    }

    public static bool TryReadMutation(
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> args,
        out HolyStoneWireIntent intent)
    {
        intent = default;
        if (!IsEndpoint(npcId, dialogIndex) ||
            args.Count != FunctionArgumentCount ||
            !TryResolveOperation(subId, out var operation) ||
            !TryDecodeTarget(
                args[TargetArgumentIndex],
                out var targetLocation,
                out var targetSlot))
        {
            return false;
        }

        var socketIndex =
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex;
        var stoneKitBagSlot =
            HolyStoneCommandEnvelope.NoStoneKitBagSlot;
        switch (operation)
        {
            case HolyStoneCommandOperation.Mount:
                if (args[MountScratchArgumentIndex] != 0 ||
                    !TryDecodeKitBagReference(
                        args[StoneArgumentIndex],
                        out stoneKitBagSlot) ||
                    targetLocation ==
                        HolyStoneTargetLocation.KitBag &&
                    targetSlot == stoneKitBagSlot ||
                    !OnlyArgumentsUsed(
                        args,
                        MountScratchArgumentIndex,
                        TargetArgumentIndex,
                        StoneArgumentIndex))
                {
                    return false;
                }
                break;

            case HolyStoneCommandOperation.Remove:
                var oneBasedSocket =
                    args[RemoveOrdinalArgumentIndex];
                if (oneBasedSocket is < 1 or > 4 ||
                    !OnlyArgumentsUsed(
                        args,
                        TargetArgumentIndex,
                        RemoveOrdinalArgumentIndex))
                {
                    return false;
                }
                socketIndex = oneBasedSocket - 1;
                break;

            case HolyStoneCommandOperation.Drill:
                if (!OnlyArgumentsUsed(
                        args,
                        TargetArgumentIndex))
                {
                    return false;
                }
                break;

            default:
                return false;
        }

        intent = new HolyStoneWireIntent(
            operation,
            targetLocation,
            targetSlot,
            socketIndex,
            stoneKitBagSlot);
        return true;
    }

    public static CommandFamily Family(
        HolyStoneCommandOperation operation) =>
        HolyStoneCommandEnvelope.Family(operation);

    public static bool TryResolveOperation(
        int subId,
        out HolyStoneCommandOperation operation)
    {
        operation = subId switch
        {
            MountSubId => HolyStoneCommandOperation.Mount,
            RemoveSubId => HolyStoneCommandOperation.Remove,
            DrillSubId => HolyStoneCommandOperation.Drill,
            _ => default
        };
        return Enum.IsDefined(operation);
    }

    private static bool TryDecodeTarget(
        int reference,
        out HolyStoneTargetLocation location,
        out int slot)
    {
        if (reference == ClientEquippedWeaponReference)
        {
            location = HolyStoneTargetLocation.Equipment;
            slot = HolyStoneCommandEnvelope.WeaponEquipmentSlot;
            return true;
        }
        if (TryDecodeKitBagReference(reference, out slot))
        {
            location = HolyStoneTargetLocation.KitBag;
            return true;
        }

        location = default;
        slot = -1;
        return false;
    }

    private static bool TryDecodeKitBagReference(
        int reference,
        out int slot)
    {
        slot = reference - ClientKitBagReferenceBase;
        return slot is
            >= HolyStoneCommandEnvelope.MinimumKitBagSlot and
            <= HolyStoneCommandEnvelope.MaximumKitBagSlot;
    }

    private static bool OnlyArgumentsUsed(
        IReadOnlyList<int> args,
        params int[] usedIndexes)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (usedIndexes.Contains(index))
            {
                continue;
            }
            if (args[index] != -1)
            {
                return false;
            }
        }

        return true;
    }
}
