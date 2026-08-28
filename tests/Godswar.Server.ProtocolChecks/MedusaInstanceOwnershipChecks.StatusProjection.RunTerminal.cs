using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static readonly FieldInfo MedusaTerminalScheduleHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckBeforeMedusaTerminalSchedule");
    private static readonly FieldInfo MedusaTerminalMemberHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckBeforeMedusaTerminalMemberPublication");
#endif

    private static async Task
        CheckMedusaRunTerminalClearsActiveAmplifierAsync()
    {
        await CheckMedusaRunTerminalClearHappyPathAsync();
#if DEBUG
        await CheckMedusaTerminalPreparationRejectsMissingLifeAsync();
        await CheckMedusaTerminalHandoffFailureAsync(
            failSchedule: true);
        await CheckMedusaTerminalHandoffFailureAsync(
            failSchedule: false);
#endif
    }

    private static async Task
        CheckMedusaRunTerminalClearHappyPathAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(
                "Final-Pikeman-1",
                102);
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
                8_590_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await fixture.AttackAsync(
                fixture.CreateAttack(eventId));
            var selfApplication =
                await ReadMedusaAttackStatusSequenceAsync(
                    fixture.Socket);
            var observerApplication =
                await ReadMedusaAttackStatusSequenceAsync(
                    observerSocket);
            Check.True(
                selfApplication.StatusId == 236 &&
                observerApplication.StatusId == 236 &&
                fixture.Mechanics().ActiveEffects.Any(effect =>
                    effect.Definition.Kind ==
                        MedusaEncounterEffectKind
                            .OutgoingPhysicalAmplifier),
                "the final-island Pikeman amplifier is active and projected before run completion");

            var finalSpawn = fixture.Preparation.Inputs.RunSpawns
                .First(spawn =>
                    spawn.Role == MedusaEncounterEnemyRole.Medusa);
            var stheno = fixture.Preparation.Inputs.RunSpawns
                .First(spawn =>
                    spawn.Role == MedusaEncounterEnemyRole.Stheno);
            var committedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            MedusaPlayerMonsterDamageCommit finalCommit = default;
            var ordered = fixture.Preparation.Inputs.RunSpawns
                .Where(spawn => spawn != finalSpawn && spawn != stheno)
                .Append(stheno)
                .Append(finalSpawn)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var spawn = ordered[index];
                var target = FindMonster(
                    fixture.Map,
                    spawn.RosterSpawnId);
#if DEBUG
                SemaphoreSlim? heldStatusGate = null;
                if (spawn == finalSpawn)
                {
                    heldStatusGate = GetMedusaStatusGate(
                        fixture.Registry,
                        fixture.Socket.Session);
                    await heldStatusGate.WaitAsync(
                        CancellationToken.None);
                }
