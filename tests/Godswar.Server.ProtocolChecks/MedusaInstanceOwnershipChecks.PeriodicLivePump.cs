using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private readonly record struct LiveBleedTiming(
        DateTimeOffset AppliedAt,
        DateTimeOffset DueAt,
        DateTimeOffset ExpiresAt);

    private static async Task CheckPeriodicLiveWorldPumpAsync()
    {
        await CheckPeriodicLiveNonlethalRecoveryAsync();
        await CheckPeriodicLiveLethalAsync();
#if DEBUG
        await CheckPeriodicLiveTransferredTargetAsync();
#endif
    }

    private static async Task CheckPeriodicLiveNonlethalRecoveryAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Chrysaor", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        _ = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);

        try
        {
            await DrainMedusaPacketsAsync(fixture.Socket);
            await DrainMedusaPacketsAsync(observerSocket);
            var timing = ApplyLivePeriodicBleed(fixture);
            DrainLivePeriodicMonsterSetup(fixture, timing.AppliedAt);
            await DrainMedusaPacketsAsync(fixture.Socket);
            await DrainMedusaPacketsAsync(observerSocket);

            var beforeHealth = ReadLivePeriodicHealth(fixture);
            var beforeVitals = fixture.Character.VitalsRevision;
            var beforeLife = fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);
            var beforeDecision = fixture.Registry
                .GetPlayerVitalsDamageEcsDiagnostics(
                    fixture.Socket.Session);

            await fixture.Registry.AdvanceMonsterWorldOnceAsync(
                timing.DueAt.AddTicks(-1),
                CancellationToken.None);
            Check.True(
                ReadLivePeriodicHealth(fixture) == beforeHealth &&
                fixture.Character.VitalsRevision == beforeVitals &&
                fixture.Registry.GetPlayerLifeRevision(
                    fixture.Socket.Session) == beforeLife &&
                Equals(
                    beforeDecision,
                    fixture.Registry
                        .GetPlayerVitalsDamageEcsDiagnostics(
                            fixture.Socket.Session)) &&
                !fixture.Registry.MedusaPeriodicDamageLedger
                    .TryGetSnapshot(fixture.Runtime.InstanceId, out _),
                "the live world pump emits no immediate or early Bleed tick");
            await DrainMedusaPacketsAsync(fixture.Socket);
            await DrainMedusaPacketsAsync(observerSocket);

#if DEBUG
            var monsterTicksBeforeRecovery = 0;
            fixture.Registry.ProtocolCheckMonsterWorldTickObserved =
                (instanceId, _) =>
                {
                    if (instanceId == fixture.Runtime.InstanceId)
                    {
                        monsterTicksBeforeRecovery++;
                    }
                };
            fixture.Registry
                .ProtocolCheckAfterMedusaPeriodicOwnerAcknowledgement =
                static () => throw new InvalidOperationException(
                    "simulated lost periodic owner acknowledgement");
            await fixture.Registry.AdvanceMonsterWorldOnceAsync(
                timing.DueAt,
                CancellationToken.None);

            var retained = default(MedusaPeriodicDamageLedgerSnapshot);
            var retainedHp = ReadLivePeriodicHealth(fixture);
            var retainedDecision = fixture.Registry
                .GetPlayerVitalsDamageEcsDiagnostics(
                    fixture.Socket.Session);
            Check.True(
                retainedHp == beforeHealth - 200 &&
                fixture.Character.VitalsRevision == beforeVitals + 1 &&
                fixture.Registry.GetPlayerLifeRevision(
                    fixture.Socket.Session) == beforeLife &&
                retainedDecision is
                {
                    Applied: true,
                    Killed: false,
                    RequestedDamage: 200,
                    AppliedDamage: 200,
                    PetHealing: null
                } &&
                fixture.Registry.MedusaPeriodicDamageLedger.TryGetSnapshot(
                    fixture.Runtime.InstanceId,
                    out retained) &&
                retained.Phase ==
                    MedusaPeriodicDamageLedgerPhase.HPCommitted &&
                retained.HpCommit is
                {
                    BeforeHealth: var retainedBefore,
                    AfterHealth: var retainedAfter
                } &&
                retainedBefore == beforeHealth &&
                retainedAfter == retainedHp &&
                monsterTicksBeforeRecovery == 0 &&
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    observerSocket),
                "a lost post-HP owner result retains exact HP evidence, emits no bytes, and blocks monster advancement");

            fixture.Registry
                .ProtocolCheckAfterMedusaPeriodicOwnerAcknowledgement = null;
            var observedHealthBeforeMonsterAdvance = -1;
            fixture.Registry.ProtocolCheckMonsterWorldTickObserved =
                (instanceId, _) =>
                {
                    if (instanceId == fixture.Runtime.InstanceId)
                    {
                        observedHealthBeforeMonsterAdvance =
                            ReadLivePeriodicHealth(fixture);
                    }
                };
            var settlementMasks = new List<ulong>();
            fixture.Registry.MedusaPeriodicDamageLedger
                .ProtocolCheckBeforeRecipientSettlementTransition = () =>
                {
                    if (fixture.Registry.MedusaPeriodicDamageLedger
                            .TryGetSnapshot(
                                fixture.Runtime.InstanceId,
                                out var current))
                    {
                        settlementMasks.Add(
                            current.RecipientSettlementMask);
                    }
                };
