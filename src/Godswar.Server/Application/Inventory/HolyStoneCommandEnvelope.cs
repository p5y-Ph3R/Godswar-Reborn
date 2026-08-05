using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct HolyStoneCommand(
    HolyStoneOperationIdentity Identity,
    HolyStoneCommandOperation Operation,
    int NpcId,
    int DialogIndex,
    HolyStoneTargetLocation TargetLocation,
    int TargetSlot,
    string ExpectedTargetCompactItemState,
    int SocketIndex,
    int StoneKitBagSlot,
    string ExpectedStoneCompactItemState,
    int CatalystKitBagSlot = -1,
    string ExpectedCatalystCompactItemState = "[]",
    int ThirdMaterialKitBagSlot = -1,
    string ExpectedThirdMaterialCompactItemState = "[]")
{
    public Guid ClientOperationId => Identity.OperationId;
}

internal static partial class HolyStoneCommandEnvelope
{
    public const int SpartaNpcId = 5083;
    public const int AthensNpcId = 5225;
    public const int DialogIndex = 30;
    public const int WeaponEquipmentSlot = 10;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MinimumSocketIndex = 0;
    public const int MaximumSocketIndex = 3;
    public const int ServerSelectedSocketIndex = -1;
    public const int NoStoneKitBagSlot = -1;
    public const int MaximumCompactItemStateUtf8Bytes = 512;
    public const int MaximumCombinedStateUtf8Bytes = 900;
    public const int MaximumUpgradeCombinedStateUtf8Bytes = 1_400;
    public const int MaximumCombinationCombinedStateUtf8Bytes = 1_900;
    public const ushort CanonicalRequestVersion = 2;
    public const ushort UpgradeCanonicalRequestVersion = 3;
    public const ushort CombinationCanonicalRequestVersion = 4;

