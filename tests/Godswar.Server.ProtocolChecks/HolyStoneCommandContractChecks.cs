using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneCommandContractChecks
{
    private static readonly Guid OperationId =
        Guid.Parse("4e67438c-54dd-496f-b741-085bd5765555");

    public static Task RunAsync()
    {
        CheckFamiliesAndIdentity();
        CheckMissingStateCommands();
        CheckCanonicalIdentity();
        CheckRawLocalUpgradeIdentity();
        CheckCombinationContract();
        CheckExactWireShapes();
        CheckAdvancedDrillContract();
        CheckWireShapeRejections();
        CheckReceiptEvidence();
        return Task.CompletedTask;
    }

    private static void CheckFamiliesAndIdentity()
    {
        Check.Equal(
            16,
            (int)HolyStoneCommandEnvelope.Family(
                HolyStoneCommandOperation.Mount),
            "Holy Stone Mount has a distinct command family");
        Check.Equal(
            17,
            (int)HolyStoneCommandEnvelope.Family(
                HolyStoneCommandOperation.Remove),
            "Holy Stone Remove has a distinct command family");
        Check.Equal(
            18,
            (int)HolyStoneCommandEnvelope.Family(
                HolyStoneCommandOperation.Drill),
            "Holy Stone Drill has a distinct command family");
        foreach (var family in new[]
                 {
                     CommandFamily.HolyStoneMount,
                     CommandFamily.HolyStoneRemove,
                     CommandFamily.HolyStoneDrill
                 })
        {
            Check.Equal(
                (int)CommandIdentityStrength.ClientOperationId,
                (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                    family),
                $"{family} requires a client operation UUID");
        }
    }

    private static void CheckMissingStateCommands()
    {
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.Mount,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                targetSlot: 16,
                expectedTargetCompactItemState: "[]",
                socketIndex:
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                stoneKitBagSlot: 0,
                expectedStoneCompactItemState: "[]",
                out _),
            "missing target and stone snapshots reach durable execution");
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.Remove,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                targetSlot: 95,
                expectedTargetCompactItemState: "[]",
                socketIndex: 3,
                HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                expectedStoneCompactItemState: "[]",
                out _),
            "missing Remove target snapshot reaches durable execution");
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.Drill,
                HolyStoneCommandEnvelope.AthensNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                targetSlot: 31,
                expectedTargetCompactItemState: "[]",
                socketIndex:
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                stoneKitBagSlot:
                    HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                expectedStoneCompactItemState: "[]",
                out _),
            "missing Drill target snapshot reaches durable execution");
        Check.True(
            !HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.Mount,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                targetSlot: 0,
                expectedTargetCompactItemState: "[]",
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                stoneKitBagSlot: 0,
                expectedStoneCompactItemState: "[]",
                out _),
            "one bag slot cannot be both target and material");
    }

    private static void CheckCanonicalIdentity()
    {
        var subject = new CommandSubject(7, 19);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.Mount,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                targetSlot: 16,
                expectedTargetCompactItemState:
                    "[1100,,,,,,3,5,1,1,0,0,,,,,,1,,,,,,,,,,,,]",
                socketIndex:
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                stoneKitBagSlot: 0,
                expectedStoneCompactItemState:
                    "[9030,,,,,,1,1,1,1,0,0,,,,,,0,,,,,,,,,,,,]",
                out var command),
            "bounded Mount command is accepted");
        var envelope = HolyStoneCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            command);
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)HolyStoneCommandEnvelope.Validate(envelope),
            "canonical Mount envelope validates");
        var changedStone = HolyStoneCommandEnvelope.Create(
            subject,
            connection,
            envelope.ReceivedAt,
            command with { ExpectedStoneCompactItemState = "[]" });
        Check.True(
            envelope.OperationId == changedStone.OperationId &&
            envelope.RequestHash != changedStone.RequestHash,
            "material state is request-hash bound");
        var athensEnvelope = HolyStoneCommandEnvelope.Create(
            subject,
            connection,
            envelope.ReceivedAt,
            command with
            {
                NpcId = HolyStoneCommandEnvelope.AthensNpcId
            });
        Check.True(
            envelope.OperationId == athensEnvelope.OperationId &&
            envelope.RequestHash == athensEnvelope.RequestHash,
            "equivalent city artisans share canonical retry identity");
        Check.True(
            HolyStoneCommandEnvelope.CreateOperationId(
                subject,
                HolyStoneCommandOperation.Mount,
                OperationId) !=
            HolyStoneCommandEnvelope.CreateOperationId(
                subject,
                HolyStoneCommandOperation.Remove,
                OperationId),
            "one UUID cannot alias different Holy Stone families");
        Check.Throws<ArgumentException>(
            () => HolyStoneCommandEnvelope.Create(
                subject,
                connection with
                {
                    Transport = CommandTransportKind.LegacyTcp
                },
                envelope.ReceivedAt,
                command),
            "raw legacy provenance cannot claim a durable UUID");
    }

    private static void CheckExactWireShapes()
    {
        var mount = CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.MountSubId,
            args =>
            {
                args[HolyStoneProtocol.MountScratchArgumentIndex] = 0;
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(53);
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(95);
            });
        Check.True(
            HolyStoneProtocol.TryReadMutation(
                mount,
                out var mountNpc,
                out var mountDialog,
                out var mountIntent),
            "exact 92-byte Mount packet parses");
        Check.Equal(
            HolyStoneProtocol.SpartaNpcId,
            mountNpc,
            "Mount endpoint");
        Check.Equal(
            HolyStoneProtocol.DialogIndex,
            mountDialog,
            "Mount dialogue");
        Check.Equal(
            (int)HolyStoneCommandOperation.Mount,
            (int)mountIntent.Operation,
            "Mount operation");
        Check.Equal(
            (int)HolyStoneTargetLocation.KitBag,
            (int)mountIntent.TargetLocation,
            "Mount uses a kitbag target");
        Check.Equal(
            53,
            mountIntent.TargetSlot,
            "page-two Mount target");
        Check.Equal(95, mountIntent.StoneKitBagSlot, "Mount material slot");

        var remove = CreatePacket(
            HolyStoneProtocol.AthensNpcId,
            HolyStoneProtocol.RemoveSubId,
            args =>
            {
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(40);
                args[HolyStoneProtocol.RemoveOrdinalArgumentIndex] = 4;
            });
        Check.True(
            HolyStoneProtocol.TryReadMutation(
                remove,
                out _,
                out _,
                out var removeIntent),
            "exact 92-byte Remove packet parses");
        Check.Equal(40, removeIntent.TargetSlot, "Remove bag target");
        Check.Equal(3, removeIntent.SocketIndex, "one-based socket ordinal");

        CheckCapturedPageAwareBagReferences();
    }

    private static void CheckWireShapeRejections()
    {
        var valid = CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.MountSubId,
            args =>
            {
                args[0] = 0;
                args[6] =
                    HolyStoneProtocol.EncodeKitBagReference(16);
                args[7] =
                    HolyStoneProtocol.EncodeKitBagReference(0);
            });
        var wrongDeclaredLength = valid.Buffer.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongDeclaredLength,
            HolyStoneProtocol.PacketBytes - 1);
        Check.True(
            !HolyStoneProtocol.TryReadMutation(
                new GamePacket(
                    wrongDeclaredLength,
                    OperationId),
                out _,
                out _,
                out _),
            "declared length must be exactly 92 bytes");

        var truncated = valid.Buffer[..^4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            truncated,
            checked((ushort)truncated.Length));
        Check.True(
            !HolyStoneProtocol.TryReadMutation(
                new GamePacket(truncated, OperationId),
                out _,
                out _,
                out _),
            "truncated 88-byte lookalike is rejected");

        var mismatchedDialog = valid.Buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            mismatchedDialog.AsSpan(12, sizeof(int)),
            HolyStoneProtocol.DialogIndex + 1);
        Check.True(
            !HolyStoneProtocol.TryReadMutation(
                new GamePacket(mismatchedDialog, OperationId),
                out _,
                out _,
                out _),
            "duplicated dialogue field must agree");

        var unusedArgument = valid.Buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            unusedArgument.AsSpan(20 + (5 * sizeof(int)), sizeof(int)),
            0);
        Check.True(
            !HolyStoneProtocol.TryReadMutation(
                new GamePacket(unusedArgument, OperationId),
                out _,
                out _,
                out _),
            "unexpected argument data is rejected");

        foreach (var invalidReference in new[]
                 {
                     -1,
                     24,
                     99,
                     124,
                     195,
                     199,
                     224,
                     299,
                     324,
                     399,
                     400
                 })
        {
            var invalidDrill = CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.DrillSubId,
                args => args[HolyStoneProtocol.TargetArgumentIndex] =
                    invalidReference);
            Check.True(
                !HolyStoneProtocol.TryReadMutation(
                    invalidDrill,
                    out _,
                    out _,
                    out _),
                $"invalid bag reference {invalidReference} is rejected");
        }
        Check.Throws<ArgumentOutOfRangeException>(
            () => HolyStoneProtocol.EncodeKitBagReference(-1),
            "negative canonical bag slot cannot encode");
        Check.Throws<ArgumentOutOfRangeException>(
            () => HolyStoneProtocol.EncodeKitBagReference(96),
            "out-of-range canonical bag slot cannot encode");

        var navigationArgs =
            Enumerable.Repeat(
                -1,
                HolyStoneProtocol.FunctionArgumentCount).ToArray();
        Check.True(
            HolyStoneProtocol.IsMountNavigation(
                HolyStoneProtocol.MountSubId,
                navigationArgs),
            "all-minus-one Mount packet remains navigation");
        navigationArgs[0] = 0;
        Check.True(
            !HolyStoneProtocol.IsMountNavigation(
                HolyStoneProtocol.MountSubId,
                navigationArgs),
            "Mount mutation cannot masquerade as navigation");
        foreach (var alias in new[] { 106, 206, 306, 406 })
        {
            Check.True(
                HolyStoneProtocol.TryResolveBoundaryOperation(
                    alias,
                    out var operation) &&
                operation == HolyStoneCommandOperation.Mount,
                $"secure alias {alias} resolves to the Mount boundary");
            Check.True(
                !HolyStoneProtocol.TryResolveOperation(alias, out _),
                $"secure alias {alias} is never an exact mutation shape");
        }
    }

    private static void CheckReceiptEvidence()
    {
        // Version-2 receipts written before the page-aware wire correction
        // can name the equipped weapon. Keep decoding this historical form
        // even though new stock-client Holy Stone commands are bag-targeted.
        var staleMissing = new HolyStoneExecutionReceipt(
            characterId: 19,
            HolyStoneCommandOperation.Mount,
            HolyStoneCommandEnvelope.SpartaNpcId,
            HolyStoneCommandEnvelope.DialogIndex,
            HolyStoneCommandResultStatus.StaleTarget,
            HolyStoneNativeResults.WrongSelectionSubId,
            HolyStoneTargetLocation.Equipment,
            HolyStoneCommandEnvelope.WeaponEquipmentSlot,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            targetItemInstanceId: 51,
            expectedTargetCompactItemState: "[]",
            authoritativeTargetBeforeCompactItemState:
                "[1100,,,,,,1,1,1,1,0,0,,,,,,1,,,,,,,,,,,,]",
            authoritativeTargetAfterCompactItemState:
                "[1100,,,,,,1,1,1,1,0,0,,,,,,1,,,,,,,,,,,,]",
            stoneKitBagSlot: 0,
            stoneItemInstanceId: null,
            expectedStoneCompactItemState: "[]",
            authoritativeStoneBeforeCompactItemState: "[]",
            authoritativeStoneAfterCompactItemState: "[]",
            outputKitBagSlot: -1,
            outputItemInstanceId: null,
            outputBeforeCompactItemState: null,
            outputAfterCompactItemState: null,
            goldSpent: 0,
            goldBefore: 10,
            goldAfter: 10,
            walletRevision: 0,
            inventoryRevision: 0,
            auditReference: "audit:holy-stone:stale-target",
            outboxEventId: null);
        Check.True(
            HolyStoneExecutionResult
                .TerminalRejected(staleMissing).IsDurable,
            "legacy equipment receipts remain replay-compatible");
    }

    internal static GamePacket CreatePacket(
        uint npcId,
        int subId,
        Action<int[]> configure,
        Guid? operationId = null)
    {
        var packet = new byte[HolyStoneProtocol.PacketBytes];
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
            HolyStoneProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12, sizeof(int)),
            HolyStoneProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16, sizeof(int)),
            subId);
        var args = Enumerable.Repeat(
            -1,
            HolyStoneProtocol.FunctionArgumentCount).ToArray();
        configure(args);
        for (var index = 0; index < args.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(
                    20 + (index * sizeof(int)),
                    sizeof(int)),
                args[index]);
        }
        return new GamePacket(packet, operationId);
    }
}
