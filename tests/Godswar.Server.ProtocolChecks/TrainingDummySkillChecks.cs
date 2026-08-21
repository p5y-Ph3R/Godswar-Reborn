using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    public const string CheckName =
        "Authoritative development-only training-dummy damage skills";

    public static async Task RunAsync()
    {
        HostileStatusProcChecks.Run();
        CheckDamageAdapterSource();
        CheckFrozenPolicy();
        await CheckWarriorDamageSkillsAsync();
        await CheckZodiacOffensiveRuntimeAsync();
        await CheckZodiacDefensiveRuntimeAsync();
        await CheckChampionScalarSetAsync();
        await CheckChampionAreaRuntimeAsync();
        await CheckInternalInjuryRuntimeAsync();
        await CheckStatusOnlyCastTransactionAsync();
        await CheckChampionAreaBoundariesAsync();
        await CheckRuntimeBoundaryAsync();
        await CheckManaAndCooldownAsync();
        await CheckDeterministicReplayAsync();
        await CheckPassiveCounterDamageAsync();
        await CheckElementalBurnStatusEgressAsync();
        await CheckDeathAndPublicationAsync();
    }

    private static void CheckFrozenPolicy()
    {
        var exact = SpearHit();
        Check.True(
            TrainingDummyDamageSkillPolicy.IsSupportedScalar(
                GameplayContentTestFixtures.Runtime,
                exact,
                attackerProfession: 1),
            "the published Spear Hit 5 scalar definition is admitted");
        Check.True(
            ChampionDamageSkills.All(skill => skill.Range == 0f
                ? TrainingDummyDamageSkillPolicy.IsSupportedScalar(
                    GameplayContentTestFixtures.Runtime,
                    skill,
                    attackerProfession: 1)
                : TrainingDummyDamageSkillPolicy.IsSupportedArea(
                    GameplayContentTestFixtures.Runtime,
                    skill,
                    attackerProfession: 1)) &&
            ChampionDamageSkills.Count(static skill =>
                skill.Range == 0f) ==
                5 &&
            ChampionDamageSkills.Count(static skill =>
                skill.Range > 0f) == 12,
            "all five scalar and twelve self-area Champion definitions remain admitted");
        Check.True(
            ChampionDamageSkills.All(expected =>
                GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                    expected.SkillId,
                    out var published) &&
                published == expected),
            "all seventeen sealed definitions exactly match the published runtime catalog");
        Check.True(
            IsUnsupportedChampionScalar(exact with { SkillId = 293 }) &&
            IsUnsupportedChampionScalar(exact with { Range = 1f }) &&
            IsUnsupportedChampionScalar(exact with { AffectObj = 12 }) &&
            IsUnsupportedChampionScalar(exact with { Mp = 599 }) &&
            IsUnsupportedChampionScalar(exact with { Power1 = 2.65m }) &&
            IsUnsupportedChampionScalar(exact with
                { CastTime = TimeSpan.FromMilliseconds(1) }) &&
            IsUnsupportedChampionScalar(exact with
                { Cooldown = TimeSpan.FromSeconds(24) }),
            "skill identity, single-target shape, formula, MP, cast, and cooldown are sealed");
        var sacredZeal = new SkillCombatDefinition(
            344, 1, 1, 0f, 0f, 0, 300, -1m, 0m,
            TimeSpan.Zero, TimeSpan.FromSeconds(10));
        var freeze = new SkillCombatDefinition(
            354, 44, 28, 9f, 0f, 0, 250, -1m, 0m,
            TimeSpan.Zero, TimeSpan.FromSeconds(40));
        Check.True(
            TrainingDummyDamageSkillPolicy.ValidateArea(
                GameplayContentTestFixtures.Runtime,
                sacredZeal,
                attackerProfession: 1) ==
                TrainingDummySkillRejectionReason.UnsupportedSkill &&
            TrainingDummyDamageSkillPolicy.ValidateScalar(
                GameplayContentTestFixtures.Runtime,
                freeze,
                attackerProfession: 1) ==
                TrainingDummySkillRejectionReason.UnsupportedSkill &&
            ChampionDamageSkills.All(skill =>
                skill.Range == 0f
                    ? IsUnsupportedChampionScalar(
                        skill with { Mp = skill.Mp + 1 })
                    : TrainingDummyDamageSkillPolicy.ValidateArea(
                        GameplayContentTestFixtures.Runtime,
                        skill with { Mp = skill.Mp + 1 },
                        attackerProfession: 1) ==
                      TrainingDummySkillRejectionReason.UnsupportedSkill),
            "buff 344, control 354, and any published-definition drift stay excluded");
        Check.True(
            SkillCombatResolver.MustRejectHostilePlayerTarget(true) &&
            !SkillCombatResolver.HostilePlayerSkillWireSupported,
            "the authored adapter does not open native hostile-player skill wire support");
    }

    private static bool IsUnsupportedChampionScalar(
        in SkillCombatDefinition skill) =>
        TrainingDummyDamageSkillPolicy.ValidateScalar(
            GameplayContentTestFixtures.Runtime,
            skill,
            attackerProfession: 1) ==
        TrainingDummySkillRejectionReason.UnsupportedSkill;

    private static async Task CheckRuntimeBoundaryAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-16T00:00:00Z");
        await using (var exact = await Fixture.CreateAsync())
        {
            var accepted = await exact.ResolveAsync(1, now);
            Check.True(
                accepted.Accepted &&
                accepted.Combat.Eligibility.EntitlementKind ==
                    PvpEntitlementKind.TrainingDummy,
                "ordinary player to exact configured dummy is admitted");
        }

        await using (var ordinary = await Fixture.CreateAsync(
            target: Player(8002, 8002, "Ordinary", 0, 1)))
        {
            var denied = await ordinary.ResolveAsync(1, now);
            Check.True(
                denied.RejectionReason ==
                    TrainingDummySkillRejectionReason.
                        TargetIsNotExactTrainingDummy,
                "ordinary capital target remains protected");
            Check.Equal(
                ordinary.Target.MaxHp,
                ordinary.Target.CurrentHp,
                "ordinary target receives no authored damage");
        }

        await using (var reverse = await Fixture.CreateAsync(
            attacker: Dummy(),
            target: Player(8003, 8003, "Ordinary", 0, 1)))
        {
            var denied = await reverse.ResolveAsync(1, now);
            Check.True(
                denied.RejectionReason ==
                    TrainingDummySkillRejectionReason.AttackerIsTrainingDummy,
                "configured dummy can never become the attacker");
        }

        var warrior = Player(
            8004,
            8004,
            "WrongSkillClass",
            0,
            0,
            profession: 0);
        await using (var wrongClass = await Fixture.CreateAsync(
            attacker: warrior))
        {
            var denied = await wrongClass.ResolveAsync(1, now);
            Check.True(
                denied.RejectionReason ==
                    TrainingDummySkillRejectionReason.
                        AttackerProfessionMismatch,
                "a Warrior cannot forge a Champion-only skill definition");
        }

        await using (var moved = await Fixture.CreateAsync(
            target: Dummy(positionX: 149f)))
        {
            var denied = await moved.ResolveAsync(1, now);
            Check.True(
                denied.RejectionReason ==
                    TrainingDummySkillRejectionReason.
                        TargetIsNotExactTrainingDummy,
                "a moved identity tuple loses training admission immediately");
        }

        await using (var unsupported = await Fixture.CreateAsync())
        {
            var area = await unsupported.ResolveAsync(
                1,
                now,
                SpearHit() with { Range = 2f });
            var forgedCaster = await unsupported.ResolveAsync(
                1,
                now,
                casterObjectId: 0xDEADBEEF);
            Check.True(
                area.RejectionReason ==
                    TrainingDummySkillRejectionReason.UnsupportedSkill &&
                forgedCaster.RejectionReason ==
                    TrainingDummySkillRejectionReason.InvalidCasterObject &&
                unsupported.Attacker.CurrentMp ==
                    unsupported.Attacker.MaxMp,
                "area variants and forged caster identities mutate no resources");
        }
    }

    private static async Task CheckManaAndCooldownAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-16T01:00:00Z");
        var attacker = Player(8101, 8101, "ManaTester", 0, 0);
        attacker.CurrentMp = 599;
        await using var fixture = await Fixture.CreateAsync(
            attacker: attacker);
        var targetHp = fixture.Target.CurrentHp;
        var revisionCalls = 0;
        var insufficient = await fixture.Registry
            .ResolveTrainingDummyDamageScalarAsync(
                fixture.AttackerSocket.Session,
                LocalPlayerObjectId,
                fixture.TargetObjectId,
                TrainingSkillCastPacket(
                    checked((uint)SpearHit().SkillId),
                    fixture.TargetObjectId),
                SpearHit(),
                () => ++revisionCalls,
                now,
                CancellationToken.None);
        Check.True(
            insufficient.RejectionReason ==
                TrainingDummySkillRejectionReason.InsufficientMana &&
            fixture.Attacker.CurrentMp == 599 &&
            fixture.Target.CurrentHp == targetHp &&
            revisionCalls == 0,
            "insufficient MP spends neither mana, cooldown, revision, nor target HP");

        fixture.Attacker.CurrentMp = 1_200;
        var first = await fixture.ResolveAsync(1, now);
        var hpAfterFirst = fixture.Target.CurrentHp;
        var secondRevisionCalls = 0;
        var second = await fixture.Registry
            .ResolveTrainingDummyDamageScalarAsync(
            fixture.AttackerSocket.Session,
            LocalPlayerObjectId,
            fixture.TargetObjectId,
            TrainingSkillCastPacket(
                checked((uint)SpearHit().SkillId),
                fixture.TargetObjectId),
            SpearHit(),
            () => ++secondRevisionCalls,
            now.AddSeconds(1),
            CancellationToken.None);
        Check.True(
            first.Accepted &&
            fixture.Attacker.CurrentMp == 600 &&
            second.RejectionReason ==
                TrainingDummySkillRejectionReason.CooldownActive &&
            second.CooldownReadyAt == now.AddSeconds(25) &&
            fixture.Target.CurrentHp == hpAfterFirst &&
            secondRevisionCalls == 0,
            "accepted attempt costs 600 MP and a replay inside 25 seconds is inert");

        var third = await fixture.ResolveAsync(2, now.AddSeconds(25));
        Check.True(
            third.Accepted && fixture.Attacker.CurrentMp == 0,
            "the exact 25-second boundary admits the next 600-MP attempt");
    }

    private static async Task CheckDeterministicReplayAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-16T02:00:00Z");
        await using var firstFixture = await Fixture.CreateAsync();
        await using var replayFixture = await Fixture.CreateAsync();
        var first = await firstFixture.ResolveAsync(77, now);
        var replay = await replayFixture.ResolveAsync(77, now);
        Check.True(
            first.Accepted && replay.Accepted &&
            first.Combat.Resolution.EventId == replay.Combat.Resolution.EventId &&
            first.Combat.Resolution.Outcome == replay.Combat.Resolution.Outcome &&
            first.Combat.Resolution.Damage == replay.Combat.Resolution.Damage &&
            first.Combat.AppliedDamage == replay.Combat.AppliedDamage,
            "identical server identities and revisions replay the same V2 outcome");
        Check.True(
            first.Combat.Resolution.FormulaVersion == AuthoredCombatV2.Version,
            "training Spear Hit resolves through the current PvP V2 formula");
    }

    private static async Task CheckDeathAndPublicationAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-16T03:00:00Z");
        var attacker = Player(8201, 8201, "DeathTester", 0, 0);
        attacker.CalculatedStats = new Godswar.Server.State.CharacterStats
        {
            CharacterId = attacker.Id,
            AccountId = attacker.AccountId,
            Name = attacker.Name,
            Profession = attacker.Profession,
            Level = attacker.Level,
            CurrentHp = attacker.CurrentHp,
            MaxHp = attacker.MaxHp,
            CurrentMp = attacker.CurrentMp,
            MaxMp = attacker.MaxMp,
            PhysicalAttack = 100_000,
            Hit = 100_000,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        var target = Dummy();
        target.CurrentHp = 100;
        target.MaxHp = 100;
        target.CalculatedStats = new Godswar.Server.State.CharacterStats
        {
            CharacterId = target.Id,
            AccountId = target.AccountId,
            Name = target.Name,
            Profession = target.Profession,
            Level = target.Level,
            CurrentHp = 100,
            MaxHp = 100,
            CurrentMp = target.CurrentMp,
            MaxMp = target.MaxMp,
            PhysicalDefense = 0,
            Dodge = 0,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        var revision = FindHittingRevision(attacker, target);
        await using var fixture = await Fixture.CreateAsync(attacker, target);
        var reset = await fixture.ResolveAsync(revision, now);
        Check.True(
            reset.Accepted &&
            reset.Combat.Resolution.CapturedDamageValue > 100 &&
            reset.Combat.AppliedDamage == 100 &&
            reset.Combat.TargetKilled &&
            reset.Combat.KilledPlayers.Any(value =>
                value.CharacterId == fixture.Target.Id) &&
            fixture.Target.CurrentHp == 0 &&
            fixture.Attacker.CurrentMp ==
                fixture.Attacker.MaxMp -
                SpearHit().Mp,
            "lethal scalar damage commits target death while charging the skill MP");

        var visual = await fixture.AttackerSocket.ReadPacketAsync(40);
        var damage = await fixture.AttackerSocket.ReadPacketAsync(30);
        var impact = await fixture.AttackerSocket.ReadPacketAsync(24);
        var firstVitals = await fixture.AttackerSocket.ReadPacketAsync(16);
        var secondVitals = await fixture.AttackerSocket.ReadPacketAsync(16);
        var death = await fixture.AttackerSocket.ReadPacketAsync(28);
        var opcodes = new[]
            { visual, damage, impact, firstVitals, secondVitals, death }
            .Select(static packet =>
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2)))
            .ToArray();
        Check.True(
            opcodes.SequenceEqual(new ushort[]
            {
                0x2738,
                0x272A,
                0x273E,
                0x2771,
                0x2771,
                0x2722
            }),
            "scalar adapter publishes cast, damage, impact, committed vitals, and death in order");
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(visual.AsSpan(4, 4)) ==
                LocalPlayerObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(visual.AsSpan(8, 4)) ==
                checked((uint)SpearHit().SkillId) &&
            BinaryPrimitives.ReadUInt32LittleEndian(visual.AsSpan(16, 4)) ==
                fixture.TargetObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(4, 4)) ==
                LocalPlayerObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(8, 4)) ==
                fixture.TargetObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(damage.AsSpan(24, 4)) ==
                reset.Combat.Resolution.CapturedDamageValue &&
            BinaryPrimitives.ReadUInt32LittleEndian(firstVitals.AsSpan(4, 4)) ==
                fixture.TargetObjectId &&
            BinaryPrimitives.ReadInt32LittleEndian(firstVitals.AsSpan(8, 4)) ==
                0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(death.AsSpan(4, 4)) ==
                fixture.TargetObjectId &&
            fixture.AttackerSocket.Available == 0,
            "the lethal scalar wire retains captured overkill damage and terminal target state");
    }

    private static long FindHittingRevision(
        Godswar.Server.State.GameCharacter attacker,
        Godswar.Server.State.GameCharacter target,
        SkillCombatDefinition? definition = null)
    {
        var source = CombatCharacterStatsAdapter.FromCharacter(attacker);
        var targetStats = CombatCharacterStatsAdapter.ToTarget(
            target.Level,
            target.CalculatedStats!);
        var skill = TrainingDummyDamageSkillPolicy.Snapshot(
            definition ?? SpearHit());
        for (var revision = 1L; revision <= 100; revision++)
        {
            var eventId = CombatEventIdentity.ForPlayerSkill(
                attacker.Id,
                target.Id,
                attacker.VitalsRevision,
                target.VitalsRevision,
                revision,
                skill.SkillId);
            if (PlayerCombatRules.ResolvePvpSkillDamage(
                    source,
                    targetStats,
                    skill,
                    eventId).Hit)
            {
                return revision;
            }
        }
        throw new InvalidOperationException(
            "Expected a deterministic hit within 100 revisions.");
    }
}
