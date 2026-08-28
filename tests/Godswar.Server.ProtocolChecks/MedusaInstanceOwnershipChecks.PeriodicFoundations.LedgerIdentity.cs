using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckPeriodicFoundationIdentityValueSemantics()
    {
        var baseline = new MedusaPeriodicDamageIdentity(
            new WorldInstanceId(Guid.NewGuid()),
            TargetCharacterId: 101,
            new PlayerOwnershipFence(Guid.NewGuid(), Generation: 7),
            TargetLifeRevision: 11,
            TargetWorldMembershipEpoch: 13,
            SourceRosterSpawnId: "Chrysaor",
            SourceObjectId: 0x2201,
            SourceSpawnGeneration: 17,
            ApplicationSequence: 19,
            TickNumber: 2,
            DueAt: StartedAt.AddSeconds(3),
            MedusaPeriodicDamageKind.DirectHealthLoss,
            Damage: 300);
        var variants = new[]
        {
            baseline with { WorldInstanceId = WorldInstanceId.New() },
            baseline with { TargetCharacterId = 102 },
            baseline with
            {
                TargetOwnership = baseline.TargetOwnership with
                {
                    OwnerId = Guid.NewGuid()
                }
            },
            baseline with
            {
                TargetOwnership = baseline.TargetOwnership with
                {
                    Generation = baseline.TargetOwnership.Generation + 1
                }
            },
            baseline with
            {
                TargetLifeRevision = baseline.TargetLifeRevision + 1
            },
            baseline with
            {
                TargetWorldMembershipEpoch =
                    baseline.TargetWorldMembershipEpoch + 1
            },
            baseline with { SourceRosterSpawnId = "Chrysaor-2" },
            baseline with { SourceObjectId = baseline.SourceObjectId + 1 },
            baseline with
            {
                SourceSpawnGeneration =
                    baseline.SourceSpawnGeneration + 1
            },
            baseline with
            {
                ApplicationSequence = baseline.ApplicationSequence + 1
            },
            baseline with { TickNumber = baseline.TickNumber + 1 },
            baseline with { DueAt = baseline.DueAt.AddTicks(1) },
            baseline with { DamageKind = (MedusaPeriodicDamageKind)2 },
            baseline with { Damage = baseline.Damage + 1 }
        };
        var exactIdentities = new HashSet<MedusaPeriodicDamageIdentity>
        {
            baseline
        };
        foreach (var variant in variants)
        {
            exactIdentities.Add(variant);
        }

        Check.True(
            baseline.IsValid &&
            variants.All(value => value != baseline) &&
            exactIdentities.Count == variants.Length + 1,
            "periodic identity equality retains every world, ownership, life, source, application, tick, time, kind, and damage component");
    }
}
