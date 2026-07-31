using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetPresenceProtocolChecks
{
    private const int AccountId = 13;
    private const int CharacterId = 2;
    private const uint PetId = 1;

    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    public static async Task RunAsync()
    {
        CheckOpcodeCatalog();
        CheckResultFrames();
        await CheckSuccessfulActionAsync(
            Opcodes.PetTakeRequest,
            PetPresenceOperation.Take,
            PetOperationResultCode.TakeSucceeded);
        await CheckSuccessfulActionAsync(
            Opcodes.PetCallOutRequest,
            PetPresenceOperation.CallOut,
            PetOperationResultCode.CallOutSucceeded);
        await CheckSuccessfulActionAsync(
            Opcodes.PetRecallRequest,
            PetPresenceOperation.Recall,
            PetOperationResultCode.RecallSucceeded);
        await CheckRejectedActionAsync();
        await CheckMalformedActionAsync();
    }

    private static void CheckOpcodeCatalog()
    {
        Check.Equal(
            (ushort)10_239,
            Opcodes.PetTakeRequest,
            "pet Take request opcode");
        Check.Equal(
            (ushort)10_240,
            Opcodes.PetCallOutRequest,
            "pet Call Out request opcode");
        Check.Equal(
            (ushort)10_241,
            Opcodes.PetRecallRequest,
            "pet Recall request opcode");
        Check.Equal(
            (ushort)10_244,
            Opcodes.PetOperationResult,
            "pet operation result opcode");
        Check.Equal(
            nameof(Opcodes.PetTakeRequest),
            Opcodes.Name(Opcodes.PetTakeRequest),
            "pet opcode has a diagnostic name");
    }

    private static void CheckResultFrames()
    {
        CheckFrame(
            PetOperationResultCode.TakeSucceeded,
            "090004280100000001");
        CheckFrame(
            PetOperationResultCode.TakeFailed,
            "090004280100000002");
        CheckFrame(
            PetOperationResultCode.RecallSucceeded,
            "090004280100000005");
        CheckFrame(
            PetOperationResultCode.RecallFailed,
            "090004280100000006");
        CheckFrame(
            PetOperationResultCode.CallOutSucceeded,
            "090004280100000007");
        CheckFrame(
            PetOperationResultCode.CallOutFailed,
            "090004280100000008");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetOperationResult(
                PetId,
                (PetOperationResultCode)0),
            "undefined native pet result is rejected");
    }

    private static async Task CheckSuccessfulActionAsync(
        ushort opcode,
        PetPresenceOperation expectedOperation,
        PetOperationResultCode expectedCode)
    {
        var store = new PetPresenceStore(
            PetPresenceTransitionStatus.Succeeded);
        var response = await InvokeAsync(
            store,
            CreateActionPacket(opcode, PetId));

        Check.True(
            response.SequenceEqual(
                PacketBuilder.PetOperationResult(PetId, expectedCode)),
            $"{expectedOperation} emits its native success code");
        Check.Equal(1, store.CallCount, $"{expectedOperation} persists once");
        Check.Equal(AccountId, store.AccountId, "authenticated pet account");
        Check.Equal(
            CharacterId,
            store.CharacterId,
            "active pet character");
        Check.Equal((long)PetId, store.PetId, "authoritative pet ID");
        Check.True(
            store.Operation == expectedOperation,
            $"{expectedOperation} reaches the matching store transition");
    }

    private static async Task CheckRejectedActionAsync()
    {
        var store = new PetPresenceStore(
            PetPresenceTransitionStatus.PetNotTaken);
        var response = await InvokeAsync(
            store,
            CreateActionPacket(Opcodes.PetCallOutRequest, PetId));

        Check.True(
            response.SequenceEqual(
                PacketBuilder.PetOperationResult(
                    PetId,
                    PetOperationResultCode.CallOutFailed)),
            "store rejection emits Call Out failure");
        Check.Equal(1, store.CallCount, "rejected action reaches store once");
    }

    private static async Task CheckMalformedActionAsync()
    {
        var store = new PetPresenceStore(
            PetPresenceTransitionStatus.Succeeded);
        var malformed = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(malformed, 7);
        BinaryPrimitives.WriteUInt16LittleEndian(
            malformed.AsSpan(2),
            Opcodes.PetTakeRequest);
        var response = await InvokeAsync(store, new GamePacket(malformed));

        Check.True(
            response.SequenceEqual(
                PacketBuilder.PetOperationResult(
                    0,
                    PetOperationResultCode.TakeFailed)),
            "malformed Take receives a bounded failure");
        Check.Equal(
            0,
            store.CallCount,
            "malformed pet request cannot reach persistence");
    }

    private static void CheckFrame(
        PetOperationResultCode code,
        string expectedHex)
    {
        var packet = PacketBuilder.PetOperationResult(PetId, code);
        Check.True(
            packet.SequenceEqual(Convert.FromHexString(expectedHex)),
            $"{code} frame bytes");
    }

    private static async Task<byte[]> InvokeAsync(
        PetPresenceStore store,
        GamePacket packet)
    {
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var handler = new GameClientHandler(
            session,
            store,
            new GameSessionRegistry(
                store: null,
                zodiacEnergyOptions: null,
                monsterRuntimeMode: MonsterRuntimeMode.Ecs,
                playerRuntimeMode: PlayerRuntimeMode.Ecs),
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty);
        SetField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "test2"));
        SetField(
            handler,
            "_character",
            new GameCharacter
            {
                Id = CharacterId,
                AccountId = AccountId,
                Name = "test2"
            });

        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;

        var clearBytes = transport.WrittenBytes;
        new PacketCipher().Transform(clearBytes);
        return clearBytes;
    }

    private static GamePacket CreateActionPacket(
        ushort opcode,
        uint petId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), petId);
        return new GamePacket(packet);
    }

    private static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

    private sealed class PetPresenceStore : GameStoreTestStub
    {
        private readonly PetPresenceTransitionStatus _status;

        public PetPresenceStore(
            PetPresenceTransitionStatus status)
        {
            _status = status;
        }

        public int CallCount { get; private set; }

        public int AccountId { get; private set; }

        public int CharacterId { get; private set; }

        public long PetId { get; private set; }

        public PetPresenceOperation Operation { get; private set; }

        public override Task<PetPresenceTransitionResult>
            TransitionPetPresenceAsync(
                int accountId,
                int characterId,
                long petId,
                PetPresenceOperation operation,
                CancellationToken cancellationToken = default)
        {
            CallCount++;
            AccountId = accountId;
            CharacterId = characterId;
            PetId = petId;
            Operation = operation;
            return Task.FromResult(
                new PetPresenceTransitionResult(
                    _status,
                    petId,
                    IsCarried:
                        _status ==
                        PetPresenceTransitionStatus.Succeeded,
                    IsSummoned:
                        _status ==
                        PetPresenceTransitionStatus.Succeeded &&
                        operation == PetPresenceOperation.CallOut));
        }
    }
}
