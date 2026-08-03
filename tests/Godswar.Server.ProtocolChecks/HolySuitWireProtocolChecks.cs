using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySuitWireProtocolChecks
{
    public static Task RunAsync()
    {
        CheckIdentityAndNavigation();
        CheckMutationShapes();
        CheckMalformedPackets();
        CheckStoreAmountSentinel();
        CheckTransformAmountSentinel();
        CheckResponsesAndCounters();
        CheckHolyBoxStoredExperienceSerialization();
        CheckClassSuitAttributeExtensionSerialization();
        return Task.CompletedTask;
    }

    private static void CheckIdentityAndNavigation()
    {
        Check.Equal(
            30,
            (int)HolySuitWireOperation.StoreExperience,
            "Store wire operation allocation");
        Check.Equal(
            33,
            (int)HolySuitWireOperation.TransformExperience,
            "Transform wire operation allocation");
        foreach (var (subId, expected) in new[]
                 {
                     (101, HolySuitWireOperation.StoreExperience),
                     (201, HolySuitWireOperation.TransferExperience),
                     (301, HolySuitWireOperation.ConsumeWare),
                     (401, HolySuitWireOperation.TransformExperience)
                 })
        {
            Check.True(
                HolySuitDesignProtocol.TryResolveOperation(
                    subId,
                    out var operation) &&
                operation == expected,
                $"menu sub-ID {subId} resolves exactly");
            var navigation = CreatePacket(
                HolySuitDesignProtocol.SpartaNpcId,
                subId,
                static _ => { });
            Check.True(
                HolySuitDesignProtocol.IsExactNavigation(
                    navigation,
                    subId),
                $"all-minus-one {subId} packet is navigation");
            Check.True(
                !HolySuitDesignProtocol.TryReadMutation(
                    navigation,
                    out _,
                    out _,
                    out _),
                $"navigation {subId} cannot become a mutation");
        }
    }

    private static void CheckMutationShapes()
    {
        var store = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.StoreExperienceSubId,
            args =>
            {
                args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 12;
                args[HolySuitDesignProtocol.AmountArgumentIndex] = 100_000_000;
            });
        Check.True(
            HolySuitDesignProtocol.TryReadMutation(
                store,
                out var storeNpc,
                out var storeDialog,
                out var storeIntent),
            "exact Store EXP packet parses");
        Check.Equal(
            HolySuitDesignProtocol.SpartaNpcId,
            storeNpc,
            "Store endpoint");
        Check.Equal(
            HolySuitDesignProtocol.DialogIndex,
            storeDialog,
            "Store dialogue");
        Check.Equal(12, storeIntent.HolyBoxKitBagSlot, "Store Holy Box slot");
        Check.Equal(100_000_000L, storeIntent.Amount, "Store EXP amount");
        Check.Equal(
            HolySuitDesignProtocol.NoKitBagSlot,
            storeIntent.EquipmentKitBagSlot,
            "Store has no equipment slot");

        foreach (var (wireValue, expected) in new[]
                 {
                     (0x80000000u, 2_147_483_648L),
                     (0xFFFFFFFEu, 4_294_967_294L)
                 })
        {
            var unsignedStore = CreatePacket(
                HolySuitDesignProtocol.SpartaNpcId,
                HolySuitDesignProtocol.StoreExperienceSubId,
                args =>
                {
                    args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 0;
                    args[HolySuitDesignProtocol.AmountArgumentIndex] =
                        unchecked((int)wireValue);
                });
            Check.True(
                HolySuitDesignProtocol.TryReadMutation(
                    unsignedStore,
                    out _,
                    out _,
                    out var unsignedIntent),
                $"UInt32 Store EXP packet 0x{wireValue:X8} parses");
            Check.Equal(
                expected,
                unsignedIntent.Amount,
                $"UInt32 Store EXP packet 0x{wireValue:X8} stays positive");
        }

        var transfer = CreatePacket(
            HolySuitDesignProtocol.AthensNpcId,
            HolySuitDesignProtocol.TransferExperienceSubId,
            args =>
            {
                args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 0;
                args[HolySuitDesignProtocol.SecondItemArgumentIndex] = 100;
            });
        Check.True(
            HolySuitDesignProtocol.TryReadMutation(
                transfer,
                out _,
                out _,
                out var transferIntent),
            "exact Transfer EXP packet parses");
        Check.Equal(0, transferIntent.EquipmentKitBagSlot, "Transfer gear slot");
        Check.Equal(24, transferIntent.HolyBoxKitBagSlot, "Transfer box slot");

        var consume = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.ConsumeWareSubId,
            args =>
            {
                args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 23;
                args[HolySuitDesignProtocol.SecondItemArgumentIndex] = 323;
            });
        Check.True(
            HolySuitDesignProtocol.TryReadMutation(
                consume,
                out _,
                out _,
                out var consumeIntent),
            "exact Ware Consuming packet parses");
        Check.Equal(23, consumeIntent.EquipmentKitBagSlot, "Consume gear slot");
        Check.Equal(95, consumeIntent.WareKitBagSlot, "Consume ware slot");

        var transform = CreatePacket(
            HolySuitDesignProtocol.AthensNpcId,
            HolySuitDesignProtocol.TransformExperienceSubId,
            args => args[HolySuitDesignProtocol.AmountArgumentIndex] = 7);
        Check.True(
            HolySuitDesignProtocol.TryReadMutation(
                transform,
                out _,
                out _,
                out var transformIntent),
            "exact Transform EXP packet parses");
        Check.Equal(7L, transformIntent.Amount, "requested prism count");

        foreach (var (reference, expectedSlot) in new[]
                 {
                     (0, 0),
                     (23, 23),
                     (100, 24),
                     (123, 47),
                     (200, 48),
                     (223, 71),
                     (300, 72),
                     (323, 95)
                 })
        {
            Check.True(
                HolySuitDesignProtocol.TryDecodeKitBagReference(
                    reference,
                    out var decoded) &&
                decoded == expectedSlot,
                $"stock bag reference {reference} decodes to {expectedSlot}");
            Check.True(
                HolySuitDesignProtocol.TryEncodeKitBagReference(
                    expectedSlot,
                    out var encodedReference) &&
                encodedReference == reference,
                $"kit-bag slot {expectedSlot} encodes to {reference}");
        }
        Check.True(
            HolySuitDesignProtocol.TryEncodeKitBagReference(95, out var encoded) &&
            encoded == 323,
            "last kit-bag slot encodes safely");

        var stockScratch = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.StoreExperienceSubId,
            args =>
            {
                args[0] = 0;
                args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 12;
                args[HolySuitDesignProtocol.AmountArgumentIndex] = 100_000_000;
            });
        Check.True(
            HolySuitDesignProtocol.TryReadMutation(
                stockScratch,
                out _,
                out _,
                out var stockScratchIntent) &&
            stockScratchIntent.HolyBoxKitBagSlot == 12 &&
            stockScratchIntent.Amount == 100_000_000,
            "exact stock unchecked-button scratch value is tolerated");
    }

    private static void CheckMalformedPackets()
    {
        var valid = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.TransferExperienceSubId,
            args =>
            {
                args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 100;
                args[HolySuitDesignProtocol.SecondItemArgumentIndex] = 101;
            });

        var wrongLength = valid.Buffer.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongLength,
            HolySuitDesignProtocol.PacketBytes - 1);
        Reject(new GamePacket(wrongLength), "declared length mismatch");

        var truncated = valid.Buffer[..^4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            truncated,
            checked((ushort)truncated.Length));
        Reject(new GamePacket(truncated), "truncated packet");

        var duplicateDialog = valid.Buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            duplicateDialog.AsSpan(12, sizeof(int)),
            HolySuitDesignProtocol.DialogIndex + 1);
        Reject(new GamePacket(duplicateDialog), "mismatched duplicate dialogue");

        var unknownNpc = valid.Buffer.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            unknownNpc.AsSpan(4, sizeof(uint)),
            9999);
        Reject(new GamePacket(unknownNpc), "unknown NPC endpoint");

        var unexpectedArgument = valid.Buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            unexpectedArgument.AsSpan(20, sizeof(int)),
            1);
        Reject(new GamePacket(unexpectedArgument), "unexpected scratch argument");
        Check.True(
            !HolySuitDesignProtocol.TryReadMutation(
                new GamePacket(unexpectedArgument),
                out _,
                out _,
                out _,
                out var unexpectedRejection) &&
            unexpectedRejection.Reason ==
                HolySuitWireRejectionReason.UnexpectedArgument &&
            unexpectedRejection.ArgumentIndex == 0,
            "nonzero scratch argument reports bounded arg_0 reason");

        var wrongScratchSlot = valid.Buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            wrongScratchSlot.AsSpan(24, sizeof(int)),
            0);
        Reject(
            new GamePacket(wrongScratchSlot),
            "zero scratch value outside arg_0");

        var badReference = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.StoreExperienceSubId,
            args =>
            {
                args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 196;
                args[HolySuitDesignProtocol.AmountArgumentIndex] = 1;
            });
        Reject(badReference, "bag reference in page-one gap");
        Check.True(
            !HolySuitDesignProtocol.TryReadMutation(
                badReference,
                out _,
                out _,
                out _,
                out var referenceRejection) &&
            referenceRejection.Reason ==
                HolySuitWireRejectionReason.InvalidItemReference &&
            referenceRejection.ArgumentIndex ==
                HolySuitDesignProtocol.FirstItemArgumentIndex,
            "invalid Store item reports bounded arg_6 reason");

        var sameSlot = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.ConsumeWareSubId,
            args =>
            {
                args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 100;
                args[HolySuitDesignProtocol.SecondItemArgumentIndex] = 100;
            });
        Reject(sameSlot, "one slot cannot be gear and ware");

        var zeroAmount = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.TransformExperienceSubId,
            args => args[HolySuitDesignProtocol.AmountArgumentIndex] = 0);
        Reject(zeroAmount, "zero amount");
        Check.True(
            !HolySuitDesignProtocol.TryReadMutation(
                zeroAmount,
                out _,
                out _,
                out _,
                out var amountRejection) &&
            amountRejection.Reason ==
                HolySuitWireRejectionReason.MissingAmount &&
            amountRejection.ArgumentIndex ==
                HolySuitDesignProtocol.AmountArgumentIndex,
            "zero amount reports bounded arg_10 reason");

        foreach (var invalidReference in new[]
                 {
                     -1,
                     24,
                     99,
                     124,
                     199,
                     224,
                     299,
                     324
                 })
        {
            Check.True(
                !HolySuitDesignProtocol.TryDecodeKitBagReference(
                    invalidReference,
                    out _),
                $"bag reference {invalidReference} fails closed");
        }
        Check.True(
            !HolySuitDesignProtocol.TryEncodeKitBagReference(-1, out _) &&
            !HolySuitDesignProtocol.TryEncodeKitBagReference(96, out _),
            "bag slot bounds fail closed");
    }

    private static void CheckResponsesAndCounters()
    {
        var menu = HolySuitDesignProtocol.BuildInitialMenuResponse(
            HolySuitDesignProtocol.SpartaNpcId);
        CheckResponse(
            menu,
            HolySuitDesignProtocol.SpartaNpcId,
            101,
            201,
            301,
            401);

        var store = HolySuitDesignProtocol.BuildStorePageResponse(
            HolySuitDesignProtocol.AthensNpcId,
            transferredToday: 12_345,
            transferCredit: 67_890);
        CheckResponse(
            store,
            HolySuitDesignProtocol.AthensNpcId,
            HolySuitDesignProtocol.StoreExperiencePageSubId,
            123_454,
            678_905);

        var consume = HolySuitDesignProtocol.BuildOperationPageResponse(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitWireOperation.ConsumeWare);
        CheckResponse(
            consume,
            HolySuitDesignProtocol.SpartaNpcId,
            306,
            406,
            506,
            606);

        var success = HolySuitDesignProtocol.BuildResultResponse(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.TransformSucceededResultSubId);
        CheckResponse(
            success,
            HolySuitDesignProtocol.SpartaNpcId,
            2100);
        Check.True(
            HolySuitDesignProtocol.IsResultSubId(888) &&
            HolySuitDesignProtocol.IsResultSubId(2101) &&
            HolySuitDesignProtocol.IsResultSubId(10001) &&
            !HolySuitDesignProtocol.IsResultSubId(706),
            "only localized result sub-IDs are accepted");

        CheckDisplayCounterLimits();
        Check.Throws<ArgumentOutOfRangeException>(
            () => HolySuitDesignProtocol.BuildResultResponse(
                HolySuitDesignProtocol.SpartaNpcId,
                706),
            "page IDs cannot be emitted as results");
        Check.Throws<ArgumentOutOfRangeException>(
            () => HolySuitDesignProtocol.BuildInitialMenuResponse(9999),
            "responses cannot target an unrelated NPC");
    }

    private static void Reject(GamePacket packet, string description)
    {
        Check.True(
            !HolySuitDesignProtocol.TryReadMutation(
                packet,
                out _,
                out _,
                out _),
            description);
    }

    private static GamePacket CreatePacket(
        uint npcId,
        int subId,
        Action<int[]> configure)
    {
        var packet = new byte[HolySuitDesignProtocol.PacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)),
            npcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8, sizeof(int)),
            HolySuitDesignProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12, sizeof(int)),
            HolySuitDesignProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16, sizeof(int)),
            subId);
        var arguments = Enumerable.Repeat(
            -1,
            HolySuitDesignProtocol.FunctionArgumentCount).ToArray();
        configure(arguments);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(
                    20 + (index * sizeof(int)),
                    sizeof(int)),
                arguments[index]);
        }
        return new GamePacket(packet);
    }

    private static void CheckResponse(
        byte[] packet,
        uint npcId,
        params int[] expectedSubIds)
    {
        Check.Equal(
            checked((ushort)packet.Length),
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "response packet length");
        Check.Equal(
            Opcodes.NpcFunctionActionResponse,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "response opcode");
        Check.Equal(
            npcId,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
            "response NPC");
        Check.Equal(
            HolySuitDesignProtocol.DialogIndex,
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)),
            "response dialogue");
        Check.Equal(
            12 + (expectedSubIds.Length * sizeof(int)),
            packet.Length,
            "response sub-ID count");
        for (var index = 0; index < expectedSubIds.Length; index++)
        {
            Check.Equal(
                expectedSubIds[index],
                BinaryPrimitives.ReadInt32LittleEndian(
                    packet.AsSpan(12 + (index * sizeof(int)))),
                $"response sub-ID {index}");
        }
    }
}
