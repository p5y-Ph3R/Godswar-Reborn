using System.Buffers.Binary;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private const int PriestAccountId = 738;
    private const int PriestCharacterId = 8_738;
    private const uint PriestAreaHealSkillId = 760;
    private const int PriestInitialHealth = 100;
    private const int PriestInitialMana = 500;

    private static async Task CheckPinnedProjectedPriestAreaHealAsync()
    {
        Check.True(
            GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                checked((int)PriestAreaHealSkillId),
                out var authored),
            "Area Heal 1 combat definition exists");
        await using var fixture = await PriestFixture.CreateAsync();
        fixture.Character.ZodiacSkillGridLevels[0] = 1;
        fixture.Character.ZodiacSkillGridSkillIds[0] = 10_076;
        var projected = ZodiacOffensiveSkillProjection.Resolve(
            fixture.Character,
            authored);
        if (!PriestHealingSkillCatalog.TryResolve(
                projected.Skill,
                out var healing))
        {
            throw new InvalidOperationException(
                "Projected Area Heal was not recognized.");
        }
        Check.True(
            projected.Applied &&
            healing.Kind == PriestHealingSkillKind.Area,
            "Area Heal retains its authoritative projected healing definition");

        await InvokePacketAsync(
            fixture.Handler,
            CreatePriestAreaHealPacket());
        var visual = await fixture.Socket.ReadPacketAsync(40);
        Check.Equal(
            Opcodes.SkillCast,
            ReadOpcode(visual),
            "projected Area Heal publishes its intonation visual");
        await Task.Delay(150);
        Check.True(
            fixture.Character.CurrentHp == PriestInitialHealth &&
            fixture.Character.CurrentMp == PriestInitialMana,
            "projected Area Heal has no effect before completion");

        lock (fixture.Character.ZodiacSync)
        {
            fixture.Character.ZodiacSkillGridSkillIds[0] =
                ZodiacSkillGridCatalog.NoSelectedSkill;
        }
        await fixture.WaitForCompletionAsync(authored.CastTime);

        Check.True(
            fixture.Character.ZodiacSkillGridSkillIds[0] ==
                ZodiacSkillGridCatalog.NoSelectedSkill &&
            fixture.Character.CurrentHp ==
                PriestInitialHealth + healing.HealAmount &&
            fixture.Character.CurrentMp ==
                PriestInitialMana - projected.Skill.Mp,
            "intoned Area Heal pins projected healing and MP through a mid-cast Zodiac deselection");
    }

    private static GamePacket CreatePriestAreaHealPacket()
    {
        var packet = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.SkillCast);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            LocalObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            PriestAreaHealSkillId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16),
            LocalObjectId);
        return new GamePacket(packet);
    }

    private sealed class PriestFixture : IAsyncDisposable
    {
        private PriestFixture(
            RuntimePolicySessionSocket socket,
            GameSessionRegistry registry,
            GameCharacter character,
            GameClientHandler handler)
        {
            Socket = socket;
            Registry = registry;
            Character = character;
            Handler = handler;
        }

        public RuntimePolicySessionSocket Socket { get; }

        public GameSessionRegistry Registry { get; }

        public GameCharacter Character { get; }

        public GameClientHandler Handler { get; }

        public static async Task<PriestFixture> CreateAsync()
        {
            var socket = await RuntimePolicySessionSocket.CreateAsync();
            var store = new PriestCombatStore();
            var character = new GameCharacter
            {
                Id = PriestCharacterId,
                AccountId = PriestAccountId,
                Name = "ZodiacAreaHealSnapshot",
                CreatedUtc = DateTime.UtcNow,
                Camp = GameDefaults.SpartaCamp,
                CurrentMap = 0,
                Profession = 2,
                Level = 80,
                CurrentHp = PriestInitialHealth,
                MaxHp = 1_000,
                CurrentMp = PriestInitialMana,
                MaxMp = PriestInitialMana,
                CalculatedStats = new CharacterStats()
            };
            var registry = new GameSessionRegistry(
                store: null,
                zodiacEnergyOptions: null,
                MonsterRuntimeMode.Ecs,
                PlayerRuntimeMode.Ecs);
            registry.JoinMap(
                socket.Session,
                PriestAccountId,
                character,
                WorldObjectIds.ForPlayer(PriestCharacterId),
                worldReady: true);
            var handler = new GameClientHandler(
                socket.Session,
                store,
                registry,
                CharacterSnapshotReaderTestFixtures.Unused,
                WorldContentReaderTestFixtures.Empty);
            SetField(
                handler,
                "_account",
                new AccountIdentity(PriestAccountId, "intoned-priest"));
            SetField(handler, "_character", character);
            SetField(handler, "_registered", true);
            SetField(handler, "_worldPresenceAnnounced", true);
            return new PriestFixture(socket, registry, character, handler);
        }

        public async Task WaitForCompletionAsync(TimeSpan castTime)
        {
            var timeout = DateTimeOffset.UtcNow +
                castTime + TimeSpan.FromSeconds(2);
            while (DateTimeOffset.UtcNow < timeout)
            {
                lock (Character.VitalsSync)
                {
                    if (Character.CurrentMp != PriestInitialMana &&
                        Character.CurrentHp != PriestInitialHealth)
                    {
                        return;
                    }
                }

                await Task.Delay(20);
            }

            throw new TimeoutException(
                "Projected Area Heal did not complete in time.");
        }

        public async ValueTask DisposeAsync()
        {
            var stop = StopPendingSkillCastsMethod.Invoke(
                Handler,
                null) as Task
                ?? throw new InvalidOperationException(
                    "StopPendingSkillCastsAsync returned no task.");
            await stop;
            Registry.Remove(Socket.Session);
            await Socket.DisposeAsync();
        }
    }

    private sealed class PriestCombatStore : GameStoreTestStub
    {
        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillState>>(
                [new SkillState
                {
                    SkillId = checked((int)PriestAreaHealSkillId),
                    Level = 1
                }]);
    }
}