#endif

            await fixture.Registry.AdvanceMonsterWorldOnceAsync(
                timing.DueAt,
                CancellationToken.None);
            var afterHealth = ReadLivePeriodicHealth(fixture);
            var observerVitals = await observerSocket.ReadPacketAsync();
            var selfVitals = await fixture.Socket.ReadPacketAsync();
            Check.True(
                afterHealth == beforeHealth - 200 &&
                fixture.Character.VitalsRevision == beforeVitals + 1 &&
                fixture.Registry.GetPlayerLifeRevision(
                    fixture.Socket.Session) == beforeLife &&
                IsLivePeriodicVitals(
                    observerVitals,
                    fixture.PlayerObjectId,
                    afterHealth,
                    fixture.Character.CurrentMp) &&
                IsLivePeriodicVitals(
                    selfVitals,
                    MedusaHandlerLocalObjectId,
                    afterHealth,
                    fixture.Character.CurrentMp) &&
                !fixture.Registry.MedusaPeriodicDamageLedger
                    .TryGetSnapshot(fixture.Runtime.InstanceId, out _) &&
                RequiredLiveBleed(fixture).EmittedPeriodicTicks == 1 &&
                RequiredLiveBleed(fixture).NextPeriodicTickAt ==
                    timing.DueAt.AddSeconds(2) &&
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    observerSocket),
                "lost-result recovery publishes one observer-first/self-last vitals update and never reapplies HP");
#if DEBUG
            Check.True(
                observedHealthBeforeMonsterAdvance == afterHealth &&
                settlementMasks.SequenceEqual(new ulong[] { 1, 3 }),
                "the recovered committed tick settles each frozen recipient before the next and drains before AdvanceMonsters");
