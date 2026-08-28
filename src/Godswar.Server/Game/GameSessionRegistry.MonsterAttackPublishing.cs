using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly record struct MonsterAttackPublicationRecipient(
        GameSessionContext Context,
        long LifeRevision);

    private enum ExactMonsterAttackAdmissionOutcome : byte
    {
        Admitted,
        AdmittedTerminal,
        CanceledOrStale,
        AdmissionFailed
    }

    private static bool WasMonsterAttackBatchOwned(
        ExactMonsterAttackAdmissionOutcome outcome) => outcome is
        ExactMonsterAttackAdmissionOutcome.Admitted or
        ExactMonsterAttackAdmissionOutcome.AdmittedTerminal;

    private IReadOnlyList<MonsterAttackPublicationRecipient>
        CaptureMonsterAttackPublicationRecipients(
            WorldInstanceRuntime runtime,
        IReadOnlyList<GameSessionContext> members)
    {
        lock (_gate)
        {
            var recipients = new List<MonsterAttackPublicationRecipient>(
                members.Count);
            foreach (var context in members)
            {
                if (context.WorldReady &&
                    context.WorldInstanceId == runtime.InstanceId &&
                    _sessions.TryGetValue(
                        context.Session,
                        out var current) &&
                    ReferenceEquals(current, context) &&
                    _playerLifeRevisions.TryGetValue(
                        context.Session,
                        out var lifeRevision))
                {
                    recipients.Add(new(context, lifeRevision));
                }
            }

            return recipients;
        }
    }

    private Task<bool> TrySendMonsterAttackPacketExactAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext recipient,
        long expectedLifeRevision,
        GameSessionContext eventTarget,
        long expectedTargetLifeRevision,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        string label,
        long? expectedTargetVitalsRevision = null,
        bool requireTargetDead = false) =>
        Task.FromResult(
            WasMonsterAttackBatchOwned(
                TrySendMonsterAttackPacketExactOutcome(
                runtime,
                recipient,
                expectedLifeRevision,
                eventTarget,
                expectedTargetLifeRevision,
                packet,
                cancellationToken,
                label,
                expectedTargetVitalsRevision,
                requireTargetDead)));

    private ExactMonsterAttackAdmissionOutcome
        TrySendMonsterAttackPacketExactOutcome(
        WorldInstanceRuntime runtime,
        GameSessionContext recipient,
        long expectedLifeRevision,
        GameSessionContext eventTarget,
        long expectedTargetLifeRevision,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        string label,
        long? expectedTargetVitalsRevision = null,
        bool requireTargetDead = false)
    {
        Task send = Task.CompletedTask;
        ClientSession? claimedDisconnect = null;
        var admissionFailed = false;
        var admittedTerminal = false;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    recipient,
                    expectedLifeRevision,
                    out recipient) ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    eventTarget,
                    expectedTargetLifeRevision,
                    out eventTarget) ||
                !MatchesMonsterAttackTargetVitalsFence(
                    eventTarget,
                    expectedTargetVitalsRevision,
                    requireTargetDead))
            {
                return ExactMonsterAttackAdmissionOutcome
                    .CanceledOrStale;
            }

            var egressOutcome = recipient.Session
                .TryAdmitExactOutcome(packet, out send);
            if (egressOutcome ==
                ExactEgressAdmissionOutcome.AdmittedTerminal)
            {
                admittedTerminal = true;
                if (recipient.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = recipient.Session;
                }
            }
            else if (egressOutcome !=
                     ExactEgressAdmissionOutcome.Admitted)
            {
                admissionFailed = true;
                if (recipient.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = recipient.Session;
                }
            }
        }

        if (admissionFailed || admittedTerminal)
        {
            if (claimedDisconnect is not null)
            {
                CompleteClaimedExactStatusDisconnect(
                    claimedDisconnect);
            }
            if (admissionFailed)
            {
                return ExactMonsterAttackAdmissionOutcome.AdmissionFailed;
            }
        }

        ObserveExactAdmissionCompletion(
            recipient.Session,
            send,
            label);
        return admittedTerminal
            ? ExactMonsterAttackAdmissionOutcome.AdmittedTerminal
            : ExactMonsterAttackAdmissionOutcome.Admitted;
    }

    private ExactMonsterAttackAdmissionOutcome
        TrySendMonsterAttackPacketBatchExactOutcome(
        WorldInstanceRuntime runtime,
        GameSessionContext recipient,
        long expectedLifeRevision,
        GameSessionContext eventTarget,
        long expectedTargetLifeRevision,
        IReadOnlyList<ReadOnlyMemory<byte>> packets,
        CancellationToken cancellationToken,
        string label,
        long? expectedTargetVitalsRevision = null,
        bool requireTargetDead = false)
    {
        Task send = Task.CompletedTask;
        ClientSession? claimedDisconnect = null;
        var admissionFailed = false;
        var admittedTerminal = false;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    recipient,
                    expectedLifeRevision,
                    out recipient) ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    eventTarget,
                    expectedTargetLifeRevision,
                    out eventTarget) ||
                !MatchesMonsterAttackTargetVitalsFence(
                    eventTarget,
                    expectedTargetVitalsRevision,
                    requireTargetDead))
            {
                return ExactMonsterAttackAdmissionOutcome
                    .CanceledOrStale;
            }

            var egressOutcome = recipient.Session
                .TryAdmitExactBatchOutcome(packets, out send);
            if (egressOutcome ==
                ExactEgressAdmissionOutcome.AdmittedTerminal)
            {
                admittedTerminal = true;
                if (recipient.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = recipient.Session;
                }
            }
            else if (egressOutcome != ExactEgressAdmissionOutcome.Admitted)
            {
                admissionFailed = true;
                if (recipient.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = recipient.Session;
                }
            }
        }

        if (admissionFailed || admittedTerminal)
        {
            if (claimedDisconnect is not null)
            {
                CompleteClaimedExactStatusDisconnect(
                    claimedDisconnect);
            }
            if (admissionFailed)
            {
                return ExactMonsterAttackAdmissionOutcome.AdmissionFailed;
            }
        }

        ObserveExactAdmissionCompletion(
            recipient.Session,
            send,
            label);
        return admittedTerminal
            ? ExactMonsterAttackAdmissionOutcome.AdmittedTerminal
            : ExactMonsterAttackAdmissionOutcome.Admitted;
    }

    private void ObserveExactAdmissionCompletion(
        ClientSession recipient,
        Task completion,
        string label)
    {
        try
        {
            InvokeProtocolCheckBeforeExactAdmissionObservation();
            _ = ObserveExactAdmissionCompletionAsync(
                recipient,
                completion,
                label);
        }
        catch
        {
            // Queue ownership is already final. Completion observation is
            // diagnostic only and must never change the truthful admitted
            // result or invite a caller to replay owned bytes.
        }
    }

    private async Task ObserveExactAdmissionCompletionAsync(
        ClientSession recipient,
        Task completion,
        string label)
    {
        try
        {
            await completion;
        }
        catch (Exception error)
        {
            try
            {
                Console.WriteLine(
                    $"[world] exact egress failed label={label}: " +
                    error.Message);
            }
            catch
            {
            }
        }
    }

    private static bool MatchesMonsterAttackTargetVitalsFence(
        GameSessionContext target,
        long? expectedVitalsRevision,
        bool requireDead)
    {
        if (expectedVitalsRevision is null && !requireDead)
        {
            return true;
        }

        lock (target.Character.VitalsSync)
        {
            return (expectedVitalsRevision is null ||
                    target.Character.VitalsRevision ==
                        expectedVitalsRevision.Value) &&
                   (!requireDead || target.Character.CurrentHp == 0);
        }
    }

    private bool IsExactMonsterAttackPublicationContextLocked(
        WorldInstanceRuntime runtime,
        GameSessionContext expected,
        long expectedLifeRevision) =>
        _sessions.TryGetValue(expected.Session, out var current) &&
        ReferenceEquals(current, expected) &&
        !expected.Session.IsDisconnected &&
        current.WorldReady &&
        current.WorldInstanceId == runtime.InstanceId &&
        current.WorldInstanceId == expected.WorldInstanceId &&
        current.WorldRevision == expected.WorldRevision &&
        current.Ownership == expected.Ownership &&
        current.ObjectId == expected.ObjectId &&
        current.CharacterId == expected.CharacterId &&
        _playerLifeRevisions.TryGetValue(
            expected.Session,
            out var currentLifeRevision) &&
        currentLifeRevision == expectedLifeRevision;

    private bool IsExactMonsterAttackPublicationContext(
        WorldInstanceRuntime runtime,
        GameSessionContext expected,
        long expectedLifeRevision)
    {
        lock (_gate)
        {
            return IsExactMonsterAttackPublicationContextLocked(
                runtime,
                expected,
                expectedLifeRevision);
        }
    }
}
