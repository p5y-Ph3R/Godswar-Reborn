using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static readonly FieldInfo
        BeforeMedusaBleedVitalsPersistenceHook = RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckBeforeMedusaBleedVitalsPersistence");
    private static readonly FieldInfo
        MedusaBleedSourceRosterDriftPending = RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckMedusaBleedSourceRosterDriftPending");
#endif

    private static async Task CheckMedusaCommittedBleedPrefixAsync()
    {
        await CheckMedusaCommittedBleedWireAsync();
#if DEBUG
        await CheckMedusaBleedPrefixPrecedesPersistenceAsync();
        await CheckMedusaBleedSourceRosterDriftFailsClosedAsync();
        await CheckMedusaBleedSelfPrefixFailureIsolationAsync();
        await CheckMedusaBleedObserverPrefixFailureIsolationAsync();
#endif
    }

    private static async Task CheckMedusaCommittedBleedWireAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Chrysaor", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            observerSocket.Session,
            observer);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);

        try
        {
            var eventId = fixture.FindEvent(
                7_850_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await fixture.AttackAsync(fixture.CreateAttack(eventId));
            var selfImpact = await fixture.Socket.ReadPacketAsync();
            var selfDamage = await fixture.Socket.ReadPacketAsync();
            var worldImpact = await observerSocket.ReadPacketAsync();
            var worldDamage = await observerSocket.ReadPacketAsync();
            var noTrailingFrames =
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    observerSocket);

            var effect = fixture.Mechanics().ActiveEffects.Single();
            Check.True(
                IsExactGenericBleedPair(
                    selfImpact,
                    selfDamage,
                    fixture.Source.ObjectId,
                    MedusaHandlerLocalObjectId) &&
                IsExactGenericBleedPair(
                    worldImpact,
                    worldDamage,
                    fixture.Source.ObjectId,
                    fixture.Context.ObjectId) &&
                effect.Definition.Kind ==
                    MedusaEncounterEffectKind.Bleed &&
                effect.Definition.ClientProjection
                    .RequiresCompatibilityDecision &&
                effect.Definition.ClientProjection
                    .NativeReferenceStatusId == 18 &&
                effect.Definition.ClientProjection
                    .EmittableStatusId is null &&
                effect.Definition.ClientProjection
                    .MatchedNativeClientSceneId is null &&
                noTrailingFrames,
                "committed Chrysaor Bleed publishes one exact generic-2000 impact+damage pair to self and observer, retains server-only Bleed state, and emits no 10167/10166/2041/18 or trailing frame");
        }
        finally
        {
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

#if DEBUG
    private static async Task
        CheckMedusaBleedPrefixPrecedesPersistenceAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Chrysaor", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            observerSocket.Session,
            observer);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);
        using var reachedPersistenceBoundary =
            new ManualResetEventSlim(false);
        using var releasePersistence = new ManualResetEventSlim(false);
        BeforeMedusaBleedVitalsPersistenceHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                reachedPersistenceBoundary.Set();
                if (!releasePersistence.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Bleed persistence boundary was not released.");
                }
            }));

        Task<MonsterPlayerAttackObservation>? attackTask = null;
        try
        {
            var eventId = fixture.FindEvent(
                7_860_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            attackTask = Task.Run(() => fixture.AttackAsync(
                fixture.CreateAttack(eventId)));
            Check.True(
                await Task.Run(() => reachedPersistenceBoundary.Wait(
                    TimeSpan.FromSeconds(5))),
                "committed Bleed reaches the pre-persistence boundary");

            var selfImpact = await fixture.Socket.ReadPacketAsync();
            var selfDamage = await fixture.Socket.ReadPacketAsync();
            var worldImpact = await observerSocket.ReadPacketAsync();
            var worldDamage = await observerSocket.ReadPacketAsync();
            Check.True(
                !attackTask.IsCompleted &&
                IsExactGenericBleedPair(
                    selfImpact,
                    selfDamage,
                    fixture.Source.ObjectId,
                    MedusaHandlerLocalObjectId) &&
                IsExactGenericBleedPair(
                    worldImpact,
                    worldDamage,
                    fixture.Source.ObjectId,
                    fixture.Context.ObjectId),
                "both exact Bleed prefix batches are admitted and delivered while routine vitals persistence invocation and completion remain blocked");

            releasePersistence.Set();
            _ = await attackTask.WaitAsync(TimeSpan.FromSeconds(5));
            var noTrailingFrames =
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    observerSocket);
            Check.True(
                noTrailingFrames,
                "the post-persistence suffix never replays either committed Bleed prefix");
        }
        finally
        {
            releasePersistence.Set();
            BeforeMedusaBleedVitalsPersistenceHook.SetValue(
                fixture.Registry,
                null);
            if (attackTask is not null)
            {
                try
                {
                    _ = await attackTask;
                }
                catch
                {
                }
            }
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task
        CheckMedusaBleedSourceRosterDriftFailsClosedAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Chrysaor", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            observerSocket.Session,
            observer);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);
        MedusaBleedSourceRosterDriftPending.SetValue(
            fixture.Registry,
            1);

        try
        {
            var eventId = fixture.FindEvent(
                7_865_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            var observation = await fixture.AttackAsync(
                fixture.CreateAttack(eventId));
            var noPublishedFrames =
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    observerSocket);

            Check.True(
                fixture.Socket.Session.IsDisconnected &&
                !fixture.Registry.TryGetSessionWorldInstanceId(
                    fixture.Socket.Session,
                    out _) &&
                !observerSocket.Session.IsDisconnected &&
                noPublishedFrames &&
                observation.AfterHealth < observation.BeforeHealth &&
                observation.AfterVitalsRevision ==
                    observation.BeforeVitalsRevision + 1 &&
                fixture.Mechanics().ActiveEffects.IsEmpty,
                "committed-Bleed source-roster drift preserves the irreversible HP/vitals commit, exact-fail-closes and removes the target with exact-life effect cleanup, admits no prefix/status bytes, and cannot fall through to the late ordinary publisher");
        }
        finally
        {
            MedusaBleedSourceRosterDriftPending.SetValue(
                fixture.Registry,
                0);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task
        CheckMedusaBleedSelfPrefixFailureIsolationAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Chrysaor", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            observerSocket.Session,
            observer);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);
        MedusaNativePrefixHook.SetValue(
            fixture.Registry,
            (Action<string>)(stage =>
            {
                if (stage == "SelfDamage")
                {
                    throw new InvalidOperationException(
                        "simulated Bleed pair construction failure");
                }
            }));

        try
        {
            var eventId = fixture.FindEvent(
                7_870_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            var observation = await fixture.AttackAsync(
                fixture.CreateAttack(eventId));
            var worldImpact = await observerSocket.ReadPacketAsync();
            var worldDamage = await observerSocket.ReadPacketAsync();
            var noTrailingFrames =
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    observerSocket);
            Check.True(
                fixture.Socket.Session.IsDisconnected &&
                !fixture.Registry.TryGetSessionWorldInstanceId(
                    fixture.Socket.Session,
                    out _) &&
                fixture.Socket.Available == 0 &&
                !observerSocket.Session.IsDisconnected &&
                IsExactGenericBleedPair(
                    worldImpact,
                    worldDamage,
                    fixture.Source.ObjectId,
                    fixture.Context.ObjectId) &&
                noTrailingFrames &&
                observation.AfterHealth < observation.BeforeHealth &&
                observation.AfterVitalsRevision ==
                    observation.BeforeVitalsRevision + 1 &&
                fixture.Mechanics().ActiveEffects.IsEmpty,
                "a self Bleed prefix construction failure preserves the irreversible HP/vitals commit, admits no self half-pair, exact-fail-closes/removes self with exact-life effect cleanup, and cannot suppress the healthy observer's one exact pair");
        }
        finally
        {
            MedusaNativePrefixHook.SetValue(fixture.Registry, null);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task
        CheckMedusaBleedObserverPrefixFailureIsolationAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(
                "Chrysaor",
                102,
                103);
        await using var firstObserverSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var secondObserverSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var firstObserver = JoinMedusaHandlerMember(
            fixture,
            firstObserverSocket.Session,
            characterId: 102);
        var secondObserver = JoinMedusaHandlerMember(
            fixture,
            secondObserverSocket.Session,
            characterId: 103);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            firstObserverSocket.Session,
            firstObserver);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            secondObserverSocket.Session,
            secondObserver);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(firstObserverSocket);
        await DrainMedusaPacketsAsync(secondObserverSocket);
        var worldDamageFaultPending = 1;
        MedusaNativePrefixHook.SetValue(
            fixture.Registry,
            (Action<string>)(stage =>
            {
                if (stage == "WorldDamage" &&
                    Interlocked.Exchange(
                        ref worldDamageFaultPending,
                        0) == 1)
                {
                    throw new InvalidOperationException(
                        "simulated first-observer pair failure");
                }
            }));

        try
        {
            var eventId = fixture.FindEvent(
                7_880_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await fixture.AttackAsync(fixture.CreateAttack(eventId));
            var selfImpact = await fixture.Socket.ReadPacketAsync();
            var selfDamage = await fixture.Socket.ReadPacketAsync();
            var firstFailed =
                firstObserverSocket.Session.IsDisconnected;
            var secondFailed =
                secondObserverSocket.Session.IsDisconnected;
            Check.True(
                firstFailed ^ secondFailed,
                "exactly one observer is fail-closed by the one-shot Bleed pair construction fault");
            var failedObserverSocket = firstFailed
                ? firstObserverSocket
                : secondObserverSocket;
            var healthyObserverSocket = firstFailed
                ? secondObserverSocket
                : firstObserverSocket;
            var healthyImpact =
                await healthyObserverSocket.ReadPacketAsync();
            var healthyDamage =
                await healthyObserverSocket.ReadPacketAsync();
            var noTrailingFrames =
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    firstObserverSocket,
                    secondObserverSocket);

            Check.True(
                failedObserverSocket.Session.IsDisconnected &&
                failedObserverSocket.Available == 0 &&
                !fixture.Socket.Session.IsDisconnected &&
                !healthyObserverSocket.Session.IsDisconnected &&
                IsExactGenericBleedPair(
                    selfImpact,
                    selfDamage,
                    fixture.Source.ObjectId,
                    MedusaHandlerLocalObjectId) &&
                IsExactGenericBleedPair(
                    healthyImpact,
                    healthyDamage,
                    fixture.Source.ObjectId,
                    fixture.Context.ObjectId) &&
                noTrailingFrames,
                "the first observer's Bleed pair fault admits no half-pair and exact-pair-disconnects only that observer while healthy peer and self each retain one pair with no trailing frame");
        }
        finally
        {
            MedusaNativePrefixHook.SetValue(fixture.Registry, null);
            fixture.Registry.Remove(firstObserverSocket.Session);
            fixture.Registry.Remove(secondObserverSocket.Session);
        }
    }
