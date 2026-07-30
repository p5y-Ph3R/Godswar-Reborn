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
    ZodiacSkillGridUpgradeDurableHandlerChecks
{
    private const int AccountId = 7;
    private const int CharacterId = 19;
    private const int GridIndex = 1;
    private static readonly Guid OperationId =
        Guid.Parse("fe8f5cd0-80d4-4d86-a0a9-9169ae08f731");
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static HandlerFixture CreateFixture(
        ZodiacSkillGridUpgradeExecutionResult? execution,
        bool configureExecutor = true,
        ZodiacUpgradeCompatibilityStore? store = null,
        CapturingExecutor? executorOverride = null)
    {
        store ??= new ZodiacUpgradeCompatibilityStore();
        var transport = new ZodiacUpgradeCaptureTransport();
        var session = new ClientSession(transport);
        var registry = new GameSessionRegistry(store);
        var registryMirror = CreateCharacter();
        var ownership = GameHandlerOwnershipTestFences.Bind(
            registry,
            session,
            AccountId,
            registryMirror);
        registry.JoinMap(
            session,
            AccountId,
            registryMirror,
            objectId: 0x0000_1448);
        var character = CreateCharacter();
        character.CheckpointOwnerId = ownership.OwnerId;
        character.CheckpointOwnerGeneration = ownership.Generation;
        var executor = configureExecutor
            ? executorOverride ?? new CapturingExecutor(
                (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(
                        execution ??
                        throw new InvalidOperationException(
                            "No Zodiac execution was configured."));
                })
            : null;
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            zodiacSkillGridUpgradeCommands: executor);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = AccountId,
                Username = "zodiac-upgrade-handler-check"
            });
        SetField(handler, "_character", character);
        return new HandlerFixture(
            session,
            transport,
            registry,
            handler,
            character,
            registryMirror,
            executor,
            store);
    }

    private static GameCharacter CreateCharacter()
    {
        var levels = ZodiacSkillGridCatalog.CreateEmptyLevels();
        var skillIds = ZodiacSkillGridCatalog.CreateEmptySkillIds();
        levels[GridIndex] = 1;
        skillIds[GridIndex] = 10_050;
        levels[4] = 7;
        skillIds[4] = 10_061;
        return new GameCharacter
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "DurableZodiacUpgradeHero",
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
            ZodiacEnergy = 1_000,
            ZodiacEnergyRemainderX100 = 50,
            ZodiacSkillGridLevels = levels,
            ZodiacSkillGridSkillIds = skillIds
        };
    }

    private static GamePacket CreateUpgradePacket(
        Guid? operationId = null,
        int gridIndex = GridIndex)
    {
        var packet = Convert.FromHexString(
            "1800392800000000FF00650001000000FFFFFFFF00000000");
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12, sizeof(int)),
            gridIndex);
        return new GamePacket(packet, operationId);
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
                "Zodiac upgrade handler did not return a task.");
        await invocation;
    }

    private static ZodiacSkillGridUpgradeExecutionReceipt
        SuccessfulReceipt() =>
        new(
            CharacterId,
            ZodiacSkillGridUpgradeReceiptStatus.Succeeded,
            GridIndex,
            previousLevel: 1,
            currentLevel: 2,
            currentZodiacLevel: 9,
            requiredZodiacLevel: 1,
            energyCost: 5,
            energyBefore: 1_000,
            energyRemainderBeforeX100: 50,
            energyAfter: 995,
            energyRemainderAfterX100: 50,
            talentPointCost: 7,
            talentPointsBefore: 890,
            talentPointsAfter: 883,
            selectedSkillId: 10_050,
            auditReference: "audit:zodiac-upgrade-handler",
            outboxEventId:
                Guid.Parse("a6f29f50-4195-451f-84d4-dde53962a091"));

    private static ZodiacSkillGridUpgradeExecutionReceipt
        RejectedReceipt(
            ZodiacSkillGridUpgradeReceiptStatus status)
    {
        byte level = 1;
        byte zodiacLevel = 9;
        byte requiredZodiacLevel = 1;
        var energyCost = 5;
        var energy = 1_000;
        var energyRemainderX100 = 50;
        var talentPointCost = 7;
        var talentPoints = 890;
        var selectedSkillId = 10_050;
        switch (status)
        {
            case ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid:
                level = 0;
                requiredZodiacLevel = 0;
                energyCost = 0;
                talentPointCost = 0;
                selectedSkillId = -1;
                break;
            case ZodiacSkillGridUpgradeReceiptStatus
                .MaximumLevelReached:
                level = 50;
                requiredZodiacLevel = 0;
                energyCost = 0;
                talentPointCost = 0;
                break;
            case ZodiacSkillGridUpgradeReceiptStatus
                .ZodiacLevelTooLow:
                zodiacLevel = 1;
                requiredZodiacLevel = 2;
                break;
            case ZodiacSkillGridUpgradeReceiptStatus
                .InsufficientEnergy:
                energy = 4;
                energyRemainderX100 = 50;
                break;
            case ZodiacSkillGridUpgradeReceiptStatus
                .InsufficientTalentPoints:
                talentPoints = 6;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new ZodiacSkillGridUpgradeExecutionReceipt(
            CharacterId,
            status,
            GridIndex,
            previousLevel: level,
            currentLevel: level,
            currentZodiacLevel: zodiacLevel,
            requiredZodiacLevel,
            energyCost,
            energyBefore: energy,
            energyRemainderBeforeX100: energyRemainderX100,
            energyAfter: energy,
            energyRemainderAfterX100: energyRemainderX100,
            talentPointCost,
            talentPointsBefore: talentPoints,
            talentPointsAfter: talentPoints,
            selectedSkillId,
            auditReference:
                $"audit:zodiac-upgrade-{status}",
            outboxEventId: null);
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

    private sealed record HandlerFixture(
        ClientSession Session,
        ZodiacUpgradeCaptureTransport Transport,
        GameSessionRegistry Registry,
        GameClientHandler Handler,
        GameCharacter Character,
        GameCharacter RegistryMirror,
        CapturingExecutor? Executor,
        ZodiacUpgradeCompatibilityStore Store) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }

    private sealed class CapturingExecutor(
        Func<CommandEnvelope<ZodiacSkillGridUpgradeCommand>,
            CancellationToken,
            Task<ZodiacSkillGridUpgradeExecutionResult>> execute) :
        IZodiacSkillGridUpgradeCommandExecutor
    {
        public int Count { get; private set; }
        public CommandEnvelope<ZodiacSkillGridUpgradeCommand>?
            LastEnvelope
        { get; private set; }

        public Task<ZodiacSkillGridUpgradeExecutionResult> ExecuteAsync(
            CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            Count++;
            LastEnvelope = envelope;
            return execute(envelope, cancellationToken);
        }
    }

    private sealed class ZodiacUpgradeCompatibilityStore :
        GameStoreTestStub
    {
        public ZodiacSkillGridUpgradeResult? Result { get; set; }
        public int UpgradeCount { get; private set; }

        public override Task<ZodiacSkillGridUpgradeResult?>
            UpgradeZodiacSkillGridAsync(
                int accountId,
                int characterId,
                int gridIndex,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpgradeCount++;
            Check.Equal(
                AccountId,
                accountId,
                "compatibility Zodiac upgrade account");
            Check.Equal(
                CharacterId,
                characterId,
                "compatibility Zodiac upgrade character");
            Check.Equal(
                GridIndex,
                gridIndex,
                "compatibility Zodiac upgrade grid");
            return Task.FromResult(Result);
        }
    }
}
