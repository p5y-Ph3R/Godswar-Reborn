using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static async Task CheckPveElementalReachAsync()
    {
        await CheckPveElementalTransactionAuthorityAsync();
        await CheckMonsterBossControlAuthorityAsync();
        await CheckMonsterDrenchCadenceAsync(MonsterRuntimeMode.Legacy);
        await CheckMonsterDrenchCadenceAsync(MonsterRuntimeMode.Ecs);
        await CheckMonsterStatusReadbacksAsync();
    }

    private static async Task CheckMonsterBossControlAuthorityAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var content = new GameplayContentCatalog(
            [],
            [],
            [],
            [
                ElementalMonsterTemplate("ElementalNormal", isBoss: false),
                ElementalMonsterTemplate("ElementalBoss", isBoss: true)
            ],
            [],
            [],
            []);
        var catalogs = GameplayRuntimeCatalogs.Create(content);
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs,
            gameplayCatalogs: catalogs);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var character = ElementalLiveCharacter(1_409, 49, ownership);
        SetElementalProfile(
            character,
            LiveProfile((
                ElementKind.Lightning,
                1,
                new ElementalEffectTotals(1_000, 0, 2_000))));
        var at = DateTimeOffset.UtcNow;
        const uint normalId = 9_409;
        const uint bossId = 9_410;
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [
                ElementalReachMonster(normalId, "ElementalNormal"),
                ElementalReachMonster(bossId, "ElementalBoss")
            ],
            at);
        BindElementalLiveSession(
            registry,
            socket.Session,
            character,
            ownership,
            at);

        var foundNormal = registry.TryGetMonsterSnapshot(
            socket.Session,
            character.CurrentMap,
            normalId,
            out var normal);
        var foundBoss = registry.TryGetMonsterSnapshot(
            socket.Session,
            character.CurrentMap,
            bossId,
            out var boss);
        Check.True(
            foundNormal &&
            foundBoss &&
            !catalogs.MonsterCombatProfiles
                .Resolve(normal.Definition).IsBoss &&
            catalogs.MonsterCombatProfiles.Resolve(boss.Definition).IsBoss,
            "published monster profiles preserve authoritative normal and boss control identity");

        var normalDamage = ApplyReachDirectDamage(
            registry,
            character,
            normal,
            at);
        var bossDamage = ApplyReachDirectDamage(
            registry,
            character,
            boss,
            at);
        var committed = await registry.CommitAndPublishPveElementalHitsAsync(
            socket.Session,
            character,
            CombatEventProvenance.DirectBasicAttack,
            [
                new(
                    FindReachApplicationEventId(
                        ElementKind.Lightning,
                        character,
                        normalId,
                        at,
                        49_001),
                    0,
                    normalDamage),
                new(
                    FindReachApplicationEventId(
                        ElementKind.Lightning,
                        character,
                        bossId,
                        at,
                        59_001),
                    1,
                    bossDamage)
            ],
            at,
            CancellationToken.None);
        Check.True(
            committed.Applications.Count == 2 &&
            committed.ControlCommits.Count == 1 &&
            committed.ControlCommits[0].ObjectId == normalId &&
            committed.ControlCommits[0].Applied,
            "direct Shock records both statuses but stuns only the authoritative normal monster");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(character.AccountId, socket.Session);
    }

    private static async Task CheckMonsterDrenchCadenceAsync(
        MonsterRuntimeMode mode)
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            mode,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var character = ElementalLiveCharacter(
            mode == MonsterRuntimeMode.Legacy ? 1_410 : 1_411,
            mode == MonsterRuntimeMode.Legacy ? 50 : 51,
            ownership);
        character.PositionX = 10f;
        SetElementalProfile(
            character,
            LiveProfile((
                ElementKind.Water,
                1,
                new ElementalEffectTotals(1_000, 0, 2_000))));
        var at = DateTimeOffset.UtcNow;
        var objectId = mode == MonsterRuntimeMode.Legacy
            ? 9_411u
            : 9_412u;
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [ElementalReachMonster(objectId, "ElementalDrench")],
            at);
        BindElementalLiveSession(
            registry,
            socket.Session,
            character,
            ownership,
            at);
        Check.True(
            registry.TryGetMonsterSnapshot(
                socket.Session,
                character.CurrentMap,
                objectId,
                out var target),
            $"{mode} Drench fixture resolves its monster");
        var damage = ApplyReachDirectDamage(
            registry,
            character,
            target,
            at);
        var eventId = FindReachApplicationEventId(
            ElementKind.Water,
            character,
            objectId,
            at,
            mode == MonsterRuntimeMode.Legacy ? 69_001UL : 79_001UL);
        var committed = await registry.CommitAndPublishPveElementalHitsAsync(
            socket.Session,
            character,
            CombatEventProvenance.DirectBasicAttack,
            [new(eventId, 0, damage)],
            at,
            CancellationToken.None);
        Check.True(
            committed.Applications is
            [{ Effect: ElementalEffectKind.Drench }],
            $"{mode} committed hit applies target-owned Drench");

        await registry.AdvanceMonsterWorldOnceAsync(
            at,
            CancellationToken.None);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                objectId,
                out var chaseStart) &&
            chaseStart.IsMoving,
            $"{mode} Drenched monster begins its authoritative chase");
        await registry.AdvanceMonsterWorldOnceAsync(
            at + MonsterMapRuntime.TickInterval,
            CancellationToken.None);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                objectId,
                out var beforeDrenchedStep) &&
            beforeDrenchedStep.X == chaseStart.X &&
            beforeDrenchedStep.Z == chaseStart.Z,
            $"{mode} Drench blocks the former full-speed movement deadline");

        var drenchedInterval = TimeSpan.FromTicks(checked(
            (MonsterMapRuntime.TickInterval.Ticks * 10_000L) / 9_000));
        await registry.AdvanceMonsterWorldOnceAsync(
            at + drenchedInterval,
            CancellationToken.None);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                objectId,
                out var afterDrenchedStep) &&
            (afterDrenchedStep.X != chaseStart.X ||
             afterDrenchedStep.Z != chaseStart.Z),
            $"{mode} Drench advances at the authored 90% chase cadence");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(character.AccountId, socket.Session);
    }

    private static async Task CheckMonsterStatusReadbacksAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(store: null);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var character = ElementalLiveCharacter(1_412, 52, ownership);
        var at = DateTimeOffset.UtcNow;
        const uint fractureId = 9_413;
        const uint dazzleId = 9_414;
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [
                ElementalReachMonster(fractureId, "ElementalFracture"),
                ElementalReachMonster(dazzleId, "ElementalDazzle")
            ],
            at);
        BindElementalLiveSession(
            registry,
            socket.Session,
            character,
            ownership,
            at);

        SetElementalProfile(character, LiveProfile((
            ElementKind.Earth,
            1,
            new ElementalEffectTotals(1_000, 0, 2_000))));
        Check.True(
            registry.TryGetMonsterSnapshot(
                socket.Session,
                character.CurrentMap,
                fractureId,
                out var fractureTarget),
            "Fracture readback fixture resolves its target");
        var fractureDamage = ApplyReachDirectDamage(
            registry,
            character,
            fractureTarget,
            at);
        _ = await registry.CommitAndPublishPveElementalHitsAsync(
            socket.Session,
            character,
            CombatEventProvenance.DirectBasicAttack,
            [new(
                FindReachApplicationEventId(
                    ElementKind.Earth,
                    character,
                    fractureId,
                    at,
                    89_001),
                0,
                fractureDamage)],
            at,
            CancellationToken.None);
        var fractured = registry.AdjustPveMonsterTargetStats(
            socket.Session,
            fractureDamage.Monster,
            at.AddMilliseconds(1),
            new CombatTargetStats
            {
                PhysicalDefense = 1_000,
                MagicDefense = 1_000
            });

        SetElementalProfile(character, LiveProfile((
            ElementKind.Light,
            1,
            new ElementalEffectTotals(1_000, 0, 2_000))));
        Check.True(
            registry.TryGetMonsterSnapshot(
                socket.Session,
                character.CurrentMap,
                dazzleId,
                out var dazzleTarget),
            "Dazzle readback fixture resolves its target");
        var dazzleDamage = ApplyReachDirectDamage(
            registry,
            character,
            dazzleTarget,
            at);
        _ = await registry.CommitAndPublishPveElementalHitsAsync(
            socket.Session,
            character,
            CombatEventProvenance.DirectBasicAttack,
            [new(
                FindReachApplicationEventId(
                    ElementKind.Light,
                    character,
                    dazzleId,
                    at,
                    99_001),
                0,
                dazzleDamage)],
            at,
            CancellationToken.None);
        var dazzled = registry.AdjustPveMonsterAttackerProfile(
            socket.Session,
            dazzleDamage.Monster,
            at.AddMilliseconds(1),
            MonsterCombatProfileCatalog.Resolve(
                tier: 1,
                MonsterAttackDamageKind.Physical));
        Check.True(
            fractured.PhysicalDefense == 900 &&
            fractured.MagicDefense == 900 &&
            dazzled.Hit == 108 &&
            !GameSessionRegistry.PveMonsterHealingProducerAvailable,
            "Fracture and Dazzle affect the next monster event; Wither is explicitly inert without a monster heal producer");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(character.AccountId, socket.Session);
    }

    private static MonsterDamageResult ApplyReachDirectDamage(
        GameSessionRegistry registry,
        GameCharacter character,
        MonsterRuntimeSnapshot target,
        DateTimeOffset at)
    {
        Check.True(
            registry.TryApplyMonsterDamageGuarded(
                character.CurrentMap,
                target.ObjectId,
                damage: 1,
                character.Id,
                target.SpawnGeneration,
                target.HealthRevision,
                at,
                out var result),
            "elemental reach fixture commits one guarded direct hit");
        return result;
    }

    private static ulong FindReachApplicationEventId(
        ElementKind element,
        GameCharacter character,
        uint objectId,
        DateTimeOffset at,
        ulong first)
    {
        for (var eventId = first;
             eventId < first + 100_000;
             eventId++)
        {
            var combatEvent = new DeterministicCombatEventContext(
                eventId,
                character.CurrentMap,
                character.Id,
                objectId,
                at.ToUnixTimeMilliseconds(),
                CombatEventProvenance.DirectBasicAttack,
                Committed: true,
                IsPvp: false,
                default);
            if (ElementalEffectExecutionPolicy
                    .DeterministicRollBasisPoints(combatEvent, element) <
                2_000)
            {
                return eventId;
            }
        }

        throw new InvalidOperationException(
            $"No deterministic {element} application event was found.");
    }

    private static GameplayMonsterTemplateDefinition
        ElementalMonsterTemplate(string templateKey, bool isBoss) =>
        new(
            $"test:{templateKey}",
            "test",
            SourceMapId: 0,
            "Sparta",
            templateKey,
            templateKey,
            "1",
            isBoss,
            IsElite: false,
            IsPet: false,
            AttackType: 1,
            CollisionRange: 1f);

    private static CapturedMonsterSpawn ElementalReachMonster(
        uint objectId,
        string template)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            1_000);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            1_000);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(template).CopyTo(packet.AsSpan(44));
        return new(
            MapId: 0,
            SceneKey: "Sparta",
            template,
            template,
            objectId,
            X: 0f,
            Z: 0f,
            packet);
    }
}