    private const int OperationScopeBytes = 16;
    private const int StateDigestBytes = 32;
    private const int CanonicalArtisanEndpoint = 1;
    private const byte TargetStateRole = 1;
    private const byte StoneStateRole = 2;
    private const byte CatalystStateRole = 3;
    private const byte ThirdMaterialStateRole = 4;
    private const int CanonicalRequestBytes =
        sizeof(ushort) + sizeof(byte) + sizeof(int) + sizeof(int) +
        sizeof(byte) + sizeof(short) + sizeof(short) + sizeof(short) +
        StateDigestBytes;
    private const int UpgradeCanonicalRequestBytes =
        CanonicalRequestBytes + sizeof(short);
    private const int CombinationCanonicalRequestBytes =
        UpgradeCanonicalRequestBytes + sizeof(short);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        HolyStoneCommandOperation operation,
        int npcId,
        int dialogIndex,
        HolyStoneTargetLocation targetLocation,
        int targetSlot,
        string? expectedTargetCompactItemState,
        int socketIndex,
        int stoneKitBagSlot,
        string? expectedStoneCompactItemState,
        out HolyStoneCommand command)
    {
        return TryCreateCommand(
            HolyStoneOperationIdentity.SecureClient(clientOperationId),
            operation,
            npcId,
            dialogIndex,
            targetLocation,
            targetSlot,
            expectedTargetCompactItemState,
            socketIndex,
            stoneKitBagSlot,
            expectedStoneCompactItemState,
            NoStoneKitBagSlot,
            "[]",
            out command);
    }

    public static bool TryCreateCommand(
        Guid clientOperationId,
        HolyStoneCommandOperation operation,
        int npcId,
        int dialogIndex,
        HolyStoneTargetLocation targetLocation,
        int targetSlot,
        string? expectedTargetCompactItemState,
        int socketIndex,
        int stoneKitBagSlot,
        string? expectedStoneCompactItemState,
        int catalystKitBagSlot,
        string? expectedCatalystCompactItemState,
        out HolyStoneCommand command)
    {
        return TryCreateCommand(
            HolyStoneOperationIdentity.SecureClient(clientOperationId),
            operation,
            npcId,
            dialogIndex,
            targetLocation,
            targetSlot,
            expectedTargetCompactItemState,
            socketIndex,
            stoneKitBagSlot,
            expectedStoneCompactItemState,
            catalystKitBagSlot,
            expectedCatalystCompactItemState,
            NoStoneKitBagSlot,
            "[]",
            out command);
    }

    public static bool TryCreateCommand(
        Guid clientOperationId,
        HolyStoneCommandOperation operation,
        int npcId,
        int dialogIndex,
        HolyStoneTargetLocation targetLocation,
        int targetSlot,
        string? expectedTargetCompactItemState,
        int socketIndex,
        int stoneKitBagSlot,
        string? expectedStoneCompactItemState,
        int catalystKitBagSlot,
        string? expectedCatalystCompactItemState,
        int thirdMaterialKitBagSlot,
        string? expectedThirdMaterialCompactItemState,
        out HolyStoneCommand command)
    {
        return TryCreateCommand(
            HolyStoneOperationIdentity.SecureClient(clientOperationId),
            operation,
            npcId,
            dialogIndex,
            targetLocation,
            targetSlot,
            expectedTargetCompactItemState,
            socketIndex,
            stoneKitBagSlot,
            expectedStoneCompactItemState,
            catalystKitBagSlot,
            expectedCatalystCompactItemState,
            thirdMaterialKitBagSlot,
            expectedThirdMaterialCompactItemState,
            out command);
    }

    public static bool TryCreateCommand(
        HolyStoneOperationIdentity identity,
        HolyStoneCommandOperation operation,
        int npcId,
        int dialogIndex,
        HolyStoneTargetLocation targetLocation,
        int targetSlot,
        string? expectedTargetCompactItemState,
        int socketIndex,
        int stoneKitBagSlot,
        string? expectedStoneCompactItemState,
        int catalystKitBagSlot,
        string? expectedCatalystCompactItemState,
        out HolyStoneCommand command)
    {
        return TryCreateCommand(
            identity,
            operation,
            npcId,
            dialogIndex,
            targetLocation,
            targetSlot,
            expectedTargetCompactItemState,
            socketIndex,
            stoneKitBagSlot,
            expectedStoneCompactItemState,
            catalystKitBagSlot,
            expectedCatalystCompactItemState,
            NoStoneKitBagSlot,
            "[]",
            out command);
    }

    public static bool TryCreateCommand(
        HolyStoneOperationIdentity identity,
        HolyStoneCommandOperation operation,
        int npcId,
        int dialogIndex,
        HolyStoneTargetLocation targetLocation,
        int targetSlot,
        string? expectedTargetCompactItemState,
        int socketIndex,
        int stoneKitBagSlot,
        string? expectedStoneCompactItemState,
        int catalystKitBagSlot,
        string? expectedCatalystCompactItemState,
        int thirdMaterialKitBagSlot,
        string? expectedThirdMaterialCompactItemState,
        out HolyStoneCommand command)
    {
        command = new HolyStoneCommand(
            identity,
            operation,
            npcId,
            dialogIndex,
            targetLocation,
            targetSlot,
            expectedTargetCompactItemState ?? string.Empty,
            socketIndex,
            stoneKitBagSlot,
            expectedStoneCompactItemState ?? string.Empty,
            catalystKitBagSlot,
            expectedCatalystCompactItemState ?? string.Empty,
            thirdMaterialKitBagSlot,
            expectedThirdMaterialCompactItemState ?? string.Empty);
        if (IsValidCommand(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandFamily Family(
        HolyStoneCommandOperation operation) =>
        operation switch
        {
            HolyStoneCommandOperation.Mount =>
                CommandFamily.HolyStoneMount,
            HolyStoneCommandOperation.Remove =>
                CommandFamily.HolyStoneRemove,
            HolyStoneCommandOperation.Drill =>
                CommandFamily.HolyStoneDrill,
            HolyStoneCommandOperation.AdvancedDrill =>
                CommandFamily.HolyStoneAdvancedDrill,
            HolyStoneCommandOperation.Upgrade =>
                CommandFamily.HolyStoneUpgrade,
            HolyStoneCommandOperation.Combine =>
                CommandFamily.HolyStoneCombine,
            HolyStoneCommandOperation.ImplementSpirit =>
                CommandFamily.HolyStoneImplementSpirit,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static bool IsEndpoint(int npcId, int dialogIndex) =>
        dialogIndex == DialogIndex &&
        npcId is SpartaNpcId or AthensNpcId;

    public static bool AreEquivalentEndpoints(
        int firstNpcId,
        int firstDialogIndex,
        int secondNpcId,
        int secondDialogIndex) =>
        IsEndpoint(firstNpcId, firstDialogIndex) &&
        IsEndpoint(secondNpcId, secondDialogIndex);

    private static bool IsValidCommand(HolyStoneCommand command)
    {
        if (!IsValidIdentity(command.Identity) ||
            (command.Identity.IsRawLocalServer &&
             !SupportsRawLocalIdentity(command.Operation)) ||
            !Enum.IsDefined(command.Operation) ||
            !IsEndpoint(command.NpcId, command.DialogIndex) ||
            !Enum.IsDefined(command.TargetLocation) ||
            !IsValidTargetSlot(
                command.TargetLocation,
                command.TargetSlot) ||
            !TryGetStateBytes(
                command.ExpectedTargetCompactItemState,
                allowEmpty: true,
                out var targetStateBytes) ||
            !TryGetStateBytes(
                command.ExpectedStoneCompactItemState,
                allowEmpty: true,
                out var stoneStateBytes) ||
            !TryGetStateBytes(
                command.ExpectedCatalystCompactItemState,
                allowEmpty: true,
                out var catalystStateBytes) ||
            !TryGetStateBytes(
                command.ExpectedThirdMaterialCompactItemState,
                allowEmpty: true,
                out var thirdMaterialStateBytes) ||
            targetStateBytes.Length + stoneStateBytes.Length +
                catalystStateBytes.Length + thirdMaterialStateBytes.Length >
                MaximumCombinedStateBytes(command.Operation))
        {
            return false;
        }

        return command.Operation switch
        {
            HolyStoneCommandOperation.Mount =>
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                HasNoCatalyst(command) &&
                HasNoThirdMaterial(command) &&
                (command.TargetLocation !=
                    HolyStoneTargetLocation.KitBag ||
                 command.TargetSlot != command.StoneKitBagSlot),
            HolyStoneCommandOperation.Remove =>
                command.SocketIndex is
                    >= MinimumSocketIndex and <= MaximumSocketIndex &&
                command.StoneKitBagSlot == NoStoneKitBagSlot &&
                command.ExpectedStoneCompactItemState == "[]" &&
                HasNoCatalyst(command) &&
                HasNoThirdMaterial(command),
            HolyStoneCommandOperation.Drill =>
                command.SocketIndex == ServerSelectedSocketIndex &&
                command.StoneKitBagSlot == NoStoneKitBagSlot &&
                command.ExpectedStoneCompactItemState == "[]" &&
                HasNoCatalyst(command) &&
                HasNoThirdMaterial(command),
            HolyStoneCommandOperation.AdvancedDrill =>
                command.TargetLocation == HolyStoneTargetLocation.KitBag &&
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                command.TargetSlot != command.StoneKitBagSlot &&
                HasNoCatalyst(command) &&
                HasNoThirdMaterial(command),
            HolyStoneCommandOperation.Upgrade =>
                command.TargetLocation == HolyStoneTargetLocation.KitBag &&
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                command.TargetSlot != command.StoneKitBagSlot &&
                IsValidOptionalCatalyst(command) &&
                HasNoThirdMaterial(command),
            HolyStoneCommandOperation.Combine =>
                command.TargetLocation == HolyStoneTargetLocation.KitBag &&
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                IsKitBagSlot(command.CatalystKitBagSlot) &&
                IsKitBagSlot(command.ThirdMaterialKitBagSlot) &&
                command.ExpectedTargetCompactItemState != "[]" &&
                command.ExpectedStoneCompactItemState != "[]" &&
                command.ExpectedCatalystCompactItemState != "[]" &&
                command.ExpectedThirdMaterialCompactItemState != "[]" &&
                AreDistinctCombinationSlots(command),
            HolyStoneCommandOperation.ImplementSpirit =>
                command.TargetLocation == HolyStoneTargetLocation.KitBag &&
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                command.TargetSlot != command.StoneKitBagSlot &&
                IsValidOptionalCatalyst(command) &&
                HasNoThirdMaterial(command),
            _ => false
        };
    }

    private static bool IsValidTargetSlot(
        HolyStoneTargetLocation location,
        int slot) =>
        location switch
        {
            HolyStoneTargetLocation.Equipment =>
                slot == WeaponEquipmentSlot,
            HolyStoneTargetLocation.KitBag => IsKitBagSlot(slot),
            _ => false
        };

    private static bool IsKitBagSlot(int slot) =>
        slot is >= MinimumKitBagSlot and <= MaximumKitBagSlot;

    private static bool HasNoCatalyst(HolyStoneCommand command) =>
        command.CatalystKitBagSlot == NoStoneKitBagSlot &&
        command.ExpectedCatalystCompactItemState == "[]";

    private static bool HasNoThirdMaterial(HolyStoneCommand command) =>
        command.ThirdMaterialKitBagSlot == NoStoneKitBagSlot &&
        command.ExpectedThirdMaterialCompactItemState == "[]";

    private static bool IsValidOptionalCatalyst(
        HolyStoneCommand command) =>
        HasNoCatalyst(command) ||
        IsKitBagSlot(command.CatalystKitBagSlot) &&
        command.CatalystKitBagSlot != command.TargetSlot &&
        command.CatalystKitBagSlot != command.StoneKitBagSlot &&
        command.ExpectedCatalystCompactItemState != "[]";

    private static bool AreDistinctCombinationSlots(
        HolyStoneCommand command) =>
        command.TargetSlot != command.StoneKitBagSlot &&
        command.TargetSlot != command.CatalystKitBagSlot &&
        command.TargetSlot != command.ThirdMaterialKitBagSlot &&
        command.StoneKitBagSlot != command.CatalystKitBagSlot &&
        command.StoneKitBagSlot != command.ThirdMaterialKitBagSlot &&
        command.CatalystKitBagSlot != command.ThirdMaterialKitBagSlot;

    private static bool SupportsRawLocalIdentity(
        HolyStoneCommandOperation operation) =>
        operation is
            HolyStoneCommandOperation.Mount or
            HolyStoneCommandOperation.Remove or
            HolyStoneCommandOperation.Upgrade or
            HolyStoneCommandOperation.Combine or
            HolyStoneCommandOperation.ImplementSpirit;

    private static int MaximumCombinedStateBytes(
        HolyStoneCommandOperation operation) =>
        operation switch
        {
            HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.ImplementSpirit =>
                MaximumUpgradeCombinedStateUtf8Bytes,
            HolyStoneCommandOperation.Combine =>
                MaximumCombinationCombinedStateUtf8Bytes,
            _ => MaximumCombinedStateUtf8Bytes
        };

    private static byte[] CreateCanonicalRequest(
        HolyStoneCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The Holy Stone command is invalid.",
                nameof(command));
        }

        var targetState = StrictUtf8.GetBytes(
            command.ExpectedTargetCompactItemState);
        var stoneState = StrictUtf8.GetBytes(
            command.ExpectedStoneCompactItemState);
        var catalystState = StrictUtf8.GetBytes(
            command.ExpectedCatalystCompactItemState);
        var thirdMaterialState = StrictUtf8.GetBytes(
            command.ExpectedThirdMaterialCompactItemState);
        var hasCatalyst =
            command.Operation is
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.ImplementSpirit;
        var isCombination =
            command.Operation == HolyStoneCommandOperation.Combine;
        var canonical = new byte[isCombination
            ? CombinationCanonicalRequestBytes
            : hasCatalyst
                ? UpgradeCanonicalRequestBytes
                : CanonicalRequestBytes];
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            isCombination
                ? CombinationCanonicalRequestVersion
                : hasCatalyst
                ? UpgradeCanonicalRequestVersion
                : CanonicalRequestVersion);
        var offset = sizeof(ushort);
        destination[offset++] = (byte)command.Operation;
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            CanonicalArtisanEndpoint);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            DialogIndex);
        offset += sizeof(int);
        destination[offset++] = (byte)command.TargetLocation;
        BinaryPrimitives.WriteInt16BigEndian(
            destination[offset..],
            checked((short)command.TargetSlot));
        offset += sizeof(short);
        BinaryPrimitives.WriteInt16BigEndian(
            destination[offset..],
            checked((short)command.SocketIndex));
        offset += sizeof(short);
        BinaryPrimitives.WriteInt16BigEndian(
            destination[offset..],
            checked((short)command.StoneKitBagSlot));
        offset += sizeof(short);
        if (hasCatalyst || isCombination)
        {
            BinaryPrimitives.WriteInt16BigEndian(
                destination[offset..],
                checked((short)command.CatalystKitBagSlot));
            offset += sizeof(short);
        }
        if (isCombination)
        {
            BinaryPrimitives.WriteInt16BigEndian(
                destination[offset..],
                checked((short)command.ThirdMaterialKitBagSlot));
            offset += sizeof(short);
        }
        (isCombination
            ? ComputeCombinationStateDigest(
                targetState,
                stoneState,
                catalystState,
                thirdMaterialState)
            : hasCatalyst
            ? ComputeUpgradeStateDigest(
                targetState,
                stoneState,
                catalystState)
            : ComputeStateDigest(targetState, stoneState))
            .CopyTo(destination[offset..]);
        return canonical;
    }

    private static bool TryGetStateBytes(
        string? value,
        bool allowEmpty,
        out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']' ||
            (!allowEmpty && value == "[]"))
        {
            return false;
        }

        try
        {
            bytes = StrictUtf8.GetBytes(value);
            return bytes.Length <= MaximumCompactItemStateUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

}
