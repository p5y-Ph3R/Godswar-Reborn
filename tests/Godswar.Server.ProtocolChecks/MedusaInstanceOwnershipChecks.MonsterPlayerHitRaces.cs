using System.Reflection;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static readonly FieldInfo MedusaOwnerCommitHookField =
        typeof(GameSessionRegistry).GetField(
            "_protocolCheckBeforeMedusaOwnerCommit",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "Debug Medusa owner-commit hook is unavailable.");

    private static readonly FieldInfo MedusaDecisionFaultField =
        typeof(GameSessionRegistry).GetField(
            "_protocolCheckMedusaDecisionFault",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "Debug Medusa decision-fault hook is unavailable.");
#endif

    private static async Task CheckMonsterPlayerStaleReconnectFenceAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        var eventId = fixture.FindEvent(
            start: 20_000,
            static value => value.Hit && value.Damage > 0);
        var oldOwnership = fixture.Ownership;
        var oldWorldInstanceId = fixture.Context.WorldInstanceId;
        var oldWorldRevision = fixture.Context.WorldRevision;
        var oldWorldMembershipEpoch =
            fixture.Context.WorldMembershipEpoch;
        var oldObjectId = fixture.Context.ObjectId;
        var oldLifeRevision = fixture.Registry.GetPlayerLifeRevision(
            fixture.Socket.Session);

        await fixture.ReconnectAndReacquireAsync();
        var staleAttack = fixture.CreateAttack(
            eventId,
            source: fixture.Source,
            ownership: oldOwnership,
            worldInstanceId: oldWorldInstanceId,
            worldRevision: oldWorldRevision,
            targetObjectId: oldObjectId,
            targetLifeRevision: oldLifeRevision,
            worldMembershipEpoch: oldWorldMembershipEpoch);
        var before = fixture.MechanicsSnapshot();
        var rejected = await fixture.AttackAsync(staleAttack);
        var source = RequiredMonster(
            fixture.Map,
            fixture.Source.ObjectId);

        Check.True(
            fixture.Ownership != oldOwnership &&
            rejected.BeforeHealth == rejected.AfterHealth &&
            rejected.BeforeVitalsRevision ==
                rejected.AfterVitalsRevision &&
            source.CombatPhase == MonsterCombatPhase.Attacking &&
            MechanicsSnapshotsValueEqual(before, rejected.Mechanics),
            "emitted old-owner hit cannot mutate HP, mechanics, or reconnected aggro");
    }

    private static async Task CheckTwoMonsterSnapshotHitsAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("First-Normal-01");
        var secondSource = await fixture.ActivateSourceAsync(
            "First-Normal-02");
        var tick = fixture.Runtime.Owner.Invoke(
            map => map.AdvanceMonsters(
                DateTimeOffset.UtcNow.AddMinutes(1),
                session =>
                    fixture.Registry.GetPlayerLifeRevision(session)),
            TimeSpan.FromSeconds(3));
        var attacks = tick.Updates
            .Where(update =>
                update.Kind == MonsterRuntimeUpdateKind.Attacked &&
                (update.Monster.ObjectId == fixture.Source.ObjectId ||
                 update.Monster.ObjectId == secondSource.ObjectId))
            .OrderBy(update => update.Monster.ObjectId)
            .ToArray();
        Check.True(
            attacks.Length == 2 &&
            attacks.All(update =>
                update.TargetVitalsRevision is null &&
                update.TargetOwnership == fixture.Ownership &&
                update.TargetWorldInstanceId ==
                    fixture.Context.WorldInstanceId &&
                update.TargetWorldRevision ==
                    fixture.Context.WorldRevision &&
                update.TargetWorldMembershipEpoch ==
                    fixture.Context.WorldMembershipEpoch),
            "one production ECS tick emits both attacks with exact incarnation fences and no transaction vitals fence");
        var firstSource = attacks[0].Monster;
        secondSource = attacks[1].Monster;
        var firstEvent = FindEvent(
            fixture,
            firstSource,
            "First-Normal-01",
            start: 1_000_000);
        var secondEvent = FindEvent(
            fixture,
            secondSource,
            "First-Normal-02",
            start: firstEvent + 1);
        var firstAttack = attacks[0] with
        {
            AttackEventId = firstEvent
        };
        var secondAttack = attacks[1] with
        {
            AttackEventId = secondEvent
        };

        var first = await fixture.AttackAsync(firstAttack);
        var second = await fixture.AttackAsync(secondAttack);
        Check.True(
            first.AfterHealth < first.BeforeHealth &&
            second.BeforeHealth == first.AfterHealth &&
            second.AfterHealth < second.BeforeHealth &&
            first.AfterVitalsRevision ==
                first.BeforeVitalsRevision + 1 &&
            second.AfterVitalsRevision ==
                first.AfterVitalsRevision + 1,
            "two monsters emitted from one target snapshot both land in serialized order");
    }

