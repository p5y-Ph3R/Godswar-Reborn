using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsLiveAdapterChecks
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var character = CreateLiveCharacter();
        var monsters = new[]
        {
            CreateLiveMonster(9_001, 1f),
            CreateLiveMonster(9_002, 2f),
            CreateLiveMonster(9_003, 3f),
            CreateLiveMonster(9_100, 20f)
        };
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        registry.InitializeMapMonsters(
            character.CurrentMap,
            monsters,
            Start);
        StabilizeLiveSkillFixtureCharacterId(
            registry,
            character,
            monsters);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: Start);
        await CommitLiveMonsterVisibilityAsync(
            registry,
            socket.Session,
            character);

        CheckLiveAtomicHealthRevisionGuard(
            registry,
            character,
            monsters[^1].ObjectId);
        CheckLiveDeadSourceAndBasicCooldown(
            registry,
            socket.Session,
            character,
            monsters[0].ObjectId);
        CheckLiveSkillManaAndArea(
            registry,
            socket.Session,
            character,
            monsters);
        CheckLiveCommittedProjection(
            registry,
            socket.Session,
            character,
            monsters[2].ObjectId);
        await CheckLiveAdapterLifecycleResetAsync(
            registry,
            socket.Session,
            character,
            monsters[0].ObjectId);
        CheckLegacyCombatRollback(
            socket.Session,
            character,
            monsters[0].ObjectId);
        registry.Remove(socket.Session);
        await CheckLiveBasicAttackResolutionAsync();
        await CheckReconnectSafeCombatAdmissionAsync();
    }

    private static void CheckLiveAtomicHealthRevisionGuard(
        GameSessionRegistry registry,
        GameCharacter character,
        uint objectId)
    {
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                objectId,
                out var before),
            "live guarded target exists");
        var accepted = new bool[2];
        Parallel.For(
            0,
            accepted.Length,
            index =>
            {
                accepted[index] =
                    registry.TryApplyMonsterDamageGuarded(
                        character.CurrentMap,
                        objectId,
                        damage: 1,
                        character.Id,
                        before.SpawnGeneration,
                        before.HealthRevision,
                        Start,
                        out _);
            });
        Check.Equal(
            1,
            accepted.Count(static value => value),
            "health revision guard admits one concurrent mutation");
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                objectId,
                out var after),
            "guarded target remains available");
        Check.Equal(
            before.CurrentHealth - 1,
            after.CurrentHealth,
            "rejected duplicate guard cannot apply damage");
        Check.Equal(
            before.HealthRevision + 1,
            after.HealthRevision,
            "one guarded mutation advances one health revision");
    }

    private static void CheckLiveDeadSourceAndBasicCooldown(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character,
        uint objectId)
    {
        character.CurrentHp = 0;
        var dead = registry.ResolvePlayerCombatEcs(
            session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.BasicAttack(
                Start,
                objectId,
                character.PositionX,
                character.PositionZ));
        Check.True(
            dead.RejectionReason ==
            PlayerCombatRejectionReason.SourceDead,
            "live adapter lets ECS reject a dead source");
        Check.Equal(
            0,
            dead.Hits.Length,
            "dead live source emits no applied hit");

        character.CurrentHp = character.MaxHp;
        var first = registry.ResolvePlayerCombatEcs(
            session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.BasicAttack(
                Start,
                objectId,
                character.PositionX,
                character.PositionZ));
        Check.Equal(
            1,
            first.Hits.Length,
            "live basic attack applies one guarded hit");
        Check.Equal(
            Start + PlayerCombatRules.BasicAttackCooldown,
            first.NextBasicAttackAt,
            "live ECS owns the basic-attack cooldown");

        var beforeCooldown =
            first.Hits[0].Result.AfterHealth;
        var cooldown = registry.ResolvePlayerCombatEcs(
            session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            first.NextBasicAttackAt,
            PlayerCombatEcsRequest.BasicAttack(
                Start.AddMilliseconds(1),
                objectId,
                character.PositionX,
                character.PositionZ));
        Check.True(
            cooldown.RejectionReason ==
            PlayerCombatRejectionReason.CooldownActive,
            "live ECS rejects an early basic attack");
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                objectId,
                out var unchanged),
            "cooldown target remains available");
        Check.Equal(
            beforeCooldown,
            unchanged.CurrentHealth,
            "cooldown rejection cannot mutate monster health");
    }

    private static void CheckLiveSkillManaAndArea(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character,
        IReadOnlyList<CapturedMonsterSpawn> monsters)
    {
        var normal = new SkillCombatDefinition(
            2_001,
            Target: 44,
            AffectObj: 28,
            Distance: 5f,
            Range: 0f,
            Property: 0,
            Mp: 25,
            Power1: 0m,
            Power2: 0m);
        var beforeMana = character.CurrentMp;
        var hit = registry.ResolvePlayerCombatEcs(
            session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.HostileSkill(
                PlayerCombatIntentKind.SingleTargetSkill,
                Start.AddSeconds(2),
                monsters[1].ObjectId,
                normal));
        Check.Equal(
            1,
            hit.Hits.Length,
            "live single-target skill applies its damage intent");
        Check.Equal(
            beforeMana - normal.Mp,
            character.CurrentMp,
            "live single-target reservation mirrors committed mana");

        var zeroDamage = normal with
        {
            SkillId = 2_002,
            Mp = 10,
            Power1 = -1m
        };
        var revisionBeforeRefund = character.VitalsRevision;
        var refunded = registry.ResolvePlayerCombatEcs(
            session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.HostileSkill(
                PlayerCombatIntentKind.SingleTargetSkill,
                Start.AddSeconds(3),
                monsters[1].ObjectId,
                zeroDamage));
        Check.True(
            refunded.RejectionReason ==
                PlayerCombatRejectionReason.ZeroDamage &&
            refunded.ResourcesRefunded,
            "zero-damage live skill closes with an ECS refund");
        Check.Equal(
            beforeMana - normal.Mp,
            character.CurrentMp,
            "live refund restores the reserved mana");
        Check.Equal(
            revisionBeforeRefund + 2,
            character.VitalsRevision,
            "reservation and refund each advance vitals revision");

        var insufficient = normal with
        {
            SkillId = 2_003,
            Mp = character.MaxMp + 1
        };
        var rejected = registry.ResolvePlayerCombatEcs(
            session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.HostileSkill(
                PlayerCombatIntentKind.SingleTargetSkill,
                Start.AddSeconds(4),
                monsters[1].ObjectId,
                insufficient));
        Check.True(
            rejected.RejectionReason ==
            PlayerCombatRejectionReason.InsufficientMana,
            "live ECS owns insufficient-mana rejection");
        Check.Equal(
            beforeMana - normal.Mp,
            character.CurrentMp,
            "insufficient-mana rejection cannot consume MP");

        var area = new SkillCombatDefinition(
            3_001,
            Target: 1,
            AffectObj: 8,
            Distance: 0f,
            Range: 10f,
            Property: 0,
            Mp: 30,
            Power1: 0m,
            Power2: 0m);
        var beforeAreaMana = character.CurrentMp;
        var areaDecision = registry.ResolvePlayerCombatEcs(
            session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.HostileSkill(
                PlayerCombatIntentKind.AreaSkill,
                Start.AddSeconds(5),
                uint.MaxValue,
                area));
        Check.Equal(
            3,
            areaDecision.SelectedTargetCount,
            "live area selection uses AOI and strict radius");
        Check.True(
            areaDecision.Hits
                .Select(static areaHit =>
                    areaHit.Result.ObjectId)
                .SequenceEqual(
                    monsters.Take(3)
                        .Select(static monster =>
                            monster.ObjectId)),
            "live area mutations retain object-ID order");
        Check.Equal(
            beforeAreaMana - area.Mp,
            character.CurrentMp,
            "live area cast reserves mana once");
    }

    private static void CheckLiveCommittedProjection(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character,
        uint objectId)
    {
        var lethal = new SkillCombatDefinition(
            2_010,
            Target: 44,
            AffectObj: 28,
            Distance: 5f,
            Range: 0f,
            Property: 0,
            Mp: 5,
            Power1: 0m,
            Power2: 10_000m);
        var kill = registry.ResolvePlayerCombatEcs(
            session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.HostileSkill(
                PlayerCombatIntentKind.SingleTargetSkill,
                Start.AddSeconds(6),
                objectId,
                lethal));
        Check.True(
            kill.Hits is [{ Result.Killed: true }],
            "live ECS records a guarded monster kill");

        var committed = new CharacterProgressionResult(
            ExperienceGained: 77,
            PreviousLevel: character.Level,
            CurrentLevel: character.Level,
            CurrentExperience: character.Experience + 77,
            NextLevelExperience: 999,
            LevelUps: [],
            TalentExperienceGained: 9,
            CurrentTalentExperience:
                character.TalentExperience + 9,
            TalentPointsGained: 0,
            CurrentTalentPoints: character.TalentPoints);
        var projection =
            registry.ProjectCommittedMonsterKillProgressionEcs(
                session,
                kill.Hits[0].Result,
                committed);
        Check.True(
            projection.Applied,
            "live adapter projects the persistence-committed result");
        Check.True(
            registry.GetPlayerCombatProjectionEcsDiagnostics(
                session) is { Applied: true },
            "live progression projection remains observable");
    }

    private static async Task CheckLiveAdapterLifecycleResetAsync(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character,
        uint objectId)
    {
        Check.True(
            registry.GetPlayerCombatEcsDiagnostics(session) is not null,
            "joined live player has combat ECS diagnostics");
        registry.Remove(session);
        Check.True(
            registry.GetPlayerCombatEcsDiagnostics(session) is null,
            "session removal disposes combat ECS lifecycle state");

        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: Start.AddMinutes(1));
        await CommitLiveMonsterVisibilityAsync(
            registry,
            session,
            character);
        var reset = registry.ResolvePlayerCombatEcs(
            session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.BasicAttack(
                Start.AddMinutes(1),
                objectId,
                character.PositionX,
                character.PositionZ));
        Check.Equal(
            1UL,
            reset.IntentId,
            "rejoined session owns a fresh combat intent sequence");
        Check.True(
            reset.IntentAccepted,
            "rejoined session does not retain an old cooldown");
    }

    private static void CheckLegacyCombatRollback(
        Networking.ClientSession session,
        GameCharacter character,
        uint objectId)
    {
        var legacy = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Legacy);
        Check.Throws<InvalidOperationException>(
            () => legacy.ResolvePlayerCombatEcs(
                session,
                character,
                WorldObjectIds.ForPlayer(character.Id),
                DateTimeOffset.MinValue,
                PlayerCombatEcsRequest.BasicAttack(
                    Start,
                    objectId,
                    character.PositionX,
                    character.PositionZ)),
            "legacy rollback cannot enter live combat ECS");
    }

    private static async Task CommitLiveMonsterVisibilityAsync(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character)
    {
        await using var transition =
            await registry.BeginMonsterVisibilityTransitionAsync(
                session,
                character.CurrentMap,
                character.PositionX,
                character.PositionZ,
                CancellationToken.None)
            ?? throw new InvalidOperationException(
                "Live combat visibility transition was unavailable.");
        transition.Commit();
    }

    private static GameCharacter CreateLiveCharacter() =>
        new()
        {
            Id = 8_731,
            AccountId = 717,
            Name = "LiveCombatEcsHero",
            CreatedUtc = Start.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = 0,
            PositionX = 0f,
            PositionZ = 0f,
            Profession = 0,
            Level = 10,
            CurrentHp = 500,
            MaxHp = 500,
            CurrentMp = 200,
            MaxMp = 200,
            CalculatedStats = new CharacterStats
            {
                PhysicalAttack = 20,
                MagicAttack = 30
            }
        };

    private static CapturedMonsterSpawn CreateLiveMonster(
        uint objectId,
        float x)
    {
        const string templateKey = "A_normal_stub_001";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            237);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            237);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28, 4),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32, 4),
            2f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36, 4),
            0f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            MapId: 0,
            SceneKey: "Sparta",
            templateKey,
            templateKey,
            objectId,
            x,
            Z: 0f,
            packet);
    }
}
