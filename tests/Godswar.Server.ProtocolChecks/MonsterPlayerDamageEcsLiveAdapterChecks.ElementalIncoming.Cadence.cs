using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterPlayerDamageEcsLiveAdapterChecks
{
    private static async Task<AeolusIncomingObservation>
        ObserveAeolusIncomingAsync(
            PlayerRuntimeMode mode,
            uint monsterObjectId)
    {
        var activeAt = DateTimeOffset.UtcNow;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = CreateElementalIncomingRegistry(mode);
        var character = CreateElementalIncomingCharacter(
            monsterObjectId,
            ElementKind.Wind,
            pieces: 10,
            currentHealth: 10_000,
            maximumHealth: 10_000,
            currentMana: 0,
            maximumMana: 1_000);
        SetIncomingElementalProfile(
            character,
            CreateMixedIncomingCadenceProfile());
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
            $"{mode} mixed-cadence monster is queryable");
        var profile = registry.GameplayCatalogs.MonsterCombatProfiles
            .Resolve(monster.Definition);
        var events = FindMissThenMonsterHits(
            profile,
            character,
            hitCount: 11);

        var beforeBaseMiss = character.CurrentHp;
        await registry.ProcessMonsterAttackForSessionAsync(
            socket.Session,
            IncomingMonsterUpdate(
                monster,
                character,
                playerObjectId,
                registry.GetPlayerLifeRevision(socket.Session),
                events.Miss),
            CancellationToken.None);
        await socket.ReadPacketAsync(24);
        var baseMiss = await socket.ReadPacketAsync(30);
        Check.True(
            character.CurrentHp == beforeBaseMiss &&
            baseMiss[29] == (byte)CombatHitOutcome.Miss &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                baseMiss.AsSpan(24, 4)) == uint.MaxValue,
            $"{mode} base miss publishes once without consuming cadence");

        var beforeSixth = 0;
        MonsterRuntimeUpdate sixthUpdate = null!;
        byte sixthOutcome = 0;
        uint sixthReported = 0;
        for (var index = 0; index < events.Hits.Count; index++)
        {
            var eventId = events.Hits[index];
            var baseResolution = MonsterIncomingCombatPolicy.ResolveAttack(
                profile,
                character,
                default,
                eventId);
            if (index == 5)
            {
                beforeSixth = character.CurrentHp;
            }

            var update = IncomingMonsterUpdate(
                monster,
                character,
                playerObjectId,
                registry.GetPlayerLifeRevision(socket.Session),
                eventId);
            if (index == 5)
            {
                sixthUpdate = update;
            }

            await registry.ProcessMonsterAttackForSessionAsync(
                socket.Session,
                update,
                CancellationToken.None);
            await socket.ReadPacketAsync(24);
            var damage = await socket.ReadPacketAsync(30);
            var reported = BinaryPrimitives.ReadUInt32LittleEndian(
                damage.AsSpan(24, 4));
            if (index is 4 or 10)
            {
                Check.Equal(
                    checked((uint)ElementalBasisPointMath.ScaleDown(
                        baseResolution.Damage,
                        2_500)),
                    reported,
                    $"{mode} Poseidon guard follows actual-hit cadence " +
                    $"at hit {index + 1}");
                await socket.ReadPacketAsync(16);
            }
            else if (index == 5)
            {
                sixthOutcome = damage[29];
                sixthReported = reported;
                Check.True(
                    character.CurrentHp == beforeSixth &&
                    sixthOutcome == (byte)CombatHitOutcome.Miss &&
                    sixthReported == uint.MaxValue,
                    $"{mode} Aeolus sixth actual hit becomes a miss");
            }
            else if (index == 9)
            {
                Check.Equal(
                    baseResolution.Damage,
                    reported,
                    $"{mode} Aeolus-evaded hit does not advance Poseidon");
            }
        }

        var afterSequence = character.CurrentHp;
        await registry.ProcessMonsterAttackForSessionAsync(
            socket.Session,
            sixthUpdate,
            CancellationToken.None);
        Check.True(
            character.CurrentHp == afterSequence && socket.Available == 0,
            $"{mode} cadence replay is side-effect free");
        registry.Remove(socket.Session);
        return new(beforeSixth, sixthReported, sixthOutcome);
    }

    private static (ulong Miss, IReadOnlyList<ulong> Hits)
        FindMissThenMonsterHits(
            in MonsterCombatProfile profile,
            GameCharacter character,
            int hitCount)
    {
        ulong miss = 0;
        var hits = new List<ulong>(hitCount);
        for (ulong eventId = 1;
             eventId <= 100_000 && hits.Count < hitCount;
             eventId++)
        {
            var hit = MonsterIncomingCombatPolicy.ResolveAttack(
                profile,
                character,
                default,
                eventId).Hit;
            if (miss == 0)
            {
                if (!hit)
                {
                    miss = eventId;
                }

                continue;
            }

            if (hit)
            {
                hits.Add(eventId);
            }
        }

        return miss > 0 && hits.Count == hitCount
            ? (miss, hits.AsReadOnly())
            : throw new InvalidOperationException(
                "No ordered miss-plus-hit cadence fixture was found.");
    }

    private static ElementalEquipmentProfile
        CreateMixedIncomingCadenceProfile()
    {
        var totals = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            static _ => default(ElementalEffectTotals));
        var counts = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            static _ => 0);
        counts[ElementKind.Water] = 10;
        counts[ElementKind.Wind] = 10;
        var active = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            value => ElementalResonanceCatalog.ActiveFor(
                value,
                counts[value]));
        return new(totals, counts, active);
    }
}
