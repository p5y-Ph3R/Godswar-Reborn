using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private static readonly SkillCombatDefinition[] ChampionDamageSkills =
    [
        new(254, 44, 28, 3f, 0f, 0, 90, 0.05m, 550m,
            TimeSpan.Zero, TimeSpan.FromSeconds(6)),
        new(264, 44, 28, 3f, 0f, 0, 162, 0.44m, 880m,
            TimeSpan.Zero, TimeSpan.FromSeconds(6)),
        new(274, 44, 28, 3f, 0f, 0, 240, 1.32m, 1320m,
            TimeSpan.Zero, TimeSpan.FromSeconds(6)),
        new(284, 44, 28, 3f, 0f, 0, 360, 1.32m, 1760m,
            TimeSpan.Zero, TimeSpan.FromSeconds(25)),
        new(294, 44, 28, 3f, 0f, 0, 600, 2.64m, 3520m,
            TimeSpan.Zero, TimeSpan.FromSeconds(25)),
        new(304, 1, 28, 0f, 10f, 0, 144, -0.09m, 660m,
            TimeSpan.Zero, TimeSpan.FromSeconds(18)),
        new(314, 1, 28, 0f, 10f, 0, 264, 0.33m, 990m,
            TimeSpan.Zero, TimeSpan.FromSeconds(18)),
        new(320, 1, 28, 0f, 10f, 0, 60, -0.3m, 200m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36)),
        new(321, 1, 28, 0f, 10f, 0, 100, -0.2m, 400m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36)),
        new(322, 1, 28, 0f, 10f, 0, 200, 0m, 800m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36)),
        new(323, 1, 28, 0f, 10f, 0, 350, 0.4m, 1500m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36)),
        new(324, 1, 28, 0f, 10f, 0, 420, 0.44m, 1650m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36)),
        new(330, 1, 28, 0f, 10f, 0, 180, 0.2m, 300m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36)),
        new(331, 1, 28, 0f, 10f, 0, 270, 0.3m, 600m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36)),
        new(332, 1, 28, 0f, 10f, 0, 450, 0.5m, 1200m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36)),
        new(333, 1, 28, 0f, 10f, 0, 750, 0.8m, 1800m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36)),
        new(334, 1, 28, 0f, 10f, 0, 900, 0.88m, 1980m,
            TimeSpan.Zero, TimeSpan.FromSeconds(36))
    ];

    private static SkillCombatDefinition AreaSkill(int skillId = 304) =>
        ChampionDamageSkills.Single(skill => skill.SkillId == skillId);

    private static async Task CheckChampionScalarSetAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-16T03:30:00Z");
        foreach (var skill in ChampionDamageSkills.Where(static skill =>
                     skill.Range == 0f))
        {
            await using var fixture = await Fixture.CreateAsync();
            var initialMana = fixture.Attacker.CurrentMp;
            var first = await fixture.ResolveAsync(
                skill.SkillId,
                now,
                skill);
            var replayCalls = 0;
            var replay = await fixture.Registry
                .ResolveTrainingDummyDamageScalarAsync(
                    fixture.AttackerSocket.Session,
                    LocalPlayerObjectId,
                    fixture.TargetObjectId,
                    TrainingSkillCastPacket(
                        checked((uint)skill.SkillId),
                        fixture.TargetObjectId),
                    skill,
                    () => ++replayCalls,
                    now.AddMilliseconds(
                        skill.Cooldown.TotalMilliseconds - 1),
                    CancellationToken.None);
            Check.True(
                first.Accepted &&
                fixture.Attacker.CurrentMp == initialMana - skill.Mp &&
                replay.RejectionReason ==
                    TrainingDummySkillRejectionReason.CooldownActive &&
                replay.CooldownReadyAt == now + skill.Cooldown &&
                replayCalls == 0,
                $"scalar Champion skill {skill.SkillId} uses its sealed MP and cooldown once");
        }
    }

    private static async Task CheckChampionAreaRuntimeAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-16T04:00:00Z");
        await using var fixture = await AreaFixture.CreateAsync();
        var initialMana = fixture.Attacker.CurrentMp;
        var ordinaryHp = fixture.Ordinary.CurrentHp;
        var revisionCalls = 0;
        var decision = await fixture.ResolveAsync(
            AreaSkill(),
            () =>
            {
                revisionCalls++;
                return 77;
            },
            now);

        Check.True(
            decision.Accepted &&
            decision.Combats.Count == 2 &&
            revisionCalls == 1 &&
            fixture.Attacker.CurrentMp == initialMana - AreaSkill().Mp,
            "one area action admits two dummies with one revision and one MP charge");
        Check.True(
            decision.Combats
                .Select(static combat => combat.Target!.ObjectId)
                .SequenceEqual(fixture.DummyObjectIds.Order()),
            "area targets are exact training identities sorted by world object ID");
        Check.True(
            decision.Combats.All(static combat =>
                combat.Resolution.FormulaVersion == AuthoredCombatV2.Version) &&
            decision.Combats.Select(static combat =>
                combat.Resolution.EventId).Distinct().Count() == 2,
            "each area target gets a distinct deterministic PvP V2 resolution");
        Check.True(
            decision.Combats[0].ChangedVitals.All(context =>
                context.CharacterId != fixture.Attacker.Id) &&
            decision.Combats[^1].ChangedVitals.Any(context =>
                context.CharacterId == fixture.Attacker.Id) &&
            decision.Combats.Take(decision.Combats.Count - 1)
                .All(static combat => combat.KilledPlayers.Count == 0),
            "attacker vitals and aggregate deaths publish only with the final sorted target");
        Check.Equal(
            ordinaryHp,
            fixture.Ordinary.CurrentHp,
            "an ordinary opposing player inside the radius is never selected");

        var hpAfterFirst = fixture.Dummies
            .Select(static dummy => dummy.CurrentHp)
            .ToArray();
        var replayRevisionCalls = 0;
        var replay = await fixture.ResolveAsync(
            AreaSkill(),
            () => ++replayRevisionCalls,
            now.AddSeconds(1));
        Check.True(
            replay.RejectionReason ==
                TrainingDummySkillRejectionReason.CooldownActive &&
            replayRevisionCalls == 0 &&
            fixture.Attacker.CurrentMp == initialMana - AreaSkill().Mp &&
            fixture.Dummies.Select(static dummy => dummy.CurrentHp)
                .SequenceEqual(hpAfterFirst),
            "area cooldown replay spends no revision, MP, or target health");

        var next = await fixture.ResolveAsync(
            AreaSkill(),
            () => 78,
            now.AddSeconds(18));
        Check.True(
            next.Accepted &&
            fixture.Attacker.CurrentMp == initialMana - (2 * AreaSkill().Mp),
            "the exact per-skill cooldown boundary admits one new area action");
    }

    private static async Task CheckChampionAreaBoundariesAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-16T05:00:00Z");
        await using (var strict = await AreaFixture.CreateAsync(
            attackerZ: -152f))
        {
            var secondHp = strict.Dummies[1].CurrentHp;
            var decision = await strict.ResolveAsync(
                AreaSkill(),
                () => 91,
                now);
            Check.True(
                decision.Accepted &&
                decision.Combats.Count == 1 &&
                decision.Combats[0].Target?.CharacterId == 7001 &&
                strict.Dummies[1].CurrentHp == secondHp,
                "strict Range10 excludes a dummy exactly ten units from the authoritative caster center");
        }

        await using (var none = await AreaFixture.CreateAsync(
            attackerX: 100f,
            attackerZ: 100f))
        {
            var initialMana = none.Attacker.CurrentMp;
            var revisionCalls = 0;
            var decision = await none.ResolveAsync(
                AreaSkill(),
                () => ++revisionCalls,
                now);
            Check.True(
                !decision.Handled &&
                revisionCalls == 0 &&
                none.Attacker.CurrentMp == initialMana,
                "no dummy in range returns control to the unchanged monster PvE path");
        }

        await using (var insufficient = await AreaFixture.CreateAsync())
        {
            insufficient.Attacker.CurrentMp = AreaSkill().Mp - 1;
            var hp = insufficient.Dummies
                .Select(static dummy => dummy.CurrentHp)
                .ToArray();
            var revisionCalls = 0;
            var decision = await insufficient.ResolveAsync(
                AreaSkill(),
                () => ++revisionCalls,
                now);
            Check.True(
                decision.RejectionReason ==
                    TrainingDummySkillRejectionReason.InsufficientMana &&
                revisionCalls == 0 &&
                insufficient.Attacker.CurrentMp == AreaSkill().Mp - 1 &&
                insufficient.Dummies.Select(static dummy => dummy.CurrentHp)
                    .SequenceEqual(hp),
                "area prevalidation failure occurs before resource or target commit");
        }
    }

    private sealed class AreaFixture : IAsyncDisposable
    {
        private readonly List<RuntimePolicySessionSocket> _sockets;

        private AreaFixture(
            List<RuntimePolicySessionSocket> sockets,
            GameSessionRegistry registry,
            GameCharacter attacker,
            IReadOnlyList<GameCharacter> dummies,
            GameCharacter ordinary)
        {
            _sockets = sockets;
            Registry = registry;
            AttackerSocket = sockets[0];
            Attacker = attacker;
            Dummies = dummies;
            Ordinary = ordinary;
        }

        public RuntimePolicySessionSocket AttackerSocket { get; }
        public RuntimePolicySessionSocket FirstDummySocket => _sockets[2];
        public RuntimePolicySessionSocket ObserverSocket => _sockets[3];
        public GameSessionRegistry Registry { get; }
        public GameCharacter Attacker { get; }
        public IReadOnlyList<GameCharacter> Dummies { get; }
        public GameCharacter Ordinary { get; }
        public uint AttackerObjectId =>
            Registry.GetRequiredPlayerObjectId(AttackerSocket.Session);
        public IReadOnlyList<uint> DummyObjectIds =>
        [
            Registry.GetRequiredPlayerObjectId(_sockets[2].Session),
            Registry.GetRequiredPlayerObjectId(_sockets[1].Session)
        ];

        public static async Task<AreaFixture> CreateAsync(
            float attackerX = 148f,
            float attackerZ = -158f,
            bool bindElementalOwnership = false,
            byte attackerProfession = 1)
        {
            var sockets = new List<RuntimePolicySessionSocket>();
            try
            {
                for (var index = 0; index < 4; index++)
                {
                    sockets.Add(await RuntimePolicySessionSocket.CreateAsync());
                }

                var registry = Registry();
                var attacker = Player(
                    8501,
                    8501,
                    "AreaTester",
                    0,
                    0,
                    attackerProfession);
                attacker.PositionX = attackerX;
                attacker.PositionZ = attackerZ;
                var first = Dummy();
                var second = Player(7002, 7002, "AresMirage", 0, 1);
                second.PositionX = 148f;
                second.PositionZ = -162f;
                var ordinary = Player(8502, 8502, "Ordinary", 0, 1);
                ordinary.PositionX = attackerX;
                ordinary.PositionZ = attackerZ;

                if (bindElementalOwnership)
                {
                    BindElementalOwnership(
                        registry,
                        sockets[0].Session,
                        attacker);
                    BindElementalOwnership(
                        registry,
                        sockets[1].Session,
                        second);
                    BindElementalOwnership(
                        registry,
                        sockets[2].Session,
                        first);
                    BindElementalOwnership(
                        registry,
                        sockets[3].Session,
                        ordinary);
                }

                Join(registry, sockets[0], attacker);
                // Reverse join order proves sorting is identity-based.
                Join(registry, sockets[1], second);
                Join(registry, sockets[2], first);
                Join(registry, sockets[3], ordinary);
                return new AreaFixture(
                    sockets,
                    registry,
                    attacker,
                    [first, second],
                    ordinary);
            }
            catch
            {
                foreach (var socket in sockets)
                {
                    await socket.DisposeAsync();
                }
                throw;
            }
        }

        public Task<TrainingDummyAreaSkillDecision> ResolveAsync(
            SkillCombatDefinition skill,
            Func<long> revision,
            DateTimeOffset now) =>
            Registry.ResolveTrainingDummyDamageAreaAsync(
                AttackerSocket.Session,
                LocalPlayerObjectId,
                TrainingSkillCastPacket(
                    checked((uint)skill.SkillId),
                    LocalPlayerObjectId),
                skill,
                revision,
                now,
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            foreach (var socket in _sockets)
            {
                Registry.Remove(socket.Session);
                await socket.DisposeAsync();
            }
        }

        private static void Join(
            GameSessionRegistry registry,
            RuntimePolicySessionSocket socket,
            GameCharacter character) =>
            registry.JoinPlayerMap(
                socket.Session,
                character.AccountId,
                character);
    }
}
