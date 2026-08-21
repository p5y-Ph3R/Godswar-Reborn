using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static void CheckElementalClientStatusProjection()
    {
        var now = DateTimeOffset.Parse("2026-08-17T06:00:00Z");
        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        var state = new ElementalStatusState(ownerCharacterId: 200);
        var first = BurnApplication(
            sourceCharacterId: 100,
            sourceEventId: 1,
            appliedAt: nowMilliseconds,
            totalDamage: 80);
        Check.True(
            state.TryApply(first),
            "authoritative Burn fixture applies");

        var applied = state.CaptureActive(nowMilliseconds);
        var fourSeconds = ElementalClientStatusProjection.Create(
            applied,
            now);
        var threeSeconds = ElementalClientStatusProjection.Create(
            applied,
            now.AddMilliseconds(1_001));
        Check.True(
            applied.Revision == 1 &&
            applied.ActiveEffects.Count == 1 &&
            applied.ActiveEffects[0].Effect == ElementalEffectKind.Burn &&
            fourSeconds.Effects is [{ StatusId: 40, RemainingSeconds: 4 }] &&
            threeSeconds.Effects is [{ StatusId: 40, RemainingSeconds: 3 }],
            "authoritative Burn projects stock status 40 with actual remaining time");

        var aggregate = ClientStatusAggregate.Empty with
        {
            Hit = 17,
            PhysicalDefense = 23
        };
        var baseline = new PlayerStatusSnapshot(
            [new ClientStatusEffect(204, 600)],
            aggregate,
            "baseline");
        var merged = ElementalClientStatusProjection.Merge(
            baseline,
            fourSeconds);
        Check.True(
            merged.Effects.SequenceEqual(
                new ClientStatusEffect[]
                {
                    new(40, 4),
                    new(204, 600)
                }) &&
            merged.Aggregate == aggregate &&
            merged.Fingerprint.Contains(
                "elemental-client:40:",
                StringComparison.Ordinal),
            "Burn joins the complete sorted snapshot without changing stats");

        var full = new PlayerStatusSnapshot(
            Enumerable.Range(100, 20)
                .Select(static id => new ClientStatusEffect((uint)id, 60))
                .ToArray(),
            aggregate,
            "full");
        var capacity = ElementalClientStatusProjection.Merge(
            full,
            fourSeconds);
        Check.True(
            capacity.Effects.SequenceEqual(full.Effects) &&
            capacity.Aggregate == aggregate,
            "a full status snapshot retains every existing entry");

        var duplicate = ElementalClientStatusProjection.Merge(
            new PlayerStatusSnapshot(
                [new ClientStatusEffect(40, 9)],
                aggregate,
                "duplicate"),
            fourSeconds);
        Check.True(
            duplicate.Effects is [{ StatusId: 40, RemainingSeconds: 9 }],
            "an existing status-40 entry is never duplicated or displaced");

        var weaker = BurnApplication(
            sourceCharacterId: 101,
            sourceEventId: 2,
            appliedAt: nowMilliseconds + 500,
            totalDamage: 40);
        Check.True(
            !state.TryApply(weaker) &&
            state.CaptureActive(nowMilliseconds + 500).Revision == 1,
            "rejected weaker Burn does not advance projection authority");

        var stronger = BurnApplication(
            sourceCharacterId: 102,
            sourceEventId: 3,
            appliedAt: nowMilliseconds + 500,
            totalDamage: 100);
        Check.True(
            state.TryApply(stronger) &&
            state.CaptureActive(nowMilliseconds + 500).Revision == 2 &&
            state.ConsumeRemainingBurn(nowMilliseconds + 500) == 100 &&
            state.CaptureActive(nowMilliseconds + 500) is
                { Revision: 3, ActiveEffects.Count: 0 },
            "stronger replacement and detonation update the same authority");

        var finalTick = new ElementalStatusState(ownerCharacterId: 200);
        Check.True(finalTick.TryApply(first), "final-tick Burn applies");
        var ticks = finalTick.CollectDuePeriodicDamage(
            nowMilliseconds + 4_000);
        Check.True(
            ticks.Count == 4 &&
            finalTick.CaptureActive(nowMilliseconds + 4_000) is
                { Revision: 2, ActiveEffects.Count: 0 },
            "the final Burn tick removes the projected authority");

        var life = new ElementalStatusState(ownerCharacterId: 200);
        Check.True(life.TryApply(first), "life-clear Burn applies");
        life.ClearOnDeath();
        Check.True(
            life.CaptureActive(nowMilliseconds) is
                { Revision: 2, ActiveEffects.Count: 0 },
            "death clears the projected authority");
        Check.True(life.TryApply(first with { SourceEventId = 4 }),
            "post-death Burn can apply with a fresh event");
        life.ClearOnReconnect();
        Check.True(
            life.CaptureActive(nowMilliseconds) is
                { Revision: 4, ActiveEffects.Count: 0 },
            "reconnect clears the projected authority");
    }
}
