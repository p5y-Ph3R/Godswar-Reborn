using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    ZodiacSkillGridActivationDurableHandlerChecks
{
    private const int AccountId = 7;
    private const int CharacterId = 19;
    private const int GridIndex = 1;
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static HandlerFixture CreateFixture(
        IZodiacSkillGridActivationCommandExecutor? executor,
        ZodiacCompatibilityStore? store = null)
    {
        store ??= new ZodiacCompatibilityStore();
        var transport = new ScriptedLegacyByteTransport();
        var session = new ClientSession(transport);
        var registry = new GameSessionRegistry(store);
        var character = CreateCharacter();
        GameHandlerOwnershipTestFences.Bind(
            registry,
            session,
            AccountId,
            character);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            zodiacSkillGridActivationCommands: executor);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = AccountId,
                Username = "zodiac-handler-check"
            });
        SetField(handler, "_character", character);
        return new HandlerFixture(
            session,
            transport,
            registry,
            handler,
            character,
            store,
            executor as CapturingExecutor);
    }

    private static GameCharacter CreateCharacter()
    {
        var levels = ZodiacSkillGridCatalog.CreateEmptyLevels();
        var skillIds = ZodiacSkillGridCatalog.CreateEmptySkillIds();
        levels[4] = 7;
        skillIds[4] = 10_061;
        return new GameCharacter
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "ZodiacProjectionHero",
            Profession = 1,
            Level = 80,
            CurrentMap = 3,
            CurrentHp = 7_777,
            CurrentMp = 888,
            Experience = 123_456,
            TalentExperience = 67,
            TalentPoints = 890,
            Silver = 654_321,
            Gold = 5_000,
            Equipment = GameDefaults.DefaultEquipment(1),
            KitBag = GameDefaults.StarterKitBag,
            ZodiacType = 2,
            ZodiacLevel = 9,
            ZodiacEnergy = 12_345,
            ZodiacSkillGridLevels = levels,
            ZodiacSkillGridSkillIds = skillIds
        };
    }

    private static GamePacket CreateActivationPacket(
        int gridIndex = GridIndex,
        int value2 = -1,
        int value3 = 0)
    {
        var packet = Convert.FromHexString(
            "18003928000000000000640001000000FFFFFFFF00000000");
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12, sizeof(int)),
            gridIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16, sizeof(int)),
            value2);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(20, sizeof(int)),
            value3);
        return new GamePacket(packet);
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet,
        CancellationToken cancellationToken = default)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [packet, cancellationToken]) as Task
            ?? throw new InvalidOperationException(
                "Zodiac handler did not return a task.");
        await invocation;
    }

    private static ZodiacSkillGridActivationExecutionReceipt Receipt() =>
        new(
            CharacterId,
            GridIndex,
            goldCost: 2_300,
            goldBefore: 5_000,
            goldAfter: 2_700,
            currentLevel: 1,
            selectedSkillId: -1,
            walletRevision: 1,
            auditReference: "audit:zodiac-handler",
            outboxEventId:
                Guid.Parse("B9FA4184-68C6-4C22-A7EF-119F2ED97B67"));

    private static IReadOnlyList<byte[]> ReadPackets(
        ScriptedLegacyByteTransport transport)
    {
        var clear = transport.WrittenBytes;
        new PacketCipher().Transform(clear);
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < clear.Length)
        {
            if (clear.Length - offset < 4)
            {
                throw new InvalidDataException(
                    "Zodiac response ended inside a packet header.");
            }
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clear.AsSpan(offset, sizeof(ushort)));
            if (length < 4 || length > clear.Length - offset)
            {
                throw new InvalidDataException(
                    "Zodiac response has an invalid packet length.");
            }
            packets.Add(clear.AsSpan(offset, length).ToArray());
            offset += length;
        }
        return packets;
    }

    private static ushort Opcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));

    private static ushort ZodiacSid(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(10, sizeof(ushort)));

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

    private sealed record HandlerFixture(
        ClientSession Session,
        ScriptedLegacyByteTransport Transport,
        GameSessionRegistry Registry,
        GameClientHandler Handler,
        GameCharacter Character,
        ZodiacCompatibilityStore Store,
        CapturingExecutor? Executor) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }

    private sealed class CapturingExecutor(
        Func<CancellationToken,
            Task<ZodiacSkillGridActivationExecutionResult>> execute) :
        IZodiacSkillGridActivationCommandExecutor
    {
        public int Count { get; private set; }
        public CommandEnvelope<ZodiacSkillGridActivationCommand>?
            LastEnvelope
        { get; private set; }

        public Task<ZodiacSkillGridActivationExecutionResult>
            ExecuteAsync(
                CommandEnvelope<ZodiacSkillGridActivationCommand> envelope,
                CancellationToken cancellationToken = default)
        {
            Count++;
            LastEnvelope = envelope;
            return execute(cancellationToken);
        }
    }

    private sealed class ZodiacCompatibilityStore : GameStoreTestStub
    {
        public ZodiacSkillGridActivationResult? Result { get; set; }
        public int ActivationCount { get; private set; }

        public override Task<ZodiacSkillGridActivationResult?>
            ActivateZodiacSkillGridAsync(
                int accountId,
                int characterId,
                int gridIndex,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivationCount++;
            Check.Equal(
                AccountId,
                accountId,
                "compatibility activation account");
            Check.Equal(
                CharacterId,
                characterId,
                "compatibility activation character");
            Check.Equal(
                GridIndex,
                gridIndex,
                "compatibility activation grid");
            return Task.FromResult(Result);
        }
    }
}
