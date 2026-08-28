using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckPeriodicLedgerTerminalAndRecipientsAsync()
    {
        await CheckClassifiedPeriodicTerminalAsync();
        await CheckPeriodicRecipientSettlementAsync();
        await CheckPeriodicRecipientContradictionAsync();
    }

    private static async Task CheckClassifiedPeriodicTerminalAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        var reservation = ReserveFixturePeriodicDamage(fixture);
        var target = CapturePeriodicFoundationTarget(fixture);
        const ulong eventId = 1_001;
        var receipt = PreparePeriodicFoundationOwnerReceipt(
            fixture.Map,
            reservation,
            eventId,
            MedusaPeriodicDamageOwnerIntent.Applied);
        var ledger = fixture.Registry.MedusaPeriodicDamageLedger;
        var prepared = new PeriodicFoundationPreparation(
            fixture.Map,
            reservation,
            target,
            eventId,
            receipt,
            Array.Empty<MedusaPeriodicDamageRecipientIdentity>());
        var handle = PrepareLedgerEntry(ledger, prepared);

        MedusaPeriodicDamageOwnerReconcileResult terminal = default;
        Check.True(
            fixture.Registry
                .TryCreateClassifiedMedusaPeriodicDamageTerminalWithoutHpAuthority(
                    handle,
                    out var aliveAuthority) ==
                        MedusaPeriodicDamageLedgerMutationOutcome.Invalid &&
            aliveAuthority is null &&
            fixture.Map.TryCompleteMedusaPeriodicDamageTerminalWithoutHp(
                receipt,
                authority: null,
                out var unclassified) &&
            unclassified.Outcome ==
                MedusaPeriodicDamageDispositionOutcome.ForeignReservation &&
            reservation.State ==
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Pending,
            "an alive Applied reservation cannot become Terminal without registry classification authority");

        fixture.Registry.Remove(fixture.Socket.Session);
        Check.True(
            fixture.Registry
                .TryCreateClassifiedMedusaPeriodicDamageTerminalWithoutHpAuthority(
                    handle,
                    out var terminalAuthority) ==
                        MedusaPeriodicDamageLedgerMutationOutcome.Prepared &&
            terminalAuthority?.Reason ==
                MedusaPeriodicDamageTerminalWithoutHpReason
                    .TargetTransferred &&
            fixture.Map.TryCompleteMedusaPeriodicDamageTerminalWithoutHp(
                receipt,
                terminalAuthority,
                out terminal) &&
            terminal.Outcome ==
                MedusaPeriodicDamageDispositionOutcome.Terminal &&
            terminal.IsAuthoritativeTerminal &&
            reservation.State ==
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Terminal,
            "only registry-gated transferred-target authority permits Applied-to-Terminal consumption without HP");
        Check.True(
            ledger.MarkTerminalWithoutHp(handle, terminal) ==
                MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked &&
            ledger.TryGetSnapshot(
                reservation.Identity.WorldInstanceId,
                out var terminalSnapshot) &&
            terminalSnapshot.Phase ==
                MedusaPeriodicDamageLedgerPhase.OwnerAcked &&
            terminalSnapshot.ActualOwnerDisposition ==
                MedusaPeriodicDamageDispositionOutcome.Terminal &&
            terminalSnapshot.TerminalWithoutHpReason ==
                MedusaPeriodicDamageTerminalWithoutHpReason
                    .TargetTransferred &&
            terminalSnapshot.HpCommit is null,
            "the ledger records the classified no-HP Terminal path separately from post-HP acknowledgement");
    }

    private static async Task CheckPeriodicRecipientSettlementAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        _ = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        var observerContext = fixture.Map.Snapshot().Single(context =>
            ReferenceEquals(context.Session, observerSocket.Session));
        fixture.Context = fixture.Map.Snapshot().Single(context =>
            ReferenceEquals(context.Session, fixture.Socket.Session));

        var reservation = ReserveFixturePeriodicDamage(fixture);
        var target = CapturePeriodicFoundationTarget(fixture);
        const ulong eventId = 1_101;
        var receipt = PreparePeriodicFoundationOwnerReceipt(
            fixture.Map,
            reservation,
            eventId,
            MedusaPeriodicDamageOwnerIntent.Applied);
        MedusaPeriodicDamageRecipientIdentity[] recipients =
        [
            CapturePeriodicFoundationRecipient(
                fixture.Registry,
                observerContext,
                MedusaPeriodicDamageRecipientVariant.Observer),
            CapturePeriodicFoundationRecipient(
                fixture.Registry,
                fixture.Context,
                MedusaPeriodicDamageRecipientVariant.Self)
        ];
        var prepared = new PeriodicFoundationPreparation(
            fixture.Map,
            reservation,
            target,
            eventId,
            receipt,
            recipients);
        var ledger = new MedusaPeriodicDamageLedger(1);
        var handle = PrepareLedgerEntry(ledger, prepared);
        CompletePeriodicFoundationOwnerAck(ledger, prepared, handle);

        Check.True(
            ledger.TryGetNextRecipientSettlement(
                handle,
                out var observerIndex,
                out var observerIdentity,
                out var observerSettlement) &&
            observerIndex == 0 &&
            observerIdentity.Variant ==
                MedusaPeriodicDamageRecipientVariant.Observer,
            "recipient settlement starts with the frozen observer identity");
