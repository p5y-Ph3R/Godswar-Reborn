using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckPeriodicRegistryLifeAdvanceAsync()
    {
        await using var fixture = await MonsterPlayerHitFixture.CreateAsync(
            "Chrysaor");
        var before = RequiredOwnership(fixture.Map);
        var appliedAt = before.Run.LastObservedAt;
        var bleed = Binding(before, "Chrysaor");
        Check.True(
            fixture.Map.TryCommitOwnerMechanicForInvariantTest(
                fixture.Character.Id,
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                appliedAt,
                out var applied) &&
            applied.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied,
            "registry life fixture applies Bleed through the owner");
        var oldLife = fixture.Registry.TryGetPlayerLifeRevision(
            fixture.Socket.Session,
            out var capturedLife)
            ? capturedLife
            : throw new InvalidOperationException(
                "registry life fixture lost its established life fence");

        var dueAt = appliedAt.AddSeconds(2);
        var advanced = fixture.Registry.AdvancePlayerLifeRevision(
            fixture.Socket.Session,
            dueAt);
        Check.True(
            advanced == oldLife + 1 &&
            fixture.Registry.TryGetPlayerLifeRevision(
                fixture.Socket.Session,
                out var currentLife) &&
            currentLife == advanced,
            "the real registry life API advances exactly once at its causal timestamp");

        Check.True(
            fixture.Map.TryObserveMedusaTime(dueAt, out var observed) &&
            observed.GateOutcome ==
                MedusaOwnedOperationGateOutcome.PeriodicDamageRequired &&
            observed.MechanicsResult?.PeriodicDamage is { } reservation &&
            reservation.Identity.TargetLifeRevision == oldLife &&
            reservation.Identity.TargetWorldMembershipEpoch ==
                fixture.Context.WorldMembershipEpoch &&
            reservation.Identity.DueAt == dueAt,
            "the current bounded checkpoint preserves and reacquires old-life work after life advance");
        var exactReservation = observed.MechanicsResult!.Value
            .PeriodicDamage!;
        Check.True(
            TryCompletePeriodicDamageForProtocolCheck(
                fixture.Map,
                exactReservation,
                terminal: true,
                out var terminal) &&
            terminal == MedusaPeriodicDamageDispositionOutcome.Terminal,
            "the preserved old-life capability remains terminal-reconcilable without HP replay");
        var reconciled = RequiredOwnership(fixture.Map);
        Check.True(
            reconciled.Mechanics.OutstandingPeriodicDamage is null &&
            reconciled.Mechanics.Characters.Single(character =>
                    character.CharacterId == fixture.Character.Id)
                .ActiveEffects.IsEmpty &&
            reconciled.Run.LastObservedAt == dueAt &&
            reconciled.Mechanics.LastObservedAt == dueAt,
            "terminal reconciliation consumes the old-life identity and couples both clocks");

        // This test deliberately does not claim due-first live behavior. The
        // HP integration slice must reserve/drain/retry before life changes,
        // retain Reserved/InvariantFault, and propagate TimedOut terminal
        // status work rather than relying on this post-advance reclamation.
    }
}