#endif

            await fixture.Registry.AdvanceMonsterWorldOnceAsync(
                timing.DueAt,
                CancellationToken.None);
            Check.True(
                ReadLivePeriodicHealth(fixture) == afterHealth &&
                fixture.Character.VitalsRevision == beforeVitals + 1 &&
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    observerSocket),
                "re-observing the settled due instant cannot replay HP or bytes");
        }
        finally
        {
#if DEBUG
            fixture.Registry
                .ProtocolCheckAfterMedusaPeriodicOwnerAcknowledgement = null;
            fixture.Registry.ProtocolCheckMonsterWorldTickObserved = null;
            fixture.Registry.MedusaPeriodicDamageLedger
                .ProtocolCheckBeforeRecipientSettlementTransition = null;
#endif
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task CheckPeriodicLiveLethalAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Chrysaor", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        _ = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);

        try
        {
            fixture.SetHealth(200);
            var beforeVitals = fixture.Character.VitalsRevision;
            var beforeLife = fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);
            var timing = ApplyLivePeriodicBleed(fixture);
            DrainLivePeriodicMonsterSetup(fixture, timing.AppliedAt);
            await DrainMedusaPacketsAsync(fixture.Socket);
            await DrainMedusaPacketsAsync(observerSocket);

            await fixture.Registry.AdvanceMonsterWorldOnceAsync(
                timing.DueAt,
                CancellationToken.None);
            var observerVitals = await observerSocket.ReadPacketAsync();
            var observerDeath = await observerSocket.ReadPacketAsync();
            var selfVitals = await fixture.Socket.ReadPacketAsync();
            var selfDeath = await fixture.Socket.ReadPacketAsync();
            var afterLife = fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);
            Check.True(
                ReadLivePeriodicHealth(fixture) == 0 &&
                fixture.Character.VitalsRevision == beforeVitals + 1 &&
                afterLife == beforeLife + 1 &&
                IsLivePeriodicVitals(
                    observerVitals,
                    fixture.PlayerObjectId,
                    currentHealth: 0,
                    fixture.Character.CurrentMp) &&
                IsLivePeriodicDeath(
                    observerDeath,
                    fixture.PlayerObjectId,
                    fixture.Character.CurrentMap) &&
                IsLivePeriodicVitals(
                    selfVitals,
                    MedusaHandlerLocalObjectId,
                    currentHealth: 0,
                    fixture.Character.CurrentMp) &&
                IsLivePeriodicDeath(
                    selfDeath,
                    MedusaHandlerLocalObjectId,
                    fixture.Character.CurrentMap) &&
                RequiredOwnership(fixture.Map).Mechanics.Characters
                    .Single(character =>
                        character.CharacterId == fixture.Character.Id)
                    .ActiveEffects.IsEmpty &&
                !fixture.Registry.MedusaPeriodicDamageLedger
                    .TryGetSnapshot(fixture.Runtime.InstanceId, out _) &&
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    observerSocket),
                "a lethal live tick advances life once and publishes only atomic vitals/death batches before cleanup and persistence");

            await fixture.Registry.AdvanceMonsterWorldOnceAsync(
                timing.DueAt,
                CancellationToken.None);
            Check.True(
                ReadLivePeriodicHealth(fixture) == 0 &&
                fixture.Character.VitalsRevision == beforeVitals + 1 &&
                fixture.Registry.GetPlayerLifeRevision(
                    fixture.Socket.Session) == afterLife &&
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket,
                    observerSocket),
                "a lethal tick cannot replay HP, death, or life advancement");
        }
        finally
        {
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

#if DEBUG
    private static async Task CheckPeriodicLiveTransferredTargetAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Chrysaor");
        var timing = ApplyLivePeriodicBleed(fixture);
        DrainLivePeriodicMonsterSetup(fixture, timing.AppliedAt);
        await DrainMedusaPacketsAsync(fixture.Socket);
        var beforeHealth = ReadLivePeriodicHealth(fixture);
        var beforeVitals = fixture.Character.VitalsRevision;
        var beforeLife = fixture.Registry.GetPlayerLifeRevision(
            fixture.Socket.Session);
        var preparedCalls = 0;
        fixture.Registry.ProtocolCheckAfterMedusaPeriodicLedgerPrepared =
            () =>
            {
                preparedCalls++;
                fixture.Registry.Remove(fixture.Socket.Session);
            };

        try
        {
            await fixture.Registry.AdvanceMonsterWorldOnceAsync(
                timing.DueAt,
                CancellationToken.None);
            Check.True(
                preparedCalls == 1 &&
                ReadLivePeriodicHealth(fixture) == beforeHealth &&
                fixture.Character.VitalsRevision == beforeVitals &&
                !fixture.Registry.TryGetPlayerLifeRevision(
                    fixture.Socket.Session,
                    out _) &&
                beforeLife >= 0 &&
                !fixture.Registry.MedusaPeriodicDamageLedger
                    .TryGetSnapshot(fixture.Runtime.InstanceId, out _) &&
                RequiredOwnership(fixture.Map).Mechanics.Characters
                    .Single(character =>
                        character.CharacterId == fixture.Character.Id)
                    .ActiveEffects.IsEmpty &&
                await RemainedWithoutTrailingMedusaFramesAsync(
                    fixture.Socket),
                "a target transferred after exact preparation terminal-consumes the owner reservation without HP or egress");
        }
        finally
        {
            fixture.Registry.ProtocolCheckAfterMedusaPeriodicLedgerPrepared =
                null;
        }
    }
