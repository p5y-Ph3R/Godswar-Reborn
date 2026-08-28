using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private readonly record struct PeriodicFoundationReservation(
        MapInstance Map,
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
            Reservation,
        MedusaPeriodicDamageTargetCapture Target);

    private readonly record struct PeriodicFoundationPreparation(
        MapInstance Map,
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
            Reservation,
        MedusaPeriodicDamageTargetCapture Target,
        ulong AttackEventId,
        MedusaPreparedPeriodicDamageOwnerReceipt Receipt,
        IReadOnlyList<MedusaPeriodicDamageRecipientIdentity> Recipients);

    private static PeriodicFoundationReservation
        CreateSimplePeriodicFoundationReservation()
    {
        var map = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var bound = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var bleed = Binding(bound, "Chrysaor");
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                StartedAt.AddSeconds(1),
                out var applied) &&
            applied.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied,
            "periodic ledger fixture applies the authored Bleed effect");

        var dueAt = StartedAt.AddSeconds(3);
        Check.True(
            map.TryObserveMedusaTime(dueAt, out var observed) &&
            observed.MechanicsResult is
            {
                Outcome: MedusaMechanicsClockOutcome
                    .PeriodicDamageRequired,
                PeriodicDamage: { } reservation
            } &&
            reservation.Identity.DueAt == dueAt,
            "periodic ledger fixture retains the exact due owner reservation");
        var exact = observed.MechanicsResult!.Value.PeriodicDamage!;
        var identity = exact.Identity;
        var target = new MedusaPeriodicDamageTargetCapture(
            new(
                identity.WorldInstanceId,
                WorldRevision: 0,
                identity.TargetOwnership,
                identity.TargetCharacterId,
                ObjectId: 1,
                identity.TargetLifeRevision,
                VitalsRevision: 0,
                identity.TargetWorldMembershipEpoch),
            CurrentHealth: 1_000_000);
        Check.True(
            target.Matches(identity) &&
            identity.Damage < target.CurrentHealth,
            "periodic ledger fixture captures a valid nonterminal target");
        return new(map, exact, target);
    }

    private static PeriodicFoundationPreparation
        PrepareSimplePeriodicFoundation(
            ulong attackEventId)
    {
        var prepared = CreateSimplePeriodicFoundationReservation();
        var receipt = PreparePeriodicFoundationOwnerReceipt(
            prepared.Map,
            prepared.Reservation,
            attackEventId,
            MedusaPeriodicDamageOwnerIntent.Applied);
        return new(
            prepared.Map,
            prepared.Reservation,
            prepared.Target,
            attackEventId,
            receipt,
            Array.Empty<MedusaPeriodicDamageRecipientIdentity>());
    }

    private static MedusaPreparedPeriodicDamageOwnerReceipt
        PreparePeriodicFoundationOwnerReceipt(
            MapInstance map,
            MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
                reservation,
            ulong attackEventId,
            MedusaPeriodicDamageOwnerIntent intent,
            MedusaPeriodicDamageReceiptRefreshAuthority?
                refreshAuthority = null)
    {
        Check.True(
            map.TryPrepareMedusaPeriodicDamageOwnerReceipt(
                reservation,
                attackEventId,
                intent,
                refreshAuthority,
                out var prepared) &&
            prepared.IsPrepared &&
            prepared.Receipt is { } receipt,
            "map owner mints an opaque exact periodic receipt");
        return prepared.Receipt!;
    }

    private static MedusaEncounterMechanicsRuntime
        .PeriodicDamageReservation ReserveFixturePeriodicDamage(
            MonsterPlayerHitFixture fixture)
    {
        var source = Binding(
            RequiredOwnership(fixture.Map),
            "Chrysaor");
        var appliedAt = RequiredOwnership(fixture.Map).Run.LastObservedAt;
        MedusaOwnedMechanicHitResult applied = default;
        Check.True(
            fixture.Map.TryCommitOwnerMechanicForInvariantTest(
                fixture.Character.Id,
                source.Identity.ObjectId,
                source.Identity.SpawnGeneration,
                appliedAt,
                out applied) &&
            applied.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied,
            "registry periodic fixture applies Bleed through its map owner");
        var dueAt = applied.MechanicsResult!.Value.Effect!.Value
            .NextPeriodicTickAt!.Value;
        Check.True(
            fixture.Map.TryObserveMedusaTime(dueAt, out var observed) &&
            observed.MechanicsResult?.PeriodicDamage is { } reservation &&
            reservation.Identity.DueAt == dueAt,
            "registry periodic fixture retains the exact due reservation");
        return observed.MechanicsResult!.Value.PeriodicDamage!;
    }

    private static MedusaPeriodicDamageTargetCapture
        CapturePeriodicFoundationTarget(
            MonsterPlayerHitFixture fixture)
    {
        var context = fixture.Map.Snapshot().Single(value =>
            ReferenceEquals(value.Session, fixture.Socket.Session));
        fixture.Context = context;
        int currentHealth;
        long vitalsRevision;
        lock (fixture.Character.VitalsSync)
        {
            currentHealth = fixture.Character.CurrentHp;
            vitalsRevision = fixture.Character.VitalsRevision;
        }
        return new(
            new(
                context.WorldInstanceId,
                context.WorldRevision,
                context.Ownership,
                context.CharacterId,
                context.ObjectId,
                fixture.Registry.GetPlayerLifeRevision(
                    fixture.Socket.Session),
                vitalsRevision,
                context.WorldMembershipEpoch),
            currentHealth);
    }

    private static MedusaPeriodicDamageRecipientIdentity
        CapturePeriodicFoundationRecipient(
            GameSessionRegistry registry,
            GameSessionContext context,
            MedusaPeriodicDamageRecipientVariant variant) =>
        new(
            context,
            context.Session,
            context.WorldInstanceId,
            context.WorldRevision,
            context.WorldMembershipEpoch,
            context.Ownership,
            context.CharacterId,
            context.ObjectId,
            registry.GetPlayerLifeRevision(context.Session),
            variant);

    private static MedusaPeriodicDamageHpCommitEvidence
        PeriodicFoundationHpEvidence(
            in PeriodicFoundationPreparation prepared)
    {
        var beforeHealth = prepared.Target.CurrentHealth;
        var damage = (int)Math.Min(
            (ulong)prepared.Reservation.Identity.Damage,
            (ulong)beforeHealth);
        var afterHealth = beforeHealth - damage;
        var beforeLife = prepared.Reservation.Identity.TargetLifeRevision;
        return new(
            prepared.AttackEventId,
            beforeHealth,
            afterHealth,
            prepared.Target.Authority.VitalsRevision,
            prepared.Target.Authority.VitalsRevision + 1,
            beforeLife,
            afterHealth == 0 ? beforeLife + 1 : beforeLife);
    }

    private static MedusaPeriodicDamageLedgerHandle PrepareLedgerEntry(
        MedusaPeriodicDamageLedger ledger,
        in PeriodicFoundationPreparation prepared,
        MedusaPeriodicDamageLedgerMutationOutcome expected =
            MedusaPeriodicDamageLedgerMutationOutcome.Prepared)
    {
        var outcome = ledger.TryPrepare(
            prepared.Reservation,
            prepared.Target,
            prepared.AttackEventId,
            prepared.Receipt,
            prepared.Recipients,
            out var handle);
        Check.True(
            outcome == expected &&
            (expected != MedusaPeriodicDamageLedgerMutationOutcome.Prepared ||
             handle is not null),
            $"periodic ledger preparation returns {expected} " +
            $"(actual {outcome}, target-match " +
            $"{prepared.Target.Matches(prepared.Reservation.Identity)}, " +
            $"target-life {prepared.Target.Authority.LifeRevision}, " +
            $"identity-life " +
            $"{prepared.Reservation.Identity.TargetLifeRevision}, " +
            $"target-epoch " +
            $"{prepared.Target.Authority.WorldMembershipEpoch}, " +
            $"identity-epoch " +
            $"{prepared.Reservation.Identity.TargetWorldMembershipEpoch}, " +
            $"ownership-match " +
            $"{prepared.Target.Authority.Ownership == prepared.Reservation.Identity.TargetOwnership})");
        return handle!;
    }
}
