using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal enum ClassSuitWireOperation
{
    ExchangeTierOne = 100,
    AddClassAttribute = 101,
    DeleteClassAttribute = 102,
    Instructions = 103,
    ConvertToCommon = 104,
    UpgradeTierTwo = 105,
    UpgradeTierThree = 106,
    AddFifthAttribute = 107,
    UpgradeTierFour = 108
}

internal readonly record struct ClassSuitWireIntent(
    ClassSuitWireOperation Operation,
    int EquipmentKitBagSlot,
    int MaterialKitBagSlot,
    int SecondaryMaterialKitBagSlot = -1);

/// <summary>
/// Bounded parser and stock-client response catalog for NPC function 37.
/// The conversion target, cost, class, and level are deliberately absent
/// from the wire intent; authoritative server rules derive all of them.
/// </summary>
internal static class ClassSuitProtocol
{
    public const uint SpartaNpcId = 5067;
    public const uint AthensNpcId = 5209;
    public const int DialogIndex = 37;
    public const int InitialMenuRequestSubId = -1;
    public const int PacketBytes = 92;
    public const int FunctionArgumentCount = 18;
    public const int EquipmentArgumentIndex = 6;
    public const int MaterialArgumentIndex = 7;
    public const int SecondaryMaterialArgumentIndex = 8;
    public const int MinimumKitBagReference = 100;
    public const int MaximumKitBagReference = 195;
    public const int NoKitBagSlot = -1;

    public static readonly int[] InitialMenuSubIds =
    [100, 101, 102, 103, 104, 105, 106, 107, 108];

    public static bool IsNpcKey(string npcKey) =>
        npcKey is "Sparta_070" or "Athens_070";

    public static bool IsEndpoint(uint npcId, int dialogIndex) =>
        dialogIndex == DialogIndex &&
        npcId is SpartaNpcId or AthensNpcId;

    public static bool IsConversionOperation(int subId) =>
        subId is
            (int)ClassSuitWireOperation.ExchangeTierOne or
            (int)ClassSuitWireOperation.ConvertToCommon or
            (int)ClassSuitWireOperation.UpgradeTierTwo or
            (int)ClassSuitWireOperation.UpgradeTierThree or
            (int)ClassSuitWireOperation.UpgradeTierFour;

    public static bool TryResolveOperation(
        int subId,
        out ClassSuitWireOperation operation)
    {
        operation = (ClassSuitWireOperation)subId;
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
            !TryResolveOperation(subId, out _))
        {
            return false;
        }

