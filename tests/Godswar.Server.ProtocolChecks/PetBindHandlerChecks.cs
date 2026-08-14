using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetBindHandlerChecks
{
    public const string CheckName =
        "Authoritative summoned-pet bind handler";
    private const uint LocalPlayerObjectId = 0x00001448;
    private static readonly MethodInfo HandlePetManagerMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePetManagerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "GameClientHandler.HandlePetManagerAsync was not found.");

    public static async Task RunAsync()
    {
        CheckNativeResultMapping();
        await CheckRawLocalProjectionAsync();
        await CheckDelayedReplayTargetsReceiptPetAsync();
        await CheckRawProductionFailsClosedAsync();
    }

    private static void CheckNativeResultMapping()
    {
        (PetDurableReceiptStatus Status, uint Result)[] cases =
        [
            (PetDurableReceiptStatus.PetBound, 1073),
            (PetDurableReceiptStatus.PetAlreadyBound, 1072),
            (PetDurableReceiptStatus.PetBindPetNotSummoned, 1075)
        ];
        foreach (var (status, result) in cases)
        {
            Check.Equal(
                result,
                GameClientHandler.ResolvePetLegacyResultCode(
                    CreateReceipt(status, CreatePet(true, 8))),
                $"pet bind {status} maps to stock result {result}");
        }
    }

    private static async Task CheckRawLocalProjectionAsync()
    {
        var character = CreateCharacter();
        var current = CreatePet(isBound: true, revision: 8);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            BindPet = envelope =>
                PetDurableExecutionResult.Committed(
                    CreateReceipt(
                        PetDurableReceiptStatus.PetBound,
                        current,
                        envelope))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            character,
            character,
            [current],
            executor,
            hasLocalDevelopmentCapability: true);

        await InvokeAsync(fixture.Handler, CreateRequest());

        Check.True(
            executor.BindPetCount == 1 &&
            executor.BindPetEnvelope is { } envelope &&
            envelope.Family == CommandFamily.PetBind &&
            envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.OperationId != Guid.Empty &&
            envelope.Command.Identity.RawLocalConnectionId ==
                envelope.Connection.ConnectionId,
            "raw LocalDevelopment bind reaches family 53 with a connection-scoped identity");

        byte[][] expected =
        [
            PacketBuilder.PetOperationResult(
                checked((uint)current.PetId),
                PetOperationResultCode.RecallSucceeded),
            PacketBuilder.PetAppearanceRefresh(
                PetContentTestCatalog.Instance,
                current),
            PacketBuilder.PetOperationResult(
                checked((uint)current.PetId),
                PetOperationResultCode.CallOutSucceeded),
            PacketBuilder.PetWorldPresence(
                checked((uint)current.PetId),
                LocalPlayerObjectId),
            PacketBuilder.NpcFunctionActionResponse(
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.PetBindSucceededResultSubId)
        ];
        var packets = fixture.ReadLegacyPackets();
        Check.Equal(
            expected.Length,
            packets.Count,
            "bind emits one non-destructive authoritative projection");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.True(
                expected[index].SequenceEqual(packets[index]),
                $"bind projection frame {index} is canonical");
        }
        Check.True(
            packets.All(packet => ReadOpcode(packet) != 10_237) &&
            packets[1].Length == 72 &&
            packets[1][69] == 1,
            "bind projects the bound flag without rebuilding the pet list");
    }

    private static async Task CheckDelayedReplayTargetsReceiptPetAsync()
    {
        var character = CreateCharacter();
        var historical = CreatePet(isBound: true, revision: 8);
        var receiptPet = historical with
        {
            Revision = 10,
            IsCarried = false,
            IsSummoned = false
        };
        var newer = CreatePet(isBound: false, revision: 3) with
        {
            PetId = historical.PetId + 1,
            Name = "Newer Pet"
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            BindPet = envelope =>
                PetDurableExecutionResult.Duplicate(
                    CreateReceipt(
                        PetDurableReceiptStatus.PetBound,
                        historical,
                        envelope))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            character,
            character,
            [receiptPet, newer],
            executor,
            hasLocalDevelopmentCapability: true);

        await InvokeAsync(fixture.Handler, CreateRequest());

        var packets = fixture.ReadLegacyPackets();
        Check.Equal(
            2,
            packets.Count,
            "delayed bind replay emits only receipt-pet refresh and result");
        Check.True(
            PacketBuilder.PetAppearanceRefresh(
                PetContentTestCatalog.Instance,
                receiptPet).SequenceEqual(packets[0]) &&
            !packets.Any(packet =>
                ReadOpcode(packet) is 10_237 or 10_248),
            "delayed bind replay never cycles a newly summoned pet");
    }

    private static async Task CheckRawProductionFailsClosedAsync()
    {
        var character = CreateCharacter();
        var pet = CreatePet(isBound: false, revision: 7);
        var executor = new DelegatingPetDurableCommandExecutor();
        await using var fixture = PetDurableRawHandlerFixture.Create(
            character,
            character,
            [pet],
            executor,
            hasLocalDevelopmentCapability: false);
        await InvokeAsync(fixture.Handler, CreateRequest());
        Check.True(
            executor.BindPetCount == 0 &&
            fixture.ReadLegacyPackets().Count == 0,
            "raw production bind fails closed without a secure operation ID");
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        BindRequest request)
    {
        var task = HandlePetManagerMethod.Invoke(
            handler,
            [
                request.Packet,
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.PetBindMenuSubId,
                request.Arguments,
                CancellationToken.None
            ]) as Task ?? throw new InvalidOperationException(
                "Pet Manager bind handler returned no task.");
        await task;
    }

    private static BindRequest CreateRequest()
    {
        var arguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        arguments[0] = PetManagerProtocol.PetBindActionSubId;
        var bytes = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            PetManagerProtocol.AthensNpcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            PetManagerProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            PetManagerProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(16),
            PetManagerProtocol.PetBindMenuSubId);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + (index * sizeof(int))),
                arguments[index]);
        }
        return new(new GamePacket(bytes), arguments);
    }

    private static PetDurableReceipt CreateReceipt(
        PetDurableReceiptStatus status,
        PetBootstrapSnapshot pet,
        CommandEnvelope<PetBindCommand>? envelope = null) =>
        new(
            CommandFamily.PetBind,
            status,
            AccountId: envelope?.Subject.AccountId ??
                PetEggHatchProtocolChecks.AccountId,
            CharacterId: envelope?.Subject.CharacterId ??
                PetEggHatchProtocolChecks.CharacterId,
            KitBagSlot: -1,
            EquipmentSlot: -1,
            PetId: status ==
                PetDurableReceiptStatus.PetBindPetNotSummoned
                    ? 0
                    : pet.PetId,
            PetLevel: status ==
                PetDurableReceiptStatus.PetBindPetNotSummoned
                    ? (short)0
                    : pet.Level,
            PetExperience: status ==
                PetDurableReceiptStatus.PetBindPetNotSummoned
                    ? 0
                    : pet.Experience,
            PetRevision: status ==
                PetDurableReceiptStatus.PetBindPetNotSummoned
                    ? 0
                    : pet.Revision,
            IsCarried: status !=
                PetDurableReceiptStatus.PetBindPetNotSummoned,
            IsSummoned: status !=
                PetDurableReceiptStatus.PetBindPetNotSummoned,
            PresenceOperation: 0,
            AggregateRevision: 4,
            AuditReference: "bind-handler-check",
            OutboxEventId: status == PetDurableReceiptStatus.PetBound
                ? Guid.NewGuid()
                : null);

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = PetEggHatchProtocolChecks.CharacterId,
            AccountId = PetEggHatchProtocolChecks.AccountId,
            Name = "test2",
            KitBag = GameDefaults.EmptyKitBag,
            Equipment = GameDefaults.DefaultEquipment(1)
        };

    private static PetBootstrapSnapshot CreatePet(
        bool isBound,
        long revision)
    {
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            50m,
            new Random(50));
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_500,
            new Random(3_500));
        return PetEggHatchProtocolChecks.CreatePet(savvy, growth) with
        {
            Name = "Bind Test",
            IsBound = isBound,
            IsCarried = true,
            IsSummoned = true,
            ContributesToCharacter = false,
            Revision = revision
        };
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));

    private sealed record BindRequest(
        GamePacket Packet,
        int[] Arguments);
}
