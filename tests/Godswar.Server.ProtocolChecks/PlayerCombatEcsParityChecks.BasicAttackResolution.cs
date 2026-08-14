using System.Buffers.Binary;
using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsParityChecks
{
    private const uint ResolutionTargetObjectId = 7_701;
    private const uint ResolutionSpawnGeneration = 3;
    private const ulong ResolutionHealthRevision = 9;

    private static void CheckEcsBasicAttackResolution()
    {
        var normalRevision = FindAdmittedRevision(
            CombatHitOutcome.Normal);
        var criticalRevision = FindAdmittedRevision(
            CombatHitOutcome.Critical);
        var missRevision = FindAdmittedRevision(CombatHitOutcome.Miss);

        var normal = RunResolvedBasicAttack(
            normalRevision,
            intentId: 501);
        var critical = RunResolvedBasicAttack(
            criticalRevision,
            intentId: 502);
        var miss = RunResolvedBasicAttack(
            missRevision,
            intentId: 503);
        var replay = RunResolvedBasicAttack(
            missRevision,
            intentId: 9_999);

        Check.True(normal.Resolution.Outcome == CombatHitOutcome.Normal,
            "ECS basic attack propagates a normal result");
        Check.True(critical.Resolution.Outcome == CombatHitOutcome.Critical,
            "ECS basic attack propagates a critical result");
        Check.True(miss.Resolution.Outcome == CombatHitOutcome.Miss,
            "ECS basic attack propagates a miss result");
        Check.Equal(miss.Resolution, replay.Resolution,
            "replaying one authoritative state reproduces exact roll evidence");
        Check.Equal(missRevision, miss.CombatRevision,
            "an admitted miss advances the server combat revision");
        Check.Equal(0, miss.DamageIntentCount,
            "a miss emits no monster health-mutation intent");
        Check.True(!miss.ReservationOpen,
            "a miss closes its cooldown reservation without mutation");
        Check.Equal(1, normal.DamageIntentCount,
            "a normal hit emits one guarded mutation intent");
        Check.Equal(1, critical.DamageIntentCount,
            "a critical hit emits one guarded mutation intent");
        Check.True(normal.ReservationOpen && critical.ReservationOpen,
            "health-changing outcomes await guarded mutation commits");

        AssertBasicAttackPacketOutcome(normal.Resolution);
        AssertBasicAttackPacketOutcome(critical.Resolution);
        AssertBasicAttackPacketOutcome(miss.Resolution);

        var next = RunResolvedBasicAttack(
            checked(missRevision + 1UL),
            intentId: 503);
        Check.True(next.Resolution.EventId != miss.Resolution.EventId,
            "the next admitted attempt owns a fresh deterministic event");
    }

    private static ResolvedBasicAttack RunResolvedBasicAttack(
        ulong admittedRevision,
        ulong intentId)
    {
        var fixture = CreateFixture();
        var offense = CreateResolutionOffense();
        fixture.World.Set(fixture.Player, offense);
        ref var resources = ref fixture.World
            .Get<PlayerCombatResourceComponent>(fixture.Player);
        resources.CombatRevision = admittedRevision - 1UL;

        var target = CreateResolutionTarget();
        PlayerCombatEcsBoundary.HydrateTarget(fixture.World, target);
        PlayerCombatEcsBoundary.QueueIntent(
            fixture.World,
            fixture.Player,
            new PlayerCombatIntentComponent(
                intentId,
                PlayerCombatIntentKind.BasicAttack,
                Start,
                target.ObjectId,
                target.SpawnGeneration,
                target.HealthRevision,
                ReportedAttackerX: 0f,
                ReportedAttackerZ: 0f,
                HasReportedTargetPosition: false,
                ReportedTargetX: float.NaN,
                ReportedTargetZ: float.NaN,
                Skill: default));
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var resolved = Events<PlayerCombatTargetResolvedEvent>(fixture)
            .Single();
        var eventId = CombatEventIdentity.ForPlayerMonsterBasicAttack(
            attackerCharacterId: 7,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            admittedRevision);
        var expected = PlayerCombatRules.ResolveBasicAttack(
            CombatCharacterStatsAdapter.FromOffense(offense),
            ToTargetStats(target),
            eventId);
        Check.Equal(expected, resolved.Resolution,
            "ECS resolution matches the shared authored resolver");

        return new ResolvedBasicAttack(
            resolved.Resolution,
            Events<PlayerCombatDamageIntentEvent>(fixture).Length,
            fixture.World.Has<PlayerCombatReservationComponent>(
                fixture.Player),
            fixture.World.Get<PlayerCombatResourceComponent>(fixture.Player)
                .CombatRevision);
    }

    private static ulong FindAdmittedRevision(CombatHitOutcome outcome)
    {
        var offense = CreateResolutionOffense();
        var target = CreateResolutionTarget();
        var attacker = CombatCharacterStatsAdapter.FromOffense(offense);
        var targetStats = ToTargetStats(target);
        for (ulong revision = 1; revision <= 10_000; revision++)
        {
            var eventId = CombatEventIdentity.ForPlayerMonsterBasicAttack(
                attackerCharacterId: 7,
                target.ObjectId,
                target.SpawnGeneration,
                target.HealthRevision,
                revision);
            if (PlayerCombatRules.ResolveBasicAttack(
                    attacker,
                    targetStats,
                    eventId).Outcome == outcome)
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            $"No deterministic {outcome} basic-attack fixture was found.");
    }

    private static void AssertBasicAttackPacketOutcome(
        in CombatResolution resolution)
    {
        var packet = GameClientHandler.BuildResolvedBasicAttackPacket(
            attackerObjectId: 0x1448,
            targetObjectId: ResolutionTargetObjectId,
            attackSelector: 3,
            resolution);
        Check.Equal(resolution.CapturedDamageValue,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24, 4)),
            $"{resolution.Outcome} packet carries captured damage encoding");
        Check.Equal((byte)resolution.Outcome, packet[29],
            $"{resolution.Outcome} packet carries its outcome byte");
        if (!resolution.Hit)
        {
            Check.Equal(uint.MaxValue,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.AsSpan(24, 4)),
                "miss packet carries the captured sentinel");
        }
    }

    private static PlayerCombatOffenseComponent CreateResolutionOffense() =>
        new(
            Profession: 0,
            PhysicalAttack: 900,
            MagicAttack: 1_100,
            PhysicalDamageBonus: 1_500,
            MagicDamageBonus: 500,
            PhysicalAppendDamage: 25,
            MagicAppendDamage: 30)
        {
            Level = 80,
            Hit = 300,
            Critical = 2_000,
            IgnorePhysicalDefenseBasisPoints = 2_000,
            CriticalDamageBasisPoints = 1_500,
            CriticalDamageFlat = 40
        };

    private static PlayerCombatTargetComponent CreateResolutionTarget() =>
        new(
            ResolutionTargetObjectId,
            MapId: 2,
            X: 1f,
            Z: 0f,
            CurrentHealth: 5_000,
            IsSpawned: true,
            IsAlive: true,
            IsVisible: true,
            ResolutionSpawnGeneration,
            ResolutionHealthRevision,
            PlayerCombatRules.DefaultBasicAttackRange)
        {
            Level = 85,
            PhysicalDefense = 240,
            MagicDefense = 300,
            Dodge = 500,
            CriticalResistance = 100,
            PhysicalDamageReductionBasisPoints = 500,
            PhysicalFlatAbsorption = 10
        };

    private static CombatTargetStats ToTargetStats(
        in PlayerCombatTargetComponent target) =>
        new()
        {
            Level = target.Level,
            PhysicalDefense = target.PhysicalDefense,
            MagicDefense = target.MagicDefense,
            Dodge = target.Dodge,
            CriticalResistance = target.CriticalResistance,
            PhysicalDamageReductionBasisPoints =
                target.PhysicalDamageReductionBasisPoints,
            MagicDamageReductionBasisPoints =
                target.MagicDamageReductionBasisPoints,
            CriticalDamageReductionBasisPoints =
                target.CriticalDamageReductionBasisPoints,
            PhysicalFlatAbsorption = target.PhysicalFlatAbsorption,
            MagicFlatAbsorption = target.MagicFlatAbsorption,
            CriticalDamageFlatReduction =
                target.CriticalDamageFlatReduction,
            DamageReboundBasisPoints = target.DamageReboundBasisPoints,
            DamageReboundFlat = target.DamageReboundFlat
        };

    private readonly record struct ResolvedBasicAttack(
        CombatResolution Resolution,
        int DamageIntentCount,
        bool ReservationOpen,
        ulong CombatRevision);
}
