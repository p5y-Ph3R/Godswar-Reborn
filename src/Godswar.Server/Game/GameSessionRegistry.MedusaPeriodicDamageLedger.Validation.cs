using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MedusaPeriodicDamageLedger
{
    private static bool IsExactPreparation(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
            reservation,
        in MedusaPeriodicDamageTargetCapture target,
        ulong attackEventId,
        MedusaPreparedPeriodicDamageOwnerReceipt? ownerReceipt)
    {
        return ownerReceipt?.ActualDisposition is null &&
            MatchesOwnerReceiptTuple(
                reservation,
                target,
                attackEventId,
                ownerReceipt);
    }

    private static bool MatchesOwnerReceiptTuple(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
            reservation,
        in MedusaPeriodicDamageTargetCapture target,
        ulong attackEventId,
        MedusaPreparedPeriodicDamageOwnerReceipt? ownerReceipt)
    {
        if (reservation is null ||
            !reservation.Identity.IsValid ||
            !target.Matches(reservation.Identity) ||
            attackEventId == 0 ||
            ownerReceipt is null ||
            ownerReceipt.Identity != reservation.Identity ||
            ownerReceipt.AttackEventId != attackEventId ||
            !ownerReceipt.MatchesReservation(reservation))
        {
            return false;
        }

        var expectedIntent = reservation.Identity.Damage >=
            (uint)target.CurrentHealth
            ? MedusaPeriodicDamageOwnerIntent.Terminal
            : MedusaPeriodicDamageOwnerIntent.Applied;
        return ownerReceipt.RequestedIntent == expectedIntent;
    }

    private static bool TryCaptureRecipients(
        in MedusaPeriodicDamageIdentity identity,
        in MedusaPeriodicDamageTargetCapture target,
        IReadOnlyList<MedusaPeriodicDamageRecipientIdentity>? source,
        out MedusaPeriodicDamageRecipientIdentity[] recipients)
    {
        recipients = [];
        if (source is null || source.Count > MaximumRecipients)
        {
            return false;
        }

        var captured = new MedusaPeriodicDamageRecipientIdentity[
            source.Count];
        var sawSelf = false;
        for (var index = 0; index < captured.Length; index++)
        {
            var recipient = source[index];
            if (!recipient.IsValid ||
                recipient.WorldInstanceId !=
                    target.Authority.WorldInstanceId ||
                recipient.WorldRevision != target.Authority.WorldRevision ||
                (recipient.Variant ==
                    MedusaPeriodicDamageRecipientVariant.Self &&
                    (sawSelf ||
                     recipient.Ownership != target.Authority.Ownership ||
                     recipient.CharacterId != target.Authority.CharacterId ||
                     recipient.ObjectId != target.Authority.ObjectId ||
                     recipient.LifeRevision != target.Authority.LifeRevision ||
                     recipient.WorldMembershipEpoch !=
                         target.Authority.WorldMembershipEpoch)) ||
                (recipient.Variant ==
                    MedusaPeriodicDamageRecipientVariant.Observer &&
                    recipient.CharacterId == identity.TargetCharacterId) ||
                (sawSelf &&
                    recipient.Variant ==
                        MedusaPeriodicDamageRecipientVariant.Observer))
            {
                return false;
            }
            for (var prior = 0; prior < index; prior++)
            {
                if (ReferenceEquals(
                        captured[prior].Session,
                        recipient.Session))
                {
                    return false;
                }
            }

            captured[index] = recipient;
            sawSelf |= recipient.Variant ==
                MedusaPeriodicDamageRecipientVariant.Self;
        }

        recipients = captured;
        return true;
    }

    private static RecipientEntry[] CreateRecipientEntries(
        MedusaPeriodicDamageLedger owner,
        Entry entry,
        MedusaPeriodicDamageRecipientIdentity[] recipients)
    {
        var entries = new RecipientEntry[recipients.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            entries[index] = new(
                recipients[index],
                new RecipientSettlementObserver(owner, entry));
        }
        return entries;
    }

    private static bool RecipientsMatch(
        RecipientEntry[] retained,
        MedusaPeriodicDamageRecipientIdentity[] supplied)
    {
        if (retained.Length != supplied.Length)
        {
            return false;
        }
        for (var index = 0; index < retained.Length; index++)
        {
            if (!RecipientIdentityMatches(
                    retained[index].Identity,
                    supplied[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool RecipientIdentityMatches(
        in MedusaPeriodicDamageRecipientIdentity left,
        in MedusaPeriodicDamageRecipientIdentity right) =>
        ReferenceEquals(left.Context, right.Context) &&
        ReferenceEquals(left.Session, right.Session) &&
        left.WorldInstanceId == right.WorldInstanceId &&
        left.WorldRevision == right.WorldRevision &&
        left.WorldMembershipEpoch == right.WorldMembershipEpoch &&
        left.Ownership == right.Ownership &&
        left.CharacterId == right.CharacterId &&
        left.ObjectId == right.ObjectId &&
        left.LifeRevision == right.LifeRevision &&
        left.Variant == right.Variant;

    private static bool MatchesHpCommit(
        Entry entry,
        in MedusaPeriodicDamageHpCommitEvidence evidence)
    {
        var expectedDamage = (int)Math.Min(
            (ulong)entry.Identity.Damage,
            (ulong)entry.Target.CurrentHealth);
        return evidence.AttackEventId == entry.AttackEventId &&
            evidence.BeforeHealth == entry.Target.CurrentHealth &&
            evidence.AfterHealth ==
                entry.Target.CurrentHealth - expectedDamage &&
            evidence.BeforeVitalsRevision ==
                entry.Target.Authority.VitalsRevision &&
            evidence.BeforeLifeRevision ==
                entry.Identity.TargetLifeRevision &&
            (evidence.AfterHealth == 0) ==
                (entry.RequestedIntent ==
                    MedusaPeriodicDamageOwnerIntent.Terminal);
    }
}