#if DEBUG
        ledger.ProtocolCheckBeforeRecipientSettlementTransition = () =>
            throw new InvalidOperationException(
                "simulated lost recipient-settlement callback");
#endif
        observerSettlement.MarkSettled(admissionOwned: true);
#if DEBUG
        ledger.ProtocolCheckBeforeRecipientSettlementTransition = null;
#endif
        Check.True(
            ledger.TryGetNextRecipientSettlement(
                handle,
                out var selfIndex,
                out var selfIdentity,
                out var selfSettlement) &&
            selfIndex == 1 &&
            selfIdentity.Variant ==
                MedusaPeriodicDamageRecipientVariant.Self,
            "a sticky observer settlement survives callback loss and advances only to self");
        observerSettlement.MarkSettled(admissionOwned: true);
        Check.True(
            ledger.TryGetNextRecipientSettlement(
                handle,
                out var sameSelfIndex,
                out _,
                out var sameSelfSettlement) &&
            sameSelfIndex == 1 &&
            ReferenceEquals(selfSettlement, sameSelfSettlement),
            "an already-settled observer can never be reacquired for byte replay");
        selfSettlement.MarkSettled(admissionOwned: false);
        Check.True(
            !ledger.TryGetNextRecipientSettlement(
                handle,
                out _,
                out _,
                out _) &&
            ledger.MarkPublished(handle) ==
                MedusaPeriodicDamageLedgerMutationOutcome.Published &&
            ledger.TryGetSnapshot(
                reservation.Identity.WorldInstanceId,
                out var published) &&
            published.RecipientSettlementMask == 0b11 &&
            published.RecipientAdmissionMask == 0b01,
            "publication waits for every recipient while retaining exact admission ownership");

        fixture.Registry.Remove(observerSocket.Session);
    }

    private static async Task CheckPeriodicRecipientContradictionAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        var reservation = ReserveFixturePeriodicDamage(fixture);
        var target = CapturePeriodicFoundationTarget(fixture);
        const ulong eventId = 1_201;
        var receipt = PreparePeriodicFoundationOwnerReceipt(
            fixture.Map,
            reservation,
            eventId,
            MedusaPeriodicDamageOwnerIntent.Applied);
        MedusaPeriodicDamageRecipientIdentity[] recipients =
        [
            CapturePeriodicFoundationRecipient(
                fixture.Registry,
                fixture.Context,
                MedusaPeriodicDamageRecipientVariant.Self)
        ];
        var prepared = new PeriodicFoundationPreparation(
            fixture.Map,
            reservation,
            target,
            eventId,
            receipt,
            recipients);
        var ledger = new MedusaPeriodicDamageLedger(1);
        var handle = PrepareLedgerEntry(ledger, prepared);
        CompletePeriodicFoundationOwnerAck(ledger, prepared, handle);
        Check.True(
            ledger.TryGetNextRecipientSettlement(
                handle,
                out _,
                out _,
                out var settlement),
            "contradiction fixture exposes its next exact recipient");
        settlement.MarkSettled(admissionOwned: true);
        settlement.MarkSettled(admissionOwned: false);
        Check.True(
            ledger.TryGetSnapshot(
                reservation.Identity.WorldInstanceId,
                out var quarantined) &&
            quarantined.Phase ==
                MedusaPeriodicDamageLedgerPhase.PostHpQuarantined &&
            ledger.MarkPublished(handle) ==
                MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase,
            "contradictory recipient ownership is sticky and quarantines publication");
    }

    private static void CompletePeriodicFoundationOwnerAck(
        MedusaPeriodicDamageLedger ledger,
        in PeriodicFoundationPreparation prepared,
        MedusaPeriodicDamageLedgerHandle handle)
    {
        Check.True(
            ledger.TryGetPreparedAttempt(
                handle,
                out _,
                out _,
                out var hpObserver),
            "recipient fixture exposes its prepared HP observer");
        hpObserver.MarkHpCommitted(PeriodicFoundationHpEvidence(prepared));
        Check.True(
            ledger.TryGetOwnerAcknowledgementAuthority(
                handle,
                out var acknowledgement) &&
            prepared.Map.TryReconcileMedusaPeriodicDamageOwnerReceipt(
                prepared.Receipt,
                acknowledgement,
                out var ownerAck) &&
            ownerAck.IsAuthoritativeApplied &&
            ledger.MarkOwnerAcked(
                handle,
                acknowledgement,
                ownerAck) ==
                    MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked,
            "recipient fixture reaches exact owner acknowledgement");
    }
}