        return arguments.All(static value => value == -1) ||
            arguments[0] == 0 &&
            arguments.Skip(1).All(static value => value == -1);
    }

    public static bool TryReadConversionMutation(
        GamePacket packet,
        out uint npcId,
        out ClassSuitWireIntent intent)
    {
        npcId = 0;
        intent = default;
        if (!TryReadAction(
                packet,
                out npcId,
                out var subId,
                out var arguments) ||
            !IsConversionOperation(subId) ||
            !TryDecodeKitBagReference(
                arguments[EquipmentArgumentIndex],
                out var equipmentSlot))
        {
            return false;
        }

        var operation = (ClassSuitWireOperation)subId;
        var materialSlot = NoKitBagSlot;
        if (operation != ClassSuitWireOperation.ConvertToCommon &&
            !TryDecodeKitBagReference(
                arguments[MaterialArgumentIndex],
                out materialSlot))
        {
            return false;
        }
        if (equipmentSlot == materialSlot)
        {
            return false;
        }

        for (var index = 0; index < arguments.Length; index++)
        {
            if (index == EquipmentArgumentIndex ||
                operation != ClassSuitWireOperation.ConvertToCommon &&
                index == MaterialArgumentIndex ||
                index == 0 && arguments[index] == 0)
            {
                continue;
            }
            if (arguments[index] != -1)
            {
                return false;
            }
        }

        intent = new ClassSuitWireIntent(
            operation,
            equipmentSlot,
            materialSlot);
        return true;
    }

    public static bool TryReadMutation(
        GamePacket packet,
        out uint npcId,
        out ClassSuitWireIntent intent)
    {
        if (TryReadConversionMutation(packet, out npcId, out intent))
        {
            return true;
        }

        npcId = 0;
        intent = default;
        if (!TryReadAction(
                packet,
                out npcId,
                out var subId,
                out var arguments) ||
            subId is not
                (int)ClassSuitWireOperation.AddClassAttribute and not
                (int)ClassSuitWireOperation.DeleteClassAttribute ||
            !TryDecodeKitBagReference(
                arguments[EquipmentArgumentIndex],
                out var equipmentSlot) ||
            !TryDecodeKitBagReference(
                arguments[MaterialArgumentIndex],
                out var materialSlot))
        {
            return false;
        }

        var operation = (ClassSuitWireOperation)subId;
        var secondarySlot = NoKitBagSlot;
        if (operation == ClassSuitWireOperation.AddClassAttribute &&
            !TryDecodeKitBagReference(
                arguments[SecondaryMaterialArgumentIndex],
                out secondarySlot))
        {
            return false;
        }

        var selected = new[]
        {
            equipmentSlot,
            materialSlot,
            secondarySlot
        }.Where(static slot => slot >= 0).ToArray();
        if (selected.Distinct().Count() != selected.Length)
        {
            return false;
        }

        for (var index = 0; index < arguments.Length; index++)
        {
            var isSelection = index == EquipmentArgumentIndex ||
                index == MaterialArgumentIndex ||
                operation == ClassSuitWireOperation.AddClassAttribute &&
                index == SecondaryMaterialArgumentIndex;
            if (isSelection ||
                index == 0 && arguments[index] == 0)
            {
                continue;
            }
            if (arguments[index] != -1)
            {
                return false;
            }
        }

        intent = new ClassSuitWireIntent(
            operation,
            equipmentSlot,
            materialSlot,
            secondarySlot);
        return true;
    }

    public static byte[] BuildInitialMenuResponse(uint npcId)
    {
        EnsureEndpoint(npcId);
        return PacketBuilder.NpcFunctionActionResponse(
            npcId,
            DialogIndex,
            InitialMenuSubIds);
    }

    public static byte[] BuildOperationPageResponse(
        uint npcId,
        ClassSuitWireOperation operation)
    {
        EnsureEndpoint(npcId);
        var subIds = operation switch
        {
            ClassSuitWireOperation.ExchangeTierOne =>
                new[] { 110, 111, 119 },
            ClassSuitWireOperation.AddClassAttribute =>
                new[] { 112, 113, 114 },
            ClassSuitWireOperation.DeleteClassAttribute =>
                new[] { 115, 116, 117 },
            ClassSuitWireOperation.Instructions => new[] { 118 },
            ClassSuitWireOperation.ConvertToCommon => new[] { 120 },
            ClassSuitWireOperation.UpgradeTierTwo =>
                new[] { 201, 202, 203 },
            ClassSuitWireOperation.UpgradeTierThree =>
                new[] { 130, 131, 132 },
            ClassSuitWireOperation.AddFifthAttribute => new[] { 123 },
            ClassSuitWireOperation.UpgradeTierFour =>
                new[] { 140, 141, 142 },
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return PacketBuilder.NpcFunctionActionResponse(
            npcId,
            DialogIndex,
            subIds);
    }

    public static byte[] BuildResultResponse(uint npcId, int resultSubId)
    {
        EnsureEndpoint(npcId);
        if (resultSubId is <= 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(resultSubId));
        }
        return PacketBuilder.NpcFunctionActionResponse(
            npcId,
            DialogIndex,
            resultSubId);
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

    private static bool TryDecodeKitBagReference(
        int encoded,
        out int slot)
    {
        slot = encoded - MinimumKitBagReference;
        return encoded is >= MinimumKitBagReference and
            <= MaximumKitBagReference;
    }

    private static void EnsureEndpoint(uint npcId)
    {
        if (!IsEndpoint(npcId, DialogIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(npcId));
        }
    }
}