#if DEBUG
    private static async Task CheckAcceptedOwnerCommitBlocksEgressAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        MedusaOwnerCommitHookField.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                entered.TrySetResult();
                release.Wait();
            }));

        try
        {
            var eventId = fixture.FindEvent(
                start: 2_000_000,
                static value => value.Hit && value.Damage > 0);
            var attackTask = Task.Run(() =>
                fixture.AttackAsync(
                    fixture.CreateAttack(eventId)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var ownership = fixture.Ownership;
            var removeStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var removeTask = Task.Run(() =>
            {
                removeStarted.TrySetResult();
                return fixture.Registry.Remove(
                    fixture.Socket.Session,
                    ownership);
            });
            await removeStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(3));
            await Task.Delay(250);
            Check.True(
                !attackTask.IsCompleted && !removeTask.IsCompleted,
                "accepted owner mutation outlives configured wait timeout while egress waits for registry authority");

            release.Set();
            var applied = await attackTask.WaitAsync(
                TimeSpan.FromSeconds(3));
            var removed = await removeTask.WaitAsync(
                TimeSpan.FromSeconds(3));
            Check.True(
                applied.AfterHealth < applied.BeforeHealth && removed,
                "accepted delayed owner mutation commits before contending egress");
        }
        finally
        {
            release.Set();
            MedusaOwnerCommitHookField.SetValue(
                fixture.Registry,
                null);
        }
    }

    private static async Task CheckCaptureCommitVitalsRaceAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        MedusaOwnerCommitHookField.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                entered.TrySetResult();
                release.Wait();
            }));
        try
        {
            var eventId = fixture.FindEvent(
                start: 3_000_000,
                static value => value.Hit && value.Damage > 0);
            var attack = fixture.CreateAttack(eventId);
            var before = fixture.MechanicsSnapshot();
            var attackTask = Task.Run(() =>
                fixture.AttackAsync(attack));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            int healthAfterConcurrentMutation;
            lock (fixture.Character.VitalsSync)
            {
                fixture.Character.MarkVitalsChanged();
                healthAfterConcurrentMutation =
                    fixture.Character.CurrentHp;
            }
            release.Set();
            var rejected = await attackTask.WaitAsync(
                TimeSpan.FromSeconds(3));
            MedusaOwnerCommitHookField.SetValue(
                fixture.Registry,
                null);

            Check.True(
                rejected.AfterHealth == healthAfterConcurrentMutation &&
                MechanicsSnapshotsValueEqual(before, rejected.Mechanics),
                "capture-to-commit vitals race rejects without HP or owner-clock mutation");
            var retry = await fixture.AttackAsync(attack);
            Check.True(
                retry.AfterHealth < retry.BeforeHealth,
                "capture-to-commit rejection rolls back replay so the exact event retries");
        }
        finally
        {
            release.Set();
            MedusaOwnerCommitHookField.SetValue(
                fixture.Registry,
                null);
        }
    }

    private static async Task
        CheckMonsterPlayerMissingLifeAuthorityAsync()
    {
        await using (var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale"))
        {
            var eventId = fixture.FindEvent(
                start: 3_500_000,
                static value => value.Hit && value.Damage > 0);
            var attack = fixture.CreateAttack(eventId);
            var before = fixture.MechanicsSnapshot();
            var removed = fixture.Registry
                .ProtocolCheckRemovePlayerLifeRevisionWhileGateHeld(
                    fixture.Socket.Session);
            var rejected = await fixture.AttackAsync(
                attack);
            Check.True(
                removed &&
                rejected.BeforeHealth == rejected.AfterHealth &&
                rejected.BeforeVitalsRevision ==
                    rejected.AfterVitalsRevision &&
                MechanicsSnapshotsValueEqual(before, rejected.Mechanics),
                "missing monster-target life authority rejects before HP, replay, or Medusa mechanics capture");
        }

        await using (var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale"))
        {
            var eventId = fixture.FindEvent(
                start: 3_600_000,
                static value => value.Hit && value.Damage > 0);
            var before = fixture.MechanicsSnapshot();
            var removed = false;
            MedusaOwnerCommitHookField.SetValue(
                fixture.Registry,
                (Action)(() =>
                {
                    MedusaOwnerCommitHookField.SetValue(
                        fixture.Registry,
                        null);
                    removed = fixture.Registry
                        .ProtocolCheckRemovePlayerLifeRevisionWhileGateHeld(
                            fixture.Socket.Session);
                }));
            try
            {
                var rejected = await fixture.AttackAsync(
                    fixture.CreateAttack(eventId));
                Check.True(
                    removed &&
                    rejected.BeforeHealth == rejected.AfterHealth &&
                    rejected.BeforeVitalsRevision ==
                        rejected.AfterVitalsRevision &&
                    MechanicsSnapshotsValueEqual(
                        before,
                        rejected.Mechanics),
                    "life authority removed between owner capture and vitals commit rejects before HP, replay, or effect mutation");
            }
            finally
            {
                MedusaOwnerCommitHookField.SetValue(
                    fixture.Registry,
                    null);
            }
        }
    }

    private static async Task CheckLethalInvariantRecoveryAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        fixture.SetHealth(1);
        MedusaDecisionFaultField.SetValue(fixture.Registry, 1);
        var eventId = fixture.FindEvent(
            start: 4_000_000,
            static value => value.Hit && value.Damage > 0);
        var attack = fixture.CreateAttack(eventId);
        var applied = await fixture.AttackAsync(attack);
        var source = RequiredMonster(
            fixture.Map,
            fixture.Source.ObjectId);
        var mechanics = fixture.Mechanics();

        Check.True(
            applied.AfterHealth == 0 &&
            applied.LifeRevision == 1 &&
            (source.CombatPhase is MonsterCombatPhase.Returning or
                MonsterCombatPhase.AwaitingRetirement) &&
            mechanics.ActiveEffects.Length == 0,
            "malformed applied decision is normalized from actual death and finalizes effects plus aggro");

        var owner = typeof(MapInstance).GetField(
                "_medusaInstanceOwner",
                BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(fixture.Map) ??
            throw new InvalidOperationException(
                "Medusa owner diagnostics are unavailable.");
        var ownerReplayCount = ReadPrivateCollectionCount(
            owner,
            "_monsterPlayerHitReplay");
        var outerLedger = typeof(GameSessionRegistry).GetField(
                "_monsterIncomingAttackReplay",
                BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(fixture.Registry) ??
            throw new InvalidOperationException(
                "Monster incoming replay diagnostics are unavailable.");
        var outerReplayCount = ReadPrivateCollectionCount(
            outerLedger,
            "_claimed");
        Check.True(
            ownerReplayCount == 1 &&
            outerReplayCount == 1,
            $"invariant-recovered lethal event remains durable in Map and outer replay ledgers (owner={ownerReplayCount}, outer={outerReplayCount}, event={eventId})");
    }

    private static int ReadPrivateCollectionCount(
        object owner,
        string fieldName)
    {
        var collection = owner.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(owner) ??
            throw new InvalidOperationException(
                $"Replay collection {fieldName} is unavailable.");
        return (int)(collection.GetType().GetProperty("Count")?
            .GetValue(collection) ??
            throw new InvalidOperationException(
                $"Replay collection {fieldName} has no count."));
    }
#endif

    private static ulong FindEvent(
        MonsterPlayerHitFixture fixture,
        MonsterRuntimeSnapshot source,
        string rosterSpawnId,
        ulong start)
    {
        for (var eventId = start;
             eventId < start + 100_000;
             eventId++)
        {
            var resolution = fixture.Resolve(
                source,
                rosterSpawnId,
                eventId);
            if (resolution.Hit && resolution.Damage > 0)
            {
                return eventId;
            }
        }

        throw new InvalidOperationException(
            $"No deterministic {rosterSpawnId} hit was found.");
    }
}
