using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterPlayerDamageEcsLiveAdapterChecks
{
    private static async Task<PoseidonIncomingObservation>
        ObservePoseidonIncomingAsync(
            PlayerRuntimeMode mode,
            uint monsterObjectId)
    {
        var activeAt = DateTimeOffset.UtcNow;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = CreateElementalIncomingRegistry(mode);
        var character = CreateElementalIncomingCharacter(
            monsterObjectId,
            ElementKind.Water,
            pieces: 10,
            currentHealth: 10_000,
            maximumHealth: 10_000,
            currentMana: 0,
            maximumMana: 1_000);
        var playerObjectId = await JoinElementalIncomingFixtureAsync(
            registry,
            socket.Session,
            character,
            monsterObjectId,
            activeAt);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var monster),
            $"{mode} Poseidon monster is queryable");
        var profile = registry.GameplayCatalogs.MonsterCombatProfiles
            .Resolve(monster.Definition);
        var events = FindMonsterHitEventIds(profile, character, count: 5);
        var expectedState = new ElementalResonanceState(character.Id);
        long expectedHealth = character.CurrentHp;
        long expectedMana = character.CurrentMp;
        IncomingResonanceAdjustment fifth = default;
        uint fifthReported = 0;
        for (var index = 0; index < events.Count; index++)
        {
            var eventId = events[index];
            var baseResolution = MonsterIncomingCombatPolicy.ResolveAttack(
                profile,
                character,
                default,
                eventId);
            var combatEvent = IncomingElementalEvent(
                eventId,
                monsterObjectId,
                character,
                activeAt.AddMilliseconds(index));
            var adjustment = ElementalResonanceExecutionPolicy
                .AdjustIncomingDirectDamage(
                    combatEvent,
                    character.ElementalEquipment,
                    expectedState,
                    baseResolution.Damage,
                    expectedHealth,
                    character.MaxHp,
                    character.MaxMp);
            expectedHealth = Math.Max(
                0,
                expectedHealth - adjustment.AdjustedDamage);
            if (expectedHealth > 0)
            {
                expectedHealth = Math.Min(
                    character.MaxHp,
                    expectedHealth + adjustment.GuardHealthRecovery);
                expectedMana = Math.Min(
                    character.MaxMp,
                    expectedMana + adjustment.GuardManaRecovery);
            }

            await registry.ProcessMonsterAttackForSessionAsync(
                socket.Session,
                IncomingMonsterUpdate(
                    monster,
                    character,
                    playerObjectId,
                    registry.GetPlayerLifeRevision(socket.Session),
                    eventId),
                CancellationToken.None);
            await socket.ReadPacketAsync(24);
            var damage = await socket.ReadPacketAsync(30);
            if (index == events.Count - 1)
            {
                fifth = adjustment;
                fifthReported = BinaryPrimitives.ReadUInt32LittleEndian(
                    damage.AsSpan(24, 4));
                var recovery = await socket.ReadPacketAsync(16);
                Check.True(
                    BinaryPrimitives.ReadInt32LittleEndian(
                        recovery.AsSpan(8, 4)) == character.CurrentHp &&
                    BinaryPrimitives.ReadInt32LittleEndian(
                        recovery.AsSpan(12, 4)) == character.CurrentMp,
                    $"{mode} Poseidon recovery publishes final HP/MP");
            }
        }

        Check.True(
            fifth.PoseidonGuardApplied &&
            fifth.GuardHealthRecovery > 0 &&
            fifth.GuardManaRecovery > 0 &&
            fifthReported == fifth.AdjustedDamage &&
            character.CurrentHp == expectedHealth &&
            character.CurrentMp == expectedMana,
            $"{mode} Poseidon fifth-hit guard and recovery commit exactly");
        registry.Remove(socket.Session);
        return new(
            character.CurrentHp,
            character.CurrentMp,
            fifthReported,
            fifth.GuardHealthRecovery,
            fifth.GuardManaRecovery);
    }

    private readonly record struct GaiaIncomingObservation(
        uint PlayerDamage,
        uint ReflectedDamage,
        uint ReportedDamage,
        byte Outcome);

    private readonly record struct PoseidonIncomingObservation(
        int Health,
        int Mana,
        uint FifthReportedDamage,
        long RequestedHealthRecovery,
        long RequestedManaRecovery);

    private readonly record struct AeolusIncomingObservation(
        int HealthBeforeSixth,
        uint SixthReportedDamage,
        byte SixthOutcome);

    private readonly record struct ApolloIncomingObservation(
        int Health,
        uint ReportedDamage,
        byte Outcome);
}
