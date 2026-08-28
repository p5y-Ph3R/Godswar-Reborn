using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckMedusaClientStatusProjectionPolicy()
    {
        var now = new DateTimeOffset(
            2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var ownership = new PlayerOwnershipFence(
            new Guid("87f32a7b-c10c-4c64-a0f4-58e5f4f22e66"),
            Generation: 7);
        var target = new MedusaClientStatusTargetFence(
            new WorldInstanceId(
                new Guid("1472613f-f228-441f-a643-e9149f476dbc")),
            WorldRevision: 19,
            ownership,
            CharacterId: 101,
            ObjectId: 0x2222,
            LifeRevision: 11,
            WorldMembershipEpoch: 29);
        var effects = new[]
        {
            ProjectionEffect("E1-Elite", ownership, 11, 1, now),
            ProjectionEffect("E5-Elite", ownership, 11, 2, now),
            ProjectionEffect("Euryale", ownership, 11, 3, now),
            ProjectionEffect(
                "Final-Pikeman-1", ownership, 11, 4, now),
            ProjectionEffect(
                "Final-Axeman-1", ownership, 11, 5, now)
        };
        var overlay = ProjectionOverlay(
            target,
            effects,
            now);

        Check.True(
            overlay.Presentations.Select(item =>
                    item.Presentation.Effect.StatusId)
                .SequenceEqual(new uint[] { 330, 402, 401, 236, 235 }) &&
            overlay.Presentations.All(item =>
                item.Presentation.Effect.RemainingSeconds >= 1),
            "all five live Medusa mechanics project their native status IDs with positive exclusive durations");

        var exactImpactEffect = effects[0];
        Check.True(
            GameSessionRegistry.ResolveMedusaMonsterImpactSkillIdForEffect(
                targetMapId: 200,
                target.Ownership,
                target.LifeRevision,
                target.WorldMembershipEpoch,
                exactImpactEffect.SourceObjectId,
                exactImpactEffect.SourceSpawnGeneration,
                exactImpactEffect) == 2002 &&
            GameSessionRegistry.ResolveMedusaMonsterImpactSkillIdForEffect(
                targetMapId: 200,
                target.Ownership,
                target.LifeRevision,
                target.WorldMembershipEpoch + 1,
                exactImpactEffect.SourceObjectId,
                exactImpactEffect.SourceSpawnGeneration,
                exactImpactEffect) == 2000,
            "native impact presentation requires the exact effect membership " +
            "epoch; an ABA mismatch falls back to generic skill 2000");

        var oneTick = ProjectionEffect(
            "E1-Elite",
            ownership,
            11,
            6,
            now,
            expiresAt: now.AddTicks(1));
        var minOne = ProjectionOverlay(target, [oneTick], now);
        var expired = ProjectionOverlay(
            target,
            [oneTick with { ExpiresAt = now }],
            now);
        Check.True(
            minOne.Presentations.Single().Presentation.Effect
                .RemainingSeconds == 1 &&
            expired.Presentations.Count == 0,
            "projection rounds a positive fraction up to one second and excludes the exact expiry instant");

        var baselinePresentations = Enumerable.Range(0, 12)
            .Select(index => new ClientStatusPresentation(
                new ClientStatusEffect(
                    checked((uint)(100 + index)),
                    60),
                Beneficial: true,
                Priority: 100 - index,
                ClientStatusPresentationClass.AuthoritativeBaseline))
            .Concat(Enumerable.Range(0, 9).Select(index =>
                new ClientStatusPresentation(
                    new ClientStatusEffect(
                        checked((uint)(200 + index)),
                        60),
                    Beneficial: false,
                    Priority: 50 - index,
                    ClientStatusPresentationClass
                        .AuthoritativeBaseline)))
            .Append(new ClientStatusPresentation(
                new ClientStatusEffect(330, 99),
                Beneficial: false,
                Priority: 1,
                ClientStatusPresentationClass.AuthoritativeBaseline))
            .Append(new ClientStatusPresentation(
                new ClientStatusEffect(
                    ElementalClientStatusProjection.BurnStatusId,
                    9),
                Beneficial: false,
                Priority: 1,
                ClientStatusPresentationClass.DisplayOnly))
            .ToArray();
        var aggregate = ClientStatusAggregate.Empty with
        {
            Hit = 77,
            PhysicalDefense = 88,
            MagicDefense = 99
        };
        var fullControl =
            HostileStatusControlFlags.HaltIntonate |
            HostileStatusControlFlags.NonMoving |
            HostileStatusControlFlags.NonMagicUsing |
            HostileStatusControlFlags.NonTechniqueUsing |
            HostileStatusControlFlags.NonAttackUsing |
            HostileStatusControlFlags.NonItemUsing;
        var baseline = new PlayerStatusSnapshot(
            [],
            aggregate,
            "capacity-baseline")
        {
            Presentations = baselinePresentations
        };
        var merged = MedusaClientStatusProjection.Merge(
            baseline,
            overlay);
        var selectedIds = merged.Effects
            .Select(static effect => effect.StatusId)
            .ToArray();
        var beneficialSelected = selectedIds.Count(id =>
            id is 235 or 236 || id is >= 100 and <= 111);
        Check.True(
            merged.Effects.Count ==
                PlayerStatusComposer.MaximumTotalStatuses &&
            beneficialSelected ==
                PlayerStatusComposer.MaximumBeneficialStatuses &&
            new uint[] { 330, 402, 401, 236, 235 }
                .All(selectedIds.Contains) &&
            selectedIds.Count(static id => id == 330) == 1 &&
            !selectedIds.Contains(
                ElementalClientStatusProjection.BurnStatusId) &&
            merged.Aggregate == aggregate with
            {
                Control = fullControl
            } &&
            merged.Presentations.Count ==
                baselinePresentations.Length +
                overlay.Presentations.Count,
            "capacity keeps controls then amps, enforces 20/10, deduplicates IDs, drops Burn last, and projects native action controls");

        var appliedAgain = PlayerStatusCapacityPolicy.Apply(merged);
        var mergedAgain = MedusaClientStatusProjection.Merge(
            merged,
            overlay);
        var reversed = MedusaClientStatusProjection.Merge(
            baseline with
            {
                Presentations = baselinePresentations.Reverse().ToArray()
            },
            overlay);
        Check.True(
            appliedAgain.Fingerprint == merged.Fingerprint &&
            appliedAgain.Effects.SequenceEqual(merged.Effects) &&
            appliedAgain.Presentations.Count ==
                merged.Presentations.Count &&
            mergedAgain.Fingerprint == merged.Fingerprint &&
            mergedAgain.Effects.SequenceEqual(merged.Effects) &&
            mergedAgain.Presentations.Count ==
                merged.Presentations.Count &&
            reversed.Effects.SequenceEqual(merged.Effects),
            "capacity Apply and Medusa Merge are fingerprint-idempotent and stable across source ordering");

        var refreshed = oneTick with
        {
            ApplicationSequence = 7,
            ExpiresAt = now.AddSeconds(2)
        };
        var refreshOverlay = ProjectionOverlay(
            target,
            [refreshed],
            now);
        var refreshedSnapshot = MedusaClientStatusProjection.Merge(
            MedusaClientStatusProjection.Merge(baseline, minOne),
            refreshOverlay);
        Check.True(
            refreshedSnapshot.Presentations.Count(presentation =>
                presentation.Source ==
                    ClientStatusPresentationSource.Medusa) == 1 &&
            refreshedSnapshot.Effects.Single(effect =>
                effect.StatusId == 330).RemainingSeconds == 2,
            "a refreshed sequence replaces its earlier Medusa layer without stale-timer resurrection");

        var bleed = ProjectionEffect(
            "E9-Elite",
            ownership,
            11,
            8,
            now);
        Check.True(
            bleed.Definition.ClientProjection.Mode ==
                MedusaEncounterClientProjectionMode
                    .CompatibilityUnresolved &&
            bleed.Definition.ClientProjection.EmittableStatusId is null &&
            ProjectionOverlay(target, [bleed], now)
                .Presentations.Count == 0,
            "Bleed stock authorship remains evidence-only while native " +
            "periodic-HP reconciliation is uncertified");
    }

    private static MedusaClientStatusOverlay ProjectionOverlay(
        in MedusaClientStatusTargetFence target,
        IReadOnlyCollection<MedusaActiveEncounterEffectSnapshot> effects,
        DateTimeOffset now)
    {
        var view = new MedusaActiveCharacterEffectView(
            target.CharacterId,
            new MedusaEncounterEffectTarget(
                target.Ownership,
                target.LifeRevision,
                target.WorldMembershipEpoch),
            now,
            now.AddMinutes(10),
            MedusaEncounterControlRestriction.None,
            PhysicalOutgoingDamageMultiplier: 1,
            MagicalOutgoingDamageMultiplier: 1,
            effects.ToImmutableArray());
        return MedusaClientStatusProjection.Create(
            target,
            new MedusaCharacterEffectAuthorityResult(
                MedusaCharacterEffectAuthorityOutcome.ResolvedActive,
                view),
            now);
    }

    private static MedusaActiveEncounterEffectSnapshot ProjectionEffect(
        string spawnId,
        PlayerOwnershipFence ownership,
        long lifeRevision,
        ulong sequence,
        DateTimeOffset appliedAt,
        DateTimeOffset? expiresAt = null)
    {
        var spawn = MedusaIslandRosterPolicy.Spawns.Single(value =>
            value.SpawnId == spawnId);
        var skill = spawn.Skill ??
            throw new InvalidOperationException(
                $"{spawnId} has no authored mechanic.");
        Check.True(
            MedusaEncounterMechanicsPolicy.TryGetEffectDefinition(
                skill.Mechanic,
                contentMapId: 200,
                out var definition),
            $"{spawnId} resolves a projection definition");
        var objectId = checked((uint)(70_000 + sequence));
        return new(
            definition,
            ownership,
            lifeRevision,
            spawnId,
            objectId,
            SourceSpawnGeneration: 1,
            sequence,
            appliedAt,
            expiresAt ?? appliedAt.Add(definition.Duration),
            NextPeriodicTickAt: null,
            EmittedPeriodicTicks: 0,
            TargetWorldMembershipEpoch: 29);
    }
}
