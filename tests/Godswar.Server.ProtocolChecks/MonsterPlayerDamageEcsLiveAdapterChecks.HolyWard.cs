using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterPlayerDamageEcsLiveAdapterChecks
{
    private static async Task
        CheckHolyWardMitigatesLiveMonsterAttackAsync()
    {
        const uint monsterObjectId = 9_100;
        var unwardedDamage = await ObserveFirstMonsterHitAsync(
            monsterObjectId,
            status: null);
        Check.True(
            SkillStatusEffectCatalog.TryGet(94, out var holyWard),
            "live mitigation fixture resolves Holy Ward 5");
        var wardedDamage = await ObserveFirstMonsterHitAsync(
            monsterObjectId + 1,
            holyWard);
        var magicalDamage = await ObserveFirstMonsterHitAsync(
            monsterObjectId + 2,
            status: null,
            MonsterAttackDamageKind.Magical);
        var wardedMagicalDamage = await ObserveFirstMonsterHitAsync(
            monsterObjectId + 3,
            holyWard,
            MonsterAttackDamageKind.Magical);

        Check.Equal(
            531,
            unwardedDamage,
            "event-one live monster hit preserves the deterministic V1 critical damage");
        Check.Equal(
            398,
            wardedDamage,
            "Holy Ward 5 reduces the deterministic live critical by twenty-five percent");
        Check.Equal(
            560,
            magicalDamage,
            "AttackType 2 selects authored magic attack in the live handler");
        Check.Equal(
            476,
            wardedMagicalDamage,
            "Holy Ward 5 applies its fifteen-percent magic reduction to live magic damage");
    }

    private static async Task<int> ObserveFirstMonsterHitAsync(
        uint monsterObjectId,
        SkillStatusEffectDefinition? status,
        MonsterAttackDamageKind attackKind =
            MonsterAttackDamageKind.Physical)
    {
        var activeAt = DateTimeOffset.UtcNow;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs,
            gameplayCatalogs: CreateMonsterCombatCatalog(attackKind));
        var character = CreateCharacter();
        character.Id = checked(
            character.Id + (int)(monsterObjectId - 9_100));
        character.Name = $"HolyWardHero{monsterObjectId}";
        character.CurrentHp = 1_000;
        character.MaxHp = 1_000;
        var objectId = WorldObjectIds.ForPlayer(character.Id);
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [CreateMonster(
                monsterObjectId,
                character.PositionX,
                character.PositionZ,
                tier: 100)],
            activeAt);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId,
            joinedAt: activeAt);

        if (status is { } definition)
        {
            Check.True(
                await registry.ApplyRuntimeStatusAndPublishAsync(
                    socket.Session,
                    definition,
                    activeAt,
                    "holy-ward-live-hit",
                    CancellationToken.None),
                "live Holy Ward status is accepted");
            await socket.ReadPacketAsync(340);
            Check.Equal(
                definition.PhysicalDamageReduction,
                registry.GetRuntimePhysicalDamageReduction(
                    socket.Session,
                    activeAt),
                "live Holy Ward mitigation remains in the " +
                "authoritative runtime status state");
        }

        Check.True(
            registry.TryApplyMonsterDamage(
                character.CurrentMap,
                monsterObjectId,
                damage: 1,
                attackerCharacterId: character.Id,
                now: activeAt,
                out _),
            "live Holy Ward fixture establishes monster aggro");
        await registry.AdvanceMonsterWorldOnceAsync(
            activeAt,
            CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(
            activeAt + MonsterMapRuntime.TickInterval,
            CancellationToken.None);

        var damage = 1_000 - character.CurrentHp;
        registry.Remove(socket.Session);
        return damage;
    }

    private static GameplayRuntimeCatalogs CreateMonsterCombatCatalog(
        MonsterAttackDamageKind attackKind)
    {
        const string templateKey = "MitigationRaceMonster";
        var content = new GameplayContentCatalog(
            Maps: [],
            AddressPoints: [],
            Links: [],
            MonsterTemplates:
            [
                new GameplayMonsterTemplateDefinition(
                    "map:0",
                    "map",
                    SourceMapId: 0,
                    "Sparta",
                    templateKey,
                    templateKey,
                    "normal",
                    IsBoss: false,
                    IsElite: false,
                    IsPet: false,
                    AttackType: (short)attackKind,
                    CollisionRange: 2.5f)
            ],
            WorldBosses: [],
            PendingWorldBossAreas: [],
            SkillCombatDefinitions: []);
        return GameplayRuntimeCatalogs.Create(content);
    }

    private static async Task CheckDeterministicMonsterMissAsync()
    {
        const uint monsterObjectId = 9_104;
        var activeAt = DateTimeOffset.UtcNow;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs,
            gameplayCatalogs: CreateMonsterCombatCatalog(
                MonsterAttackDamageKind.Physical));
        var character = CreateCharacter();
        character.Id += 4;
        character.Name = "MonsterMissHero";
        character.CurrentHp = 1_000;
        character.MaxHp = 1_000;
        character.CalculatedStats = new CharacterStats
        {
            Dodge = int.MaxValue
        };
        var objectId = WorldObjectIds.ForPlayer(character.Id);
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [CreateMonster(
                monsterObjectId,
                character.PositionX,
                character.PositionZ,
                tier: 100)],
            activeAt);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId,
            joinedAt: activeAt);

        Check.True(
            registry.TryApplyMonsterDamage(
                character.CurrentMap,
                monsterObjectId,
                damage: 1,
                attackerCharacterId: character.Id,
                now: activeAt,
                out _),
            "monster miss fixture establishes aggro");
        await registry.AdvanceMonsterWorldOnceAsync(
            activeAt,
            CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(
            activeAt + MonsterMapRuntime.TickInterval,
            CancellationToken.None);

        await socket.ReadPacketAsync(24);
        var damagePacket = await socket.ReadPacketAsync(30);
        Check.Equal(
            uint.MaxValue,
            System.Buffers.Binary.BinaryPrimitives
                .ReadUInt32LittleEndian(damagePacket.AsSpan(24, 4)),
            "live monster miss publishes the captured damage sentinel");
        Check.Equal(
            (byte)CombatHitOutcome.Miss,
            damagePacket[29],
            "live monster miss publishes the captured outcome byte");
        Check.Equal(
            1_000,
            character.CurrentHp,
            "live monster miss leaves HP unchanged");
        Check.Equal(
            0L,
            character.VitalsRevision,
            "live monster miss leaves the vitals revision unchanged");
        var decision = registry.GetPlayerVitalsDamageEcsDiagnostics(
            socket.Session);
        Check.True(
            decision is
            {
                Applied: false,
                RejectionReason:
                    MonsterPlayerDamageRejectionReason.ZeroDamage,
                LastAttackEventId: 1
            },
            "live monster miss consumes the stable attack event ID");

        registry.Remove(socket.Session);
    }
}
