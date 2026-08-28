using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private bool TryCaptureMedusaPeriodicDamageTarget(
        WorldInstanceRuntime runtime,
        in MedusaPeriodicDamageIdentity identity,
        IReadOnlyList<GameSessionContext> members,
        out MedusaPeriodicDamageTargetCapture target,
        out IReadOnlyList<MedusaPeriodicDamageRecipientIdentity> recipients,
        out ulong playerEventFloor)
    {
        target = default;
        recipients = [];
        playerEventFloor = 0;
        var exactIdentity = identity;
        lock (_gate)
        {
            var member = members.FirstOrDefault(candidate =>
                candidate.CharacterId ==
                    exactIdentity.TargetCharacterId &&
                candidate.WorldReady);
            if (member is null ||
                !_sessions.TryGetValue(member.Session, out var current) ||
                !ReferenceEquals(member, current) ||
                !MatchesPeriodicTargetRoute(runtime, current, identity) ||
                !_nextPlayerRecoveryAt.ContainsKey(current.CharacterId) ||
                !_playerLifeRevisions.TryGetValue(
                    current.Session,
                    out var lifeRevision) ||
                lifeRevision != identity.TargetLifeRevision)
            {
                return false;
            }

            int currentHealth;
            long vitalsRevision;
            lock (current.Character.VitalsSync)
            {
                currentHealth = current.Character.CurrentHp;
                vitalsRevision = current.Character.VitalsRevision;
            }
            if (currentHealth <= 0)
            {
                return false;
            }

            target = new(
                new(
                    current.WorldInstanceId,
                    current.WorldRevision,
                    current.Ownership,
                    current.CharacterId,
                    current.ObjectId,
                    lifeRevision,
                    vitalsRevision,
                    current.WorldMembershipEpoch),
                currentHealth);
            if (!target.Matches(identity) ||
                !TryCaptureMedusaPeriodicDamageRecipientsLocked(
                    runtime,
                    members,
                    current,
                    lifeRevision,
                    out recipients))
            {
                target = default;
                recipients = [];
                return false;
            }

            playerEventFloor = GetPlayerVitalsDamageEcsDiagnostics(
                current.Session)?.LastAttackEventId ?? 0;
            return true;
        }
    }

    private bool TryCaptureMedusaPeriodicDamageRecipientsLocked(
        WorldInstanceRuntime runtime,
        IReadOnlyList<GameSessionContext> members,
        GameSessionContext target,
        long targetLifeRevision,
        out IReadOnlyList<MedusaPeriodicDamageRecipientIdentity> recipients)
    {
        var captured = new List<MedusaPeriodicDamageRecipientIdentity>(
            Math.Min(
                members.Count,
                MedusaPeriodicDamageLedger.MaximumRecipients));
        foreach (var member in members)
        {
            if (ReferenceEquals(member.Session, target.Session))
            {
                continue;
            }
            if (captured.Count >=
                    MedusaPeriodicDamageLedger.MaximumRecipients - 1 ||
                !_sessions.TryGetValue(member.Session, out var current) ||
                !ReferenceEquals(member, current) ||
                !MatchesPeriodicRecipientRoute(
                    runtime,
                    current,
                    target.WorldRevision) ||
                current.CharacterId == target.CharacterId ||
                !_playerLifeRevisions.TryGetValue(
                    current.Session,
                    out var lifeRevision))
            {
                continue;
            }

            captured.Add(CreatePeriodicRecipient(
                current,
                lifeRevision,
                MedusaPeriodicDamageRecipientVariant.Observer));
        }

        if (captured.Count >=
            MedusaPeriodicDamageLedger.MaximumRecipients)
        {
            recipients = [];
            return false;
        }
        captured.Add(CreatePeriodicRecipient(
            target,
            targetLifeRevision,
            MedusaPeriodicDamageRecipientVariant.Self));
        recipients = captured;
        return true;
    }

    private bool MatchesPeriodicTargetRoute(
        WorldInstanceRuntime runtime,
        GameSessionContext context,
        in MedusaPeriodicDamageIdentity identity) =>
        !context.Session.IsDisconnected &&
        context.WorldReady &&
        context.WorldInstanceId == runtime.InstanceId &&
        context.WorldInstanceId == identity.WorldInstanceId &&
        context.CharacterId == identity.TargetCharacterId &&
        context.Ownership == identity.TargetOwnership &&
        context.WorldMembershipEpoch ==
            identity.TargetWorldMembershipEpoch &&
        context.ObjectId != 0 &&
        IsCurrentAccountSession(
            context.AccountId,
            context.Session,
            context.Ownership);

    private static bool MatchesPeriodicRecipientRoute(
        WorldInstanceRuntime runtime,
        GameSessionContext context,
        long targetWorldRevision) =>
        !context.Session.IsDisconnected &&
        context.WorldReady &&
        context.WorldInstanceId == runtime.InstanceId &&
        context.WorldRevision == targetWorldRevision &&
        context.WorldMembershipEpoch > 0 &&
        context.Ownership.IsValid &&
        context.CharacterId > 0 &&
        context.ObjectId > 0;

    private static MedusaPeriodicDamageRecipientIdentity
        CreatePeriodicRecipient(
            GameSessionContext context,
            long lifeRevision,
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
            lifeRevision,
            variant);

    private static bool TryGetPeriodicSelf(
        MedusaPeriodicDamageLedger ledger,
        MedusaPeriodicDamageLedgerHandle handle,
        int recipientCount,
        out MedusaPeriodicDamageRecipientIdentity self)
    {
        for (var index = 0; index < recipientCount; index++)
        {
            if (ledger.TryGetRecipientIdentity(
                    handle,
                    index,
                    out var candidate) &&
                candidate.Variant ==
                    MedusaPeriodicDamageRecipientVariant.Self)
            {
                self = candidate;
                return true;
            }
        }

        self = default;
        return false;
    }
}
