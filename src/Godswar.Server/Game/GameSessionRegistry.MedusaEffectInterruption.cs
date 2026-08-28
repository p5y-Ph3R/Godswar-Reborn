using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
#if DEBUG
    private Action? _protocolCheckBeforePreparedMedusaNotificationClaim =
        null;
#endif

    private sealed class RegistryMedusaCapturedEffectInterruption
        : MedusaCapturedEffectInterruption
    {
        private readonly GameSessionRegistry _registry;
        private readonly WorldInstanceRuntime _runtime;
        private readonly GameSessionContext _targetContext;
        private readonly IReadOnlyList<MonsterAttackPublicationRecipient>
            _recipients;
        private readonly ClientSession _session;
        private readonly GameCharacter _character;
        private readonly MedusaMonsterPlayerSourceAuthority _source;
        private readonly MedusaMonsterPlayerTargetAuthority _target;
        private readonly PreparedSkillCastInterruption _prepared;
        private PreparedSkillCastInterruptionClaimOutcome _claimOutcome;
        private int _claimed;
        private int _completed;
        private int _notificationAdmitted;

        internal RegistryMedusaCapturedEffectInterruption(
            GameSessionRegistry registry,
            WorldInstanceRuntime runtime,
            GameSessionContext targetContext,
            IReadOnlyList<MonsterAttackPublicationRecipient> recipients,
            ClientSession session,
            GameCharacter character,
            in MedusaMonsterPlayerSourceAuthority source,
            in MedusaMonsterPlayerTargetAuthority target,
            MedusaEncounterEffectKind effectKind,
            PreparedSkillCastInterruption prepared)
        {
            _registry = registry;
            _runtime = runtime;
            _targetContext = targetContext;
            _recipients = recipients;
            _session = session;
            _character = character;
            _source = source;
            _target = target;
            EffectKind = effectKind;
            _prepared = prepared;
        }

        internal override MedusaEncounterEffectKind EffectKind { get; }

        internal bool Claimed => Volatile.Read(ref _claimed) != 0;

        internal bool NotificationAdmitted =>
            Volatile.Read(ref _notificationAdmitted) != 0;

        internal bool RequiresNotification => _claimOutcome is
            PreparedSkillCastInterruptionClaimOutcome
                .InterruptionWon or
            PreparedSkillCastInterruptionClaimOutcome
                .AlreadyInterrupted or
            PreparedSkillCastInterruptionClaimOutcome
                .ClaimFaulted;

        internal override bool Matches(
            ClientSession session,
            GameCharacter character,
            in MedusaMonsterPlayerSourceAuthority source,
            in MedusaMonsterPlayerTargetAuthority target) =>
            ReferenceEquals(_session, session) &&
            ReferenceEquals(_character, character) &&
            _source == source &&
            _target == target;

        private protected override void ClaimCore()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            {
                return;
            }

            _claimOutcome = _prepared.ClaimNonThrowing();
        }

        internal async Task CompleteAfterStatusPublicationAsync(
            string targetName,
            bool mayPublishNotification)
        {
            if (!Claimed)
            {
                return;
            }
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            try
            {
                if (_claimOutcome ==
                    PreparedSkillCastInterruptionClaimOutcome.ClaimFaulted)
                {
                    BestEffortMedusaInterruptionLog(
                        "[medusa-status] cast interruption claim deferred " +
                        $"target={targetName}: synchronous claim faulted");
                    return;
                }

                await _prepared.CompleteAfterStatusPublicationAsync();
                _registry
                    .InvokeProtocolCheckBeforePreparedMedusaNotificationClaim();
                var notificationClaim =
                    _prepared.ClaimNotificationNonThrowing();
                var notificationAdmitted = false;
                if (notificationClaim ==
                    PreparedSkillCastNotificationClaimOutcome.Owner)
                {
                    try
                    {
                        notificationAdmitted =
                            mayPublishNotification &&
                            await _registry
                                .PublishPreparedMedusaCastInterruptionExactAsync(
                                    _runtime,
                                    _targetContext,
                                    _target.LifeRevision,
                                    _recipients);
                    }
                    finally
                    {
                        _prepared
                            .CompleteNotificationAdmissionNonThrowing(
                                notificationAdmitted);
                    }
                }
                else if (notificationClaim ==
                    PreparedSkillCastNotificationClaimOutcome.Delegated)
                {
                    notificationAdmitted =
                        await _prepared
                            .WaitForNotificationAdmissionAsync();
                }
                else if (notificationClaim ==
                    PreparedSkillCastNotificationClaimOutcome.ClaimFaulted)
                {
                    _prepared.CompleteNotificationAdmissionNonThrowing(
                        admitted: false);
                }

                if (notificationAdmitted)
                {
                    Interlocked.Exchange(
                        ref _notificationAdmitted,
                        1);
                }
            }
            catch (Exception error)
            {
                BestEffortMedusaInterruptionLog(
                    "[medusa-status] cast interruption notification " +
                    $"deferred target={targetName}: {error.Message}");
            }
            finally
            {
                // Keep the exact pending generation reserved through native
                // packet submission. A newer cast cannot be cleared by this
                // old generation's delayed 10171.
                _prepared.ReleaseNonThrowing();
            }
        }

    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void
        InvokeProtocolCheckBeforePreparedMedusaNotificationClaim()
    {
#if DEBUG
        _protocolCheckBeforePreparedMedusaNotificationClaim?.Invoke();
#endif
    }

    private async Task<bool>
        PublishPreparedMedusaCastInterruptionExactAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext target,
        long targetLifeRevision,
        IReadOnlyList<MonsterAttackPublicationRecipient> recipients)
    {
        InvokeProtocolCheckBeforeMedusaInterruptSubmit();
        if (!TryRebaseMedusaPublicationContext(
                target,
                targetLifeRevision,
                out target))
        {
            return false;
        }
        if (!await TryAdmitPreparedMedusaCastInterruptionAsync(
                runtime,
                target,
                targetLifeRevision,
                target,
                targetLifeRevision,
                localTarget: true,
                "MedusaSkillCastInterruptedSelf"))
        {
            return false;
        }

        foreach (var recipient in recipients)
        {
            if (ReferenceEquals(recipient.Context.Session, target.Session))
            {
                continue;
            }
            if (!TryRebaseMedusaPublicationContext(
                    recipient.Context,
                    recipient.LifeRevision,
                    out var currentRecipient))
            {
                continue;
            }

            try
            {
                _ = await TryAdmitPreparedMedusaCastInterruptionAsync(
                    runtime,
                    currentRecipient,
                    recipient.LifeRevision,
                    target,
                    targetLifeRevision,
                    localTarget: false,
                    "MedusaSkillCastInterruptedWorld");
            }
            catch (Exception)
            {
                if (TryClaimExactMedusaPublicationPairDisconnect(
                        target,
                        targetLifeRevision,
                        recipient.Context,
                        recipient.LifeRevision,
                        out var claimedRecipient))
                {
                    CompleteClaimedExactStatusDisconnect(
                        claimedRecipient);
                }
            }
        }

        return true;
    }

    private async Task<bool>
        TryAdmitPreparedMedusaCastInterruptionAsync(
            WorldInstanceRuntime runtime,
            GameSessionContext recipient,
            long recipientLifeRevision,
            GameSessionContext target,
            long targetLifeRevision,
            bool localTarget,
            string label)
    {
        if (!TryGetOrCreatePlayerStatusState(
                target.Session,
                out var state))
        {
            return false;
        }

        ClientSession? claimedFailClosedSession = null;
        var admissionClaims = new ExactStatusDisconnectClaims();
        admissionClaims.EnsureCapacity(1);
        await state.Gate.WaitAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!TryRebaseMedusaPublicationContext(
                        target,
                        targetLifeRevision,
                        out target) ||
                    !TryRebaseMedusaPublicationContext(
                        recipient,
                        recipientLifeRevision,
                        out recipient))
                {
                    return false;
                }
                var projectionAt = DateTimeOffset.UtcNow;
                var envelope = ComposeStatusProjectionEnvelopeLocked(
                    target.Session,
                    state,
                    projectionAt);
                InvokeProtocolCheckAfterMedusaStatusCapture();
                if (!ReferenceEquals(envelope.Context, target))
                {
                    if (TryRebaseMedusaPublicationContext(
                            target,
                            targetLifeRevision,
                            out target))
                    {
                        continue;
                    }
                    return false;
                }
                if (!envelope.MedusaOverlay.CanPublish)
                {
                    if (envelope.MedusaOverlay.AuthorityOutcome ==
                        MedusaCharacterEffectAuthorityOutcome
                            .BoundAuthorityUnavailable)
                    {
                        _ = TryClaimExactMedusaMembershipDisconnect(
                            target,
                            targetLifeRevision,
                            out claimedFailClosedSession!);
                        break;
                    }
                    return false;
                }

                var localAggregate = localTarget
                    ? ProjectElementalMovementStatus(
                        target.Session,
                        target.Character,
                        target.Ownership,
                        envelope.Snapshot.Aggregate,
                        projectionAt)
                    : (Godswar.Server.State.ClientStatusAggregate?)null;
                IReadOnlyList<ReadOnlyMemory<byte>> packets = localTarget
                    ?
                    [
                    PacketBuilder.PlayerStatusEffects(
                        target.Character,
                        envelope.Snapshot.Effects,
                        envelope.Snapshot.Aggregate),
                    PacketBuilder.PlayerStatusUpdate(
                        target.Character,
                        localAggregate!.Value),
                    PacketBuilder.SkillCastInterrupt(
                        LocalPlayerObjectId)
                ]
                :
                [
                    PacketBuilder.PlayerStatusEffects(
                        target.Character,
                        target.ObjectId,
                        envelope.Snapshot.Effects,
                        envelope.Snapshot.Aggregate),
                    PacketBuilder.SkillCastInterrupt(
                        target.ObjectId)
                    ];
                if (localTarget)
                {
                    InvokeProtocolCheckAfterMedusaLocalAggregateCapture();
                }
                var admissionOutcome =
                    await TrySendStatusPacketBatchExactAsync(
                    runtime,
                    recipient,
                    recipientLifeRevision,
                    target,
                    targetLifeRevision,
                    envelope.MedusaOverlay,
                    packets,
                    CancellationToken.None,
                    label,
                    completeFence: envelope.CompleteFence with
                    {
                        LocalAggregate = localAggregate
                    },
                    completionStatusGate: state,
                    claimedDisconnects: admissionClaims);
                if (WasAdmitted(admissionOutcome))
                {
                    return true;
                }
                if (RequiresAdmissionFailureDisconnect(
                        admissionOutcome))
                {
                    _ = TryClaimExactMedusaPublicationPairDisconnect(
                        target,
                        targetLifeRevision,
                        localTarget ? target : recipient,
                        localTarget
                            ? targetLifeRevision
                            : recipientLifeRevision,
                        out claimedFailClosedSession!);
                    break;
                }
                if (admissionOutcome ==
                    ExactStatusAdmissionOutcome.AuthorityUnavailable)
                {
                    _ = TryClaimExactMedusaMembershipDisconnect(
                        target,
                        targetLifeRevision,
                        out claimedFailClosedSession!);
                    break;
                }
                if (admissionOutcome is
                    ExactStatusAdmissionOutcome.Canceled or
                    ExactStatusAdmissionOutcome
                        .RecipientOrTargetStale)
                {
                    return false;
                }

                var current = CaptureMedusaClientStatusOverlay(
                    target,
                    DateTimeOffset.UtcNow);
                if (current.AuthorityOutcome ==
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired)
                {
                    if (TryRebaseMedusaPublicationContext(
                            target,
                            targetLifeRevision,
                            out target))
                    {
                        continue;
                    }
                    return false;
                }
                if (!current.CanPublish)
                {
                    _ = TryClaimExactMedusaMembershipDisconnect(
                        target,
                        targetLifeRevision,
                        out claimedFailClosedSession!);
                    break;
                }
            }

            if (claimedFailClosedSession is null)
            {
                _ = TryClaimExactMedusaPublicationPairDisconnect(
                    target,
                    targetLifeRevision,
                    localTarget ? target : recipient,
                    localTarget
                        ? targetLifeRevision
                        : recipientLifeRevision,
                    out claimedFailClosedSession!);
            }
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
        }

        if (claimedFailClosedSession is not null)
        {
            CompleteClaimedExactStatusDisconnect(
                claimedFailClosedSession);
        }
        return false;
    }

    private static void BestEffortMedusaInterruptionLog(string message)
    {
        try
        {
            Console.WriteLine(message);
        }
        catch
        {
            // Diagnostics cannot escape the capability's guaranteed release
            // or bypass the caller's missing-notification fail-close check.
        }
    }

}