#endif
                MedusaPlayerMonsterDamageCommit commit = default;
                try
                {
                Check.True(
                    fixture.Registry.TryCapturePlayerMonsterTarget(
                        fixture.Socket.Session,
                        mapId: 200,
                        target.ObjectId,
                        out var captured,
                        out var authority) &&
                    fixture.Registry
                        .TryCommitPlayerMonsterDamageGuarded(
                            fixture.Socket.Session,
                            mapId: 200,
                            captured.ObjectId,
                            captured.RuntimeInstanceId,
                            fixture.Character.Id,
                            captured.SpawnGeneration,
                            captured.HealthRevision,
                            authority,
                            committedAt.AddMilliseconds(index),
                            Resolution(
                                spawn.Role ==
                                    MedusaEncounterEnemyRole.Medusa
                                    ? CombatDamageChannel.Magic
                                    : CombatDamageChannel.Physical,
                                uint.MaxValue),
                            out commit) &&
                    commit.DamageResult is { Killed: true },
                    $"terminal-clear fixture defeats {spawn.RosterSpawnId}");
                if (spawn == finalSpawn)
                {
                    finalCommit = commit;
#if DEBUG
                    fixture.Registry.UpdateCharacter(
                        fixture.Socket.Session,
                        fixture.Character,
                        advanceWorldRevision: true);
                    var refreshed =
                        fixture.Map.Snapshot().Single(context =>
                            ReferenceEquals(
                                context.Session,
                                fixture.Socket.Session));
                    Check.True(
                        !ReferenceEquals(refreshed, fixture.Context) &&
                        refreshed.WorldRevision ==
                            fixture.Context.WorldRevision + 1 &&
                        refreshed.WorldMembershipEpoch ==
                            fixture.Context.WorldMembershipEpoch,
                        "a routine revision advances context but preserves membership lineage while terminal clear waits");
#endif
                }
                }
                finally
                {
#if DEBUG
                    heldStatusGate?.Release();
#endif
                }
            }

            Check.True(
                finalCommit.Defeat is
                {
                    Claim.Outcome: MedusaDefeatClaimOutcome.Completed
                },
                "the final typed defeat commits the run terminal before client clearing");
            var selfClear = await ReadMedusaTerminalClearAsync(
                fixture.Socket,
                MedusaHandlerLocalObjectId);
            var observerClear = await ReadMedusaTerminalClearAsync(
                observerSocket,
                fixture.Context.ObjectId);
            Check.True(
                BinaryPrimitives.ReadUInt32LittleEndian(
                    selfClear.AsSpan(8)) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    observerClear.AsSpan(8)) == 0,
                "early Completed immediately publishes an exact complete amplifier clear to self and same-instance observers");
        }
        finally
        {
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

#if DEBUG
    private static async Task
        CheckMedusaTerminalPreparationRejectsMissingLifeAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var target = FindMonster(fixture.Map, "E1-Elite");
        Check.True(
            fixture.Registry.TryCapturePlayerMonsterTarget(
                fixture.Socket.Session,
                mapId: 200,
                target.ObjectId,
                out var captured,
                out var authority),
            "the missing-life terminal fixture captures owner authority");
        Check.True(
            fixture.Map.TryGetMedusaOwnershipSnapshot(
                out var beforeOwnership),
            "the missing-life terminal fixture captures owner state");
        var removedLife = fixture.Registry
            .ProtocolCheckRemovePlayerLifeRevisionWhileGateHeld(
                fixture.Socket.Session);
        var recaptured = fixture.Registry.TryCapturePlayerMonsterTarget(
            fixture.Socket.Session,
            mapId: 200,
            captured.ObjectId,
            out _,
            out _);
        var committed = fixture.Registry.TryCommitPlayerMonsterDamageGuarded(
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
            out _);
        var after = RequiredMonster(
            fixture.Map,
            captured.ObjectId);
        Check.True(
            fixture.Map.TryGetMedusaOwnershipSnapshot(
                out var afterOwnership) &&
            removedLife &&
            !recaptured &&
            !committed &&
            after.CurrentHealth == captured.CurrentHealth &&
            after.HealthRevision == captured.HealthRevision &&
            afterOwnership.Run.State == beforeOwnership.Run.State &&
            afterOwnership.Run.TeamScore ==
                beforeOwnership.Run.TeamScore &&
            afterOwnership.Run.Spawns.Count(spawn => spawn.Defeated) ==
                beforeOwnership.Run.Spawns.Count(spawn => spawn.Defeated),
            "a ready member without a life fence rejects capture and commit before monster HP or run state can mutate");

    }

    private static async Task CheckMedusaTerminalHandoffFailureAsync(
        bool failSchedule)
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(
                "E1-Elite",
                102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        var now = DateTimeOffset.UtcNow;
        var status = new SkillStatusEffectDefinition(
            SkillId: 1,
            StatusId: failSchedule ? 60_601U : 60_602U,
            Kind: failSchedule ? 80_601 : 80_602,
            Priority: 1,
            Beneficial: true,
            Duration: TimeSpan.FromMinutes(1),
            Cooldown: TimeSpan.Zero,
            HitBonus: 0,
            CriticalAppendBonus: 0);
        Check.True(
            await fixture.Registry.ApplyRuntimeStatusAndPublishAsync(
                fixture.Socket.Session,
                status,
                now,
                "MedusaTerminalFailureTarget",
                CancellationToken.None) &&
            await fixture.Registry.ApplyRuntimeStatusAndPublishAsync(
                observerSocket.Session,
                status with
                {
                    StatusId = status.StatusId + 10,
                    Kind = status.Kind + 10
                },
                now,
                "MedusaTerminalFailureObserver",
                CancellationToken.None),
            "terminal failure fixtures establish both member status gates");
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);

        var disconnectCount = 0;
        var everyGateWasFree = true;
        var registryGateWasFree = true;
        ExactStatusDisconnectHook.SetValue(
            fixture.Registry,
            (Action<ClientSession>)(session =>
            {
                if (!ReferenceEquals(session, fixture.Socket.Session) &&
                    !ReferenceEquals(session, observerSocket.Session))
                {
                    return;
                }
                everyGateWasFree &= IsMedusaStatusGateFree(
                    fixture.Registry,
                    session);
                registryGateWasFree &= fixture.Registry
                    .ProtocolCheckIsRegistryGateFree();
                Interlocked.Increment(ref disconnectCount);
            }));
        if (failSchedule)
        {
            MedusaTerminalScheduleHook.SetValue(
                fixture.Registry,
                (Action)(() => throw new InvalidOperationException(
                    "simulated terminal scheduling failure")));
        }
        else
        {
            MedusaTerminalMemberHook.SetValue(
                fixture.Registry,
                (Action<int>)(_ => throw new InvalidOperationException(
                    "simulated terminal member publication failure")));
        }

        try
        {
            var finalCommit = DefeatEveryMedusaSpawn(
                fixture,
                now.AddSeconds(1));
            Check.True(
                finalCommit.Defeat is
                {
                    Claim.Outcome: MedusaDefeatClaimOutcome.Completed
                } &&
                SpinWait.SpinUntil(
                    () => fixture.Socket.Session.IsDisconnected &&
                        observerSocket.Session.IsDisconnected,
                    TimeSpan.FromSeconds(5)) &&
                disconnectCount == 2 &&
                everyGateWasFree &&
                registryGateWasFree,
                $"a terminal {(failSchedule ? "schedule" : "worker")} fault exact-fails-closed every prepared current member after all registry/status gates are released");
        }
        finally
        {
            ExactStatusDisconnectHook.SetValue(
                fixture.Registry,
                null);
            MedusaTerminalScheduleHook.SetValue(
                fixture.Registry,
                null);
            MedusaTerminalMemberHook.SetValue(
                fixture.Registry,
                null);
            fixture.Registry.Remove(observerSocket.Session);
            _ = observer;
        }
    }

    private static MedusaPlayerMonsterDamageCommit DefeatEveryMedusaSpawn(
        MonsterPlayerHitFixture fixture,
        DateTimeOffset committedAt)
    {
        MedusaPlayerMonsterDamageCommit final = default;
        var spawns = fixture.Preparation.Inputs.RunSpawns
            .OrderBy(static spawn => spawn.Role switch
            {
                MedusaEncounterEnemyRole.Stheno => 1,
                MedusaEncounterEnemyRole.Medusa => 2,
                _ => 0
            })
            .ToArray();
        for (var index = 0; index < spawns.Length; index++)
        {
            var spawn = spawns[index];
            var target = FindMonster(fixture.Map, spawn.RosterSpawnId);
            Check.True(
                fixture.Registry.TryCapturePlayerMonsterTarget(
                    fixture.Socket.Session,
                    mapId: 200,
                    target.ObjectId,
                    out var captured,
                    out var authority) &&
                fixture.Registry.TryCommitPlayerMonsterDamageGuarded(
                    fixture.Socket.Session,
                    mapId: 200,
                    captured.ObjectId,
                    captured.RuntimeInstanceId,
                    fixture.Character.Id,
                    captured.SpawnGeneration,
                    captured.HealthRevision,
                    authority,
                    committedAt.AddMilliseconds(index),
                    Resolution(
                        spawn.Role == MedusaEncounterEnemyRole.Medusa
                            ? CombatDamageChannel.Magic
                            : CombatDamageChannel.Physical,
                        uint.MaxValue),
                    out final) &&
                final.DamageResult is { Killed: true },
                $"terminal fallback fixture defeats {spawn.RosterSpawnId}");
        }
        return final;
    }
#endif

    private static async Task<byte[]> ReadMedusaTerminalClearAsync(
        RuntimePolicySessionSocket socket,
        uint expectedObjectId)
    {
        for (var index = 0; index < 4; index++)
        {
            var packet = await socket.ReadPacketAsync();
            if (MedusaPacketOpcode(packet) == MedusaStatusOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.AsSpan(4)) == expectedObjectId &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.AsSpan(8)) == 0)
            {
                return packet;
            }
        }

        throw new InvalidOperationException(
            $"No exact Medusa terminal clear was published for object " +
            $"{expectedObjectId}.");
    }
}