#endif

    private static LiveBleedTiming ApplyLivePeriodicBleed(
        MonsterPlayerHitFixture fixture)
    {
        var ownership = RequiredOwnership(fixture.Map);
        var source = Binding(ownership, "Chrysaor");
        var appliedAt = ownership.Run.LastObservedAt;
        Check.True(
            fixture.Map.TryCommitOwnerMechanicForInvariantTest(
                fixture.Character.Id,
                source.Identity.ObjectId,
                source.Identity.SpawnGeneration,
                appliedAt,
                out var applied) &&
            applied.MechanicsResult is
            {
                Outcome: MedusaMechanicHitOutcome.Applied,
                Effect: { } effect
            } &&
            effect.NextPeriodicTickAt is { } dueAt,
            "the live periodic fixture applies one authored Bleed");
        var exact = applied.MechanicsResult!.Value.Effect!.Value;
        return new(
            appliedAt,
            exact.NextPeriodicTickAt!.Value,
            exact.ExpiresAt);
    }

    private static MedusaActiveEncounterEffectSnapshot RequiredLiveBleed(
        MonsterPlayerHitFixture fixture) =>
        fixture.Mechanics().ActiveEffects.Single(effect =>
            effect.Definition.Kind == MedusaEncounterEffectKind.Bleed);

    private static void DrainLivePeriodicMonsterSetup(
        MonsterPlayerHitFixture fixture,
        DateTimeOffset now)
    {
        var members = fixture.Map.Snapshot();
        foreach (var member in members)
        {
            lock (member.Character.VitalsSync)
            {
                member.Character.PositionX = 1_000;
                member.Character.PositionZ = 1_000;
            }
        }

        _ = fixture.Runtime.Owner.Invoke(
            map =>
            {
                foreach (var member in members)
                {
                    map.ClearMonsterAggroForCharacter(
                        member.CharacterId,
                        now);
                }
                return true;
            },
            TimeSpan.FromSeconds(3));
        for (var index = 1; index <= 12; index++)
        {
            var tick = fixture.Runtime.Owner.Invoke(
                map => map.AdvanceMonsters(
                    now.AddMilliseconds(index * 100),
                    session => fixture.Registry
                        .GetPlayerLifeRevision(session)),
                TimeSpan.FromSeconds(3));
            if (index > 1 &&
                !tick.PositionsChanged &&
                tick.Updates.Count == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "The live periodic fixture did not reach a quiet monster frame.");
    }

    private static int ReadLivePeriodicHealth(
        MonsterPlayerHitFixture fixture)
    {
        lock (fixture.Character.VitalsSync)
        {
            return fixture.Character.CurrentHp;
        }
    }

    private static bool IsLivePeriodicVitals(
        ReadOnlySpan<byte> packet,
        uint expectedObjectId,
        int currentHealth,
        int currentMana) =>
        packet.Length == 16 &&
        MedusaPacketOpcode(packet) == 0x2771 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]) ==
            expectedObjectId &&
        BinaryPrimitives.ReadInt32LittleEndian(packet[8..]) ==
            currentHealth &&
        BinaryPrimitives.ReadInt32LittleEndian(packet[12..]) == currentMana;

    private static bool IsLivePeriodicDeath(
        ReadOnlySpan<byte> packet,
        uint expectedObjectId,
        uint expectedMapId) =>
        packet.Length == 28 &&
        MedusaPacketOpcode(packet) == MedusaPlayerDeathOpcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]) ==
            expectedObjectId &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet[20..]) ==
            expectedMapId;
}
