using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class ClassSuitWireProtocolChecks
{
    public const string CheckName =
        "Class Suit bounded stock-client wire protocol";

    private static readonly uint[] Endpoints =
    [
        ClassSuitProtocol.SpartaNpcId,
        ClassSuitProtocol.AthensNpcId
    ];

    private static readonly (
        ClassSuitWireOperation Operation,
        int[] PageSubIds)[] OperationPages =
    [
        (ClassSuitWireOperation.ExchangeTierOne, [110, 111, 119]),
        (ClassSuitWireOperation.AddClassAttribute, [112, 113, 114]),
        (ClassSuitWireOperation.DeleteClassAttribute, [115, 116, 117]),
        (ClassSuitWireOperation.Instructions, [118]),
        (ClassSuitWireOperation.ConvertToCommon, [120]),
        (ClassSuitWireOperation.UpgradeTierTwo, [201, 202, 203]),
        (ClassSuitWireOperation.UpgradeTierThree, [130, 131, 132]),
        (ClassSuitWireOperation.AddFifthAttribute, [123]),
        (ClassSuitWireOperation.UpgradeTierFour, [140, 141, 142])
    ];

    public static Task RunAsync()
    {
        CheckEndpointsAndResponses();
        CheckNavigationShapes();
        CheckConversionMutations();
        CheckMalformedMutations();
        return Task.CompletedTask;
    }

    private static void CheckEndpointsAndResponses()
    {
        Check.Equal(92, ClassSuitProtocol.PacketBytes, "request bytes");
        Check.Equal(37, ClassSuitProtocol.DialogIndex, "dialog index");
        Check.True(
            ClassSuitProtocol.IsNpcKey("Sparta_070") &&
            ClassSuitProtocol.IsNpcKey("Athens_070") &&
            !ClassSuitProtocol.IsNpcKey("Sparta_071"),
            "only the two captured Gear Mentors own Class Suit");

        foreach (var endpoint in Endpoints)
        {
            Check.True(
                ClassSuitProtocol.IsEndpoint(
                    endpoint,
                    ClassSuitProtocol.DialogIndex),
                $"NPC {endpoint} is a dialog-37 endpoint");
            CheckResponse(
                ClassSuitProtocol.BuildInitialMenuResponse(endpoint),
                endpoint,
                ClassSuitProtocol.InitialMenuSubIds);

            foreach (var (operation, pageSubIds) in OperationPages)
            {
                CheckResponse(
                    ClassSuitProtocol.BuildOperationPageResponse(
                        endpoint,
                        operation),
                    endpoint,
                    pageSubIds);
            }

            CheckResponse(
                ClassSuitProtocol.BuildResultResponse(endpoint, 157),
                endpoint,
                157);
        }

        Check.True(
            !ClassSuitProtocol.IsEndpoint(
                ClassSuitProtocol.SpartaNpcId,
                ClassSuitProtocol.DialogIndex + 1) &&
            !ClassSuitProtocol.IsEndpoint(9999, ClassSuitProtocol.DialogIndex),
            "wrong NPC or dialogue fails closed");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ClassSuitProtocol.BuildInitialMenuResponse(9999),
            "responses cannot target an unrelated NPC");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ClassSuitProtocol.BuildResultResponse(
                ClassSuitProtocol.SpartaNpcId,
                0),
            "result sub-ID must be positive");
    }

    private static void CheckNavigationShapes()
    {
        foreach (var (operation, _) in OperationPages)
        {
            var subId = (int)operation;
            Check.True(
                ClassSuitProtocol.TryResolveOperation(
                    subId,
                    out var resolved) &&
                resolved == operation,
                $"operation {subId} resolves exactly");

            foreach (var endpoint in Endpoints)
            {
                var navigation = CreateAction(endpoint, subId);
                Check.True(
                    ClassSuitProtocol.IsExactNavigation(
                        navigation,
                        subId),
                    $"{endpoint}/{subId} all-minus-one navigation");

                var stockScratch = CreateAction(
                    endpoint,
                    subId,
                    static args => args[0] = 0);
                Check.True(
                    ClassSuitProtocol.IsExactNavigation(
                        stockScratch,
                        subId),
                    $"{endpoint}/{subId} stock arg-0 navigation");
            }
        }

        Check.True(
            !ClassSuitProtocol.TryResolveOperation(109, out _),
            "unknown operation does not resolve");
        Check.True(
            !ClassSuitProtocol.IsExactNavigation(
                CreateAction(
                    ClassSuitProtocol.SpartaNpcId,
                    (int)ClassSuitWireOperation.ExchangeTierOne,
                    static args => args[6] = 100),
                (int)ClassSuitWireOperation.ExchangeTierOne),
            "selected gear cannot be mistaken for navigation");
    }

    private static void CheckConversionMutations()
    {
        var conversions = new[]
        {
            ClassSuitWireOperation.ExchangeTierOne,
            ClassSuitWireOperation.ConvertToCommon,
            ClassSuitWireOperation.UpgradeTierTwo,
            ClassSuitWireOperation.UpgradeTierThree,
            ClassSuitWireOperation.UpgradeTierFour
        };

        foreach (var endpoint in Endpoints)
        {
            foreach (var operation in conversions)
            {
                var packet = CreateAction(
                    endpoint,
                    (int)operation,
                    args =>
                    {
                        args[ClassSuitProtocol.EquipmentArgumentIndex] = 112;
                        if (operation !=
                            ClassSuitWireOperation.ConvertToCommon)
                        {
                            args[ClassSuitProtocol.MaterialArgumentIndex] = 195;
                        }
                    });
                Check.True(
                    ClassSuitProtocol.TryReadConversionMutation(
                        packet,
                        out var parsedNpc,
                        out var intent),
                    $"{endpoint}/{operation} exact mutation parses");
                Check.Equal(endpoint, parsedNpc, $"{operation} endpoint");
                Check.Equal(
                    (int)operation,
                    (int)intent.Operation,
                    $"{operation} intent operation");
                Check.Equal(12, intent.EquipmentKitBagSlot, "gear slot");
                Check.Equal(
                    operation == ClassSuitWireOperation.ConvertToCommon
                        ? ClassSuitProtocol.NoKitBagSlot
                        : 95,
                    intent.MaterialKitBagSlot,
                    $"{operation} material slot");
                Check.True(
                    !ClassSuitProtocol.IsExactNavigation(
                        packet,
                        (int)operation),
                    $"{operation} mutation is not navigation");
            }
        }

        var stockScratchMutation = CreateAction(
            ClassSuitProtocol.SpartaNpcId,
            (int)ClassSuitWireOperation.ExchangeTierOne,
            static args =>
            {
                args[0] = 0;
                args[ClassSuitProtocol.EquipmentArgumentIndex] = 100;
                args[ClassSuitProtocol.MaterialArgumentIndex] = 101;
            });
        Check.True(
            ClassSuitProtocol.TryReadConversionMutation(
                stockScratchMutation,
                out _,
                out var scratchIntent) &&
            scratchIntent.EquipmentKitBagSlot == 0 &&
            scratchIntent.MaterialKitBagSlot == 1,
            "stock unchecked-button arg-0 scratch is tolerated");

        Check.True(
            !ClassSuitProtocol.TryReadConversionMutation(
                CreateAction(
                    ClassSuitProtocol.SpartaNpcId,
                    (int)ClassSuitWireOperation.AddClassAttribute,
                    static args =>
                    {
                        args[6] = 100;
                        args[7] = 101;
                        args[8] = 102;
                    }),
                out _,
                out _),
            "class-attribute mutation is handled by its dedicated parser");
    }

    private static void CheckMalformedMutations()
    {
        var valid = CreateAction(
            ClassSuitProtocol.SpartaNpcId,
            (int)ClassSuitWireOperation.ExchangeTierOne,
            static args =>
            {
                args[6] = 100;
                args[7] = 101;
            });

        var badHeaderLength = valid.Buffer.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            badHeaderLength,
            ClassSuitProtocol.PacketBytes - 1);
        Reject(new GamePacket(badHeaderLength), "declared length mismatch");

        var truncated = valid.Buffer[..^4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            truncated,
            checked((ushort)truncated.Length));
        Reject(new GamePacket(truncated), "truncated request");

        var badOpcode = valid.Buffer.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            badOpcode.AsSpan(2),
            Opcodes.NpcFunctionActionResponse);
        Reject(new GamePacket(badOpcode), "wrong opcode");

        var wrongNpc = valid.Buffer.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongNpc.AsSpan(4), 9999);
        Reject(new GamePacket(wrongNpc), "wrong NPC");

        var wrongDialog = valid.Buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            wrongDialog.AsSpan(8),
            ClassSuitProtocol.DialogIndex + 1);
        Reject(new GamePacket(wrongDialog), "wrong dialogue");

        var duplicateDialog = valid.Buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            duplicateDialog.AsSpan(12),
            ClassSuitProtocol.DialogIndex + 1);
        Reject(new GamePacket(duplicateDialog), "mismatched duplicate dialogue");

        foreach (var invalidReference in new[] { 99, 196 })
        {
            Reject(
                CreateAction(
                    ClassSuitProtocol.SpartaNpcId,
                    (int)ClassSuitWireOperation.ExchangeTierOne,
                    args =>
                    {
                        args[6] = invalidReference;
                        args[7] = 101;
                    }),
                $"gear reference {invalidReference}");
            Reject(
                CreateAction(
                    ClassSuitProtocol.SpartaNpcId,
                    (int)ClassSuitWireOperation.ExchangeTierOne,
                    args =>
                    {
                        args[6] = 100;
                        args[7] = invalidReference;
                    }),
                $"material reference {invalidReference}");
        }

        Reject(
            CreateAction(
                ClassSuitProtocol.SpartaNpcId,
                (int)ClassSuitWireOperation.ExchangeTierOne,
                static args =>
                {
                    args[6] = 100;
                    args[7] = 100;
                }),
            "one slot cannot be both gear and material");
        Reject(
            CreateAction(
                ClassSuitProtocol.SpartaNpcId,
                (int)ClassSuitWireOperation.ExchangeTierOne,
                static args =>
                {
                    args[1] = 0;
                    args[6] = 100;
                    args[7] = 101;
                }),
            "unexpected non-slot argument");
        Reject(
            CreateAction(
                ClassSuitProtocol.SpartaNpcId,
                (int)ClassSuitWireOperation.ConvertToCommon,
                static args =>
                {
                    args[6] = 100;
                    args[7] = 101;
                }),
            "reverse conversion accepts no material");
    }

    private static void Reject(GamePacket packet, string description)
    {
        Check.True(
            !ClassSuitProtocol.TryReadConversionMutation(
                packet,
                out _,
                out _),
            description);
    }

    private static GamePacket CreateAction(
        uint npcId,
        int subId,
        Action<int[]>? configure = null)
    {
        var packet = new byte[ClassSuitProtocol.PacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            ClassSuitProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            ClassSuitProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16), subId);

        var arguments = Enumerable.Repeat(
            -1,
            ClassSuitProtocol.FunctionArgumentCount).ToArray();
        configure?.Invoke(arguments);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(20 + (index * sizeof(int))),
                arguments[index]);
        }
        return new GamePacket(packet);
    }

    private static void CheckResponse(
        byte[] packet,
        uint expectedNpcId,
        params int[] expectedSubIds)
    {
        Check.Equal(
            checked((ushort)packet.Length),
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "response length field");
        Check.Equal(
            Opcodes.NpcFunctionActionResponse,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "response opcode");
        Check.Equal(
            expectedNpcId,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
            "response NPC");
        Check.Equal(
            ClassSuitProtocol.DialogIndex,
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)),
            "response dialogue");
        Check.Equal(
            12 + (expectedSubIds.Length * sizeof(int)),
            packet.Length,
            "response length from bounded sub-ID count");
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