#endif

    private static bool IsExactGenericBleedPair(
        ReadOnlySpan<byte> impact,
        ReadOnlySpan<byte> damage,
        uint expectedSourceObjectId,
        uint expectedTargetObjectId) =>
        MedusaPacketOpcode(impact) == MedusaImpactOpcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(impact[4..]) ==
            expectedSourceObjectId &&
        BinaryPrimitives.ReadUInt32LittleEndian(impact[8..]) ==
            expectedTargetObjectId &&
        BinaryPrimitives.ReadUInt32LittleEndian(impact[12..]) == 2000 &&
        MedusaPacketOpcode(damage) == MedusaPhysicalDamageOpcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(damage[4..]) ==
            expectedSourceObjectId &&
        BinaryPrimitives.ReadUInt32LittleEndian(damage[20..]) ==
            expectedTargetObjectId;

    private static async Task<bool>
        RemainedWithoutTrailingMedusaFramesAsync(
            params RuntimePolicySessionSocket[] sockets)
    {
        const int quietChecks = 6;
        var quietCheckDelay = TimeSpan.FromMilliseconds(25);
        for (var check = 0; check < quietChecks; check++)
        {
            foreach (var socket in sockets)
            {
                if (socket.Available != 0)
                {
                    return false;
                }
            }

            await Task.Delay(quietCheckDelay);
        }

        foreach (var socket in sockets)
        {
            if (socket.Available != 0)
            {
                return false;
            }
        }

        return true;
    }
}
