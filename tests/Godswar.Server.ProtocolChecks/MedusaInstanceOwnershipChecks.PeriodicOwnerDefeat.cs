using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckPeriodicPreparedDefeat()
    {
        CheckPeriodicPreparedDefeatCompletion();
#if DEBUG
        CheckPeriodicPreparedDefeatFault(mode: 1, completesRun: false);
        CheckPeriodicPreparedDefeatFault(mode: 2, completesRun: false);
        CheckPeriodicPreparedDefeatFault(mode: 3, completesRun: true);
        for (var mode = 1; mode <= 3; mode++)
        {
            CheckPeriodicPreparedDefeatRegistryFallbackAsync(mode)
                .GetAwaiter()
                .GetResult();
        }
#endif
    }

    private static void CheckPeriodicPreparedDefeatCompletion()
    {
        var fixture = CreateAttachmentFixture();
        Check.True(
            AttachAuthored(fixture).IsAttached,
            "prepared-defeat fixture attaches the ECS owner");
        var map = fixture.Map;
        var session = new ClientSession(
            new ScriptedLegacyByteTransport());
        var context = CreatePeriodicDamageContext(map, session);
        map.AddOrUpdate(context);
        try
        {
            var ownership = RequiredOwnership(map);
            var bleed = Binding(ownership, "Chrysaor");
            Check.True(
                map.TryCommitOwnerMechanicForInvariantTest(
                    context.CharacterId,
                    bleed.Identity.ObjectId,
                    bleed.Identity.SpawnGeneration,
                    StartedAt.AddSeconds(1),
                    out var bleedApplied) &&
                bleedApplied.MechanicsResult?.Outcome ==
                    MedusaMechanicHitOutcome.Applied,
                "prepared-defeat fixture applies Bleed through the owner");

            var ordered = OrderedMedusaRosterMonsters(map);
            foreach (var target in ordered[..^1])
            {
                var killed = CommitPeriodicOwnerDamage(
                    map,
                    session,
                    context,
                    target,
                    StartedAt.AddSeconds(1));
                Check.True(
                    killed is
                    {
                        Applied: true,
                        DamageResult:
                        {
                            Killed: true,
                            HealthMutation: not null
                        },
                        Defeat.GateOutcome:
                            MedusaOwnedOperationGateOutcome.Delegated
                    },
                    "each preterminal roster defeat commits through the prepared ECS path");
            }

            var finalBefore = RequiredMonster(map, ordered[^1].ObjectId);
            var dueAt = StartedAt.AddSeconds(3);
            var blocked = CommitPeriodicOwnerDamage(
                map,
                session,
                context,
                finalBefore,
                dueAt);
            Check.True(
                blocked.Outcome ==
                    MedusaPlayerMonsterDamageOutcome
                        .PeriodicDamageHandoffUnavailable &&
                blocked.DamageResult is null &&
                RequiredMonster(map, finalBefore.ObjectId).CurrentHealth ==
                    finalBefore.CurrentHealth,
                "a due Bleed tick blocks the final lethal HP mutation");

            Check.True(
                map.TryObserveMedusaTime(dueAt, out var due) &&
                due.MechanicsResult?.PeriodicDamage is { } reservation &&
                TryCompletePeriodicDamageForProtocolCheck(
                    map,
                    reservation,
                    terminal: false,
                    out var disposition) &&
                disposition ==
                    MedusaPeriodicDamageDispositionOutcome.Applied,
                "the exact due tick is consumed before the final defeat retry");

            var retryTarget = RequiredMonster(map, finalBefore.ObjectId);
            var completed = CommitPeriodicOwnerDamage(
                map,
                session,
                context,
                retryTarget,
                dueAt);
            var after = RequiredMonster(map, finalBefore.ObjectId);
            var finalOwnership = RequiredOwnership(map);
            Check.True(
                completed is
                {
                    Applied: true,
                    DamageResult:
                    {
                        Killed: true,
                        HealthMutation: not null
                    },
                    Defeat:
                    {
                        GateOutcome:
                            MedusaOwnedOperationGateOutcome.Delegated,
                        Claim.Outcome: MedusaDefeatClaimOutcome.Completed,
                        SourceRetirement.Outcome:
                            MedusaMechanicSourceRetireOutcome.Retired
                    }
                } &&
                after.CurrentHealth == 0 &&
                after.HealthRevision ==
                    retryTarget.HealthRevision + 1 &&
                finalOwnership.Run.State == MedusaRunState.Completed &&
                finalOwnership.Run.TeamScore ==
                    finalOwnership.Run.Spawns.Sum(static spawn =>
                        spawn.ScoreValue) &&
                finalOwnership.Run.Spawns.All(static spawn =>
                    spawn.Defeated) &&
                finalOwnership.Mechanics.Characters.All(static character =>
                    character.ActiveEffects.IsEmpty),
                "prepared defeat commits ECS HP, score, retirement, and terminal clear exactly once");
            AssertCoupledAt(map, dueAt, "prepared terminal defeat");

            var duplicate = CommitPeriodicOwnerDamage(
                map,
                session,
                context,
                after,
                dueAt);
            Check.True(
                !duplicate.Applied &&
                RequiredMonster(map, after.ObjectId).HealthRevision ==
                    after.HealthRevision &&
                RequiredOwnership(map).Run.TeamScore ==
                    finalOwnership.Run.TeamScore,
                "a replay after terminal HP cannot duplicate health or score");
        }
        finally
        {
            _ = map.Remove(session, out _);
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

#if DEBUG
    private static void CheckPeriodicPreparedDefeatFault(
        int mode,
        bool completesRun)
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            $"prepared-defeat fault {mode} fixture attaches");
        var map = fixture.Map;
        var session = new ClientSession(
            new ScriptedLegacyByteTransport());
        var context = CreatePeriodicDamageContext(map, session);
        map.AddOrUpdate(context);
        try
        {
            var ordered = OrderedMedusaRosterMonsters(map);
            var faultTarget = completesRun ? ordered[^1] : ordered[0];
            if (completesRun)
            {
                foreach (var target in ordered[..^1])
                {
                    Check.True(
                        CommitPeriodicOwnerDamage(
                            map,
                            session,
                            context,
                            target,
                            StartedAt.AddSeconds(1)).Applied,
                        "prepared terminal-fault setup defeats its prefix roster");
                }
                faultTarget = RequiredMonster(map, faultTarget.ObjectId);
            }

            var before = RequiredOwnership(map);
            SetPreparedDefeatFault(map, mode);
            var committed = CommitPeriodicOwnerDamage(
                map,
                session,
                context,
                faultTarget,
                StartedAt.AddSeconds(1));
            var after = RequiredOwnership(map);
            var sourcePreview = RequiredOwnerMechanicsForInvariantTest(map)
                .PreviewRetireMonster(
                    faultTarget.ObjectId,
                    faultTarget.SpawnGeneration,
                    StartedAt.AddSeconds(1));
            Check.True(
                committed is
                {
                    Applied: true,
                    DamageResult:
                    {
                        Killed: true,
                        HealthMutation: not null
                    },
                    Defeat.GateOutcome:
                        MedusaOwnedOperationGateOutcome.InvariantFault
                } &&
                after.Run.TeamScore == before.Run.TeamScore &&
                after.Run.State == MedusaRunState.Active &&
                sourcePreview ==
                    MedusaMechanicSourceRetireOutcome.Retired,
                $"prepared-defeat fault {mode} is typed after HP without partial owner mutation or an exception");
        }
        finally
        {
            _ = map.Remove(session, out _);
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task
        CheckPeriodicPreparedDefeatRegistryFallbackAsync(int mode)
    {
        await using var fixture = await MonsterPlayerHitFixture.CreateAsync(
            "E1-Elite");
        var ordered = OrderedMedusaRosterMonsters(fixture.Map);
        var count = mode == 3 ? ordered.Length : 1;
        for (var index = 0; index < count; index++)
        {
            var target = RequiredMonster(
                fixture.Map,
                ordered[index].ObjectId);
            Check.True(
                fixture.Registry.TryCapturePlayerMonsterTarget(
                    fixture.Socket.Session,
                    mapId: 200,
                    target.ObjectId,
                    out var captured,
                    out var authority),
                "registry defeat fallback captures current ECS authority");
            if (index == count - 1)
            {
                SetPreparedDefeatFault(fixture.Map, mode);
            }

            var applied = fixture.Registry
                .TryCommitPlayerMonsterDamageGuarded(
                    fixture.Socket.Session,
                    mapId: 200,
                    captured.ObjectId,
                    captured.RuntimeInstanceId,
                    fixture.Character.Id,
                    captured.SpawnGeneration,
                    captured.HealthRevision,
                    authority,
                    DateTimeOffset.UtcNow,
                    Resolution(
                        CombatDamageChannel.Physical,
                        uint.MaxValue),
                    out var commit);
            Check.True(
                applied &&
                commit.DamageResult?.Killed == true,
                "registry defeat fallback commits each ECS lethal scalar mutation");
            if (index == count - 1)
            {
                Check.True(
                    commit.Defeat?.GateOutcome ==
                        MedusaOwnedOperationGateOutcome.InvariantFault,
                    "registry receives the typed post-HP terminal-clear invariant fault");
            }
        }

        Check.True(
            SpinWait.SpinUntil(
                () => fixture.Socket.Session.IsDisconnected,
                TimeSpan.FromSeconds(2)) &&
            !fixture.Registry.IsSessionInWorldInstance(
                fixture.Socket.Session,
                fixture.Runtime.InstanceId),
            "the preprepared registry fallback disconnects outside the owner and registry gates");
    }

    private static void SetPreparedDefeatFault(MapInstance map, int mode) =>
        typeof(MapInstance).GetField(
                "_protocolCheckMedusaPreparedDefeatFault",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(map, mode);
#endif

    private static MonsterRuntimeSnapshot[]
        OrderedMedusaRosterMonsters(MapInstance map)
    {
        var rolesByObjectId = RequiredOwnership(map).Run.Spawns
            .ToDictionary(
                static spawn => spawn.ObjectId,
                static spawn => spawn.Role);
        return map.SnapshotMonsters()
            .Where(monster => rolesByObjectId.ContainsKey(monster.ObjectId))
            .OrderBy(monster => rolesByObjectId[monster.ObjectId] switch
            {
                MedusaEncounterEnemyRole.Stheno => 1,
                MedusaEncounterEnemyRole.Medusa => 2,
                _ => 0
            })
            .ThenBy(static monster => monster.ObjectId)
            .ToArray();
    }

    private static GameSessionContext CreatePeriodicDamageContext(
        MapInstance map,
        ClientSession session) =>
        CreateAdmittedDamageContext(map, session, characterId: 101) with
        {
            Ownership =
                MedusaEncounterMechanicsRuntime.CompatibilityOwnership,
            WorldMembershipEpoch = 1
        };

    private static MedusaPlayerMonsterDamageCommit
        CommitPeriodicOwnerDamage(
            MapInstance map,
            ClientSession session,
            GameSessionContext context,
            MonsterRuntimeSnapshot target,
            DateTimeOffset committedAt) =>
        map.TryCommitPlayerMonsterDamageForSessionGuarded(
            session,
            target.ObjectId,
            target.RuntimeInstanceId,
            context.CharacterId,
            target.SpawnGeneration,
            target.HealthRevision,
            new(
                context.WorldInstanceId,
                context.WorldRevision,
                context.Ownership,
                LifeRevision: 0,
                context.WorldMembershipEpoch),
            committedAt,
            Resolution(
                CombatDamageChannel.Physical,
                uint.MaxValue));

    private static MedusaInstanceOwnershipSnapshot RequiredOwnership(
        MapInstance map) => map.TryGetMedusaOwnershipSnapshot(out var value)
        ? value
        : throw new InvalidOperationException(
            "The Medusa owner snapshot is unavailable.");
}
