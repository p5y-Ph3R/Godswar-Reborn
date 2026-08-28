using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
#if DEBUG
    // No callable production seam: ProtocolChecks may set this private field
    // by reflection to exercise Map recovery from an impossible post-HP proof
    // shape. Release builds contain neither the field nor corruption branch.
    private int _protocolCheckMedusaDecisionFault;
    private Action? _protocolCheckBeforeMedusaOwnerCommit = null;
    private Action? _protocolCheckAfterMedusaTransaction = null;
    private Action? _protocolCheckBeforeMedusaInterruptSubmit = null;
    private Action? _protocolCheckAfterMedusaStatusCapture = null;
    private Action? _protocolCheckAfterMedusaLocalAggregateCapture = null;
    private Action? _protocolCheckAfterMedusaStatusSelfAdmission = null;
#endif

    /// <summary>
    /// Concrete, single-target ECS-vitals authority. Construction is private
    /// to the registry and invocation always uses the captured live adapter;
    /// callers cannot inject a fabricated damage decision.
    /// </summary>
    private sealed class RegistryMedusaCapturedPlayerVitalsCommit
        : MedusaCapturedPlayerVitalsCommit
    {
        private readonly GameSessionRegistry _owner;
        private readonly PlayerVitalsDamageEcsAdapter _adapter;
        private readonly Action? _beforeLethalCommit;
        private readonly Action? _guardedBeforeLethalCommit;
        private PlayerMonsterDamageEcsDecision? _lastDecision;

        internal RegistryMedusaCapturedPlayerVitalsCommit(
            GameSessionRegistry owner,
            PlayerVitalsDamageEcsAdapter adapter,
            ClientSession session,
            GameCharacter character,
            uint playerObjectId,
            long expectedLifeRevision,
            in PlayerMonsterDamageEcsRequest request,
            Action? beforeLethalCommit)
        {
            _owner = owner;
            _adapter = adapter;
            Session = session;
            Character = character;
            PlayerObjectId = playerObjectId;
            ExpectedLifeRevision = expectedLifeRevision;
            Request = request;
            _beforeLethalCommit = beforeLethalCommit;
            _guardedBeforeLethalCommit =
                beforeLethalCommit is null
                    ? null
                    : InvokeGuardedBeforeLethalCommit;
        }

        internal override ClientSession Session { get; }

        internal override GameCharacter Character { get; }

        internal override uint PlayerObjectId { get; }

        internal override long ExpectedLifeRevision { get; }

        internal override PlayerMonsterDamageEcsRequest Request { get; }

        internal override long CurrentLifeRevision =>
            _owner._playerLifeRevisions.TryGetValue(
                Session,
                out var revision)
                ? revision
                : -1;

        internal override PlayerMonsterDamageEcsDecision? LastDecision
            => _lastDecision;

        internal override PlayerMonsterDamageEcsDecision Invoke()
        {
            var decision = _adapter.Apply(
                Character,
                PlayerObjectId,
                ExpectedLifeRevision,
                Request,
                _guardedBeforeLethalCommit);
            if (decision.Applied && decision.Killed)
            {
                var advanced = _owner._playerLifeRevisions.TryUpdate(
                    Session,
                    decision.AfterLifeRevision,
                    ExpectedLifeRevision);
                if (!advanced)
                {
                    _lifeAdvanceAuthorityLost = true;
                }
            }
#if DEBUG
            if (Interlocked.Exchange(
                    ref _owner._protocolCheckMedusaDecisionFault,
                    0) != 0)
            {
                decision = decision with
                {
                    Killed = false,
                    AfterLifeRevision = ExpectedLifeRevision,
                    AfterHealth = 1
                };
            }
#endif
            _lastDecision = decision;

            return decision;
        }

        private bool _lifeAdvanceAuthorityLost;

        internal override bool LifeAdvanceAuthorityLost =>
            _lifeAdvanceAuthorityLost;

        private void InvokeGuardedBeforeLethalCommit()
        {
            _beforeLethalCommit!();
            if (!_owner._playerLifeRevisions.TryGetValue(
                    Session,
                    out var lifeRevision) ||
                lifeRevision != ExpectedLifeRevision)
            {
                throw new InvalidOperationException(
                    "Death interruption changed the captured player life authority.");
            }
        }

    }

    /// <summary>
    /// Commits against an already captured session/character/life authority.
    /// This is invoked from the owning map lane while the caller retains the
    /// registry authority gate. It deliberately performs no session or map
    /// lookup, which avoids an owner-to-registry lock inversion.
    /// </summary>
    private MedusaCapturedPlayerVitalsCommit
        CapturePlayerVitalsDamageEcsCommit(
            ClientSession session,
            GameCharacter character,
            uint playerObjectId,
            long expectedLifeRevision,
            in PlayerMonsterDamageEcsRequest request,
            Action? beforeLethalCommit)
    {
        var capturedRequest = request;
        return new RegistryMedusaCapturedPlayerVitalsCommit(
            this,
            GetPlayerRuntimeEcs(session).IncomingDamage,
            session,
            character,
            playerObjectId,
            expectedLifeRevision,
            capturedRequest,
            beforeLethalCommit);
    }

    private bool IsLegacyMedusaMonsterAttackUnsupported(
        WorldInstanceRuntime runtime) =>
        _playerRuntimeMode != PlayerRuntimeMode.Ecs &&
        InvokeWorldOwner(
            runtime,
            static map => map.HasBoundMedusaEncounter());

    private Func<Task> CaptureDeathInterruption(
        ClientSession session,
        CancellationToken cancellationToken)
    {
        if (!_skillCastInterruptionSinks.TryGetValue(
                session,
                out var sink))
        {
            return static () => Task.CompletedTask;
        }

        // The lookup is completed on the registry lane. The captured callback
        // only claims the already-selected cast generation before lethal HP;
        // it performs no registry or map discovery from the map owner lane.
        return () =>
        {
            try
            {
                return sink(
                    SkillCastInterruptionReason.Death,
                    CancellationToken.None,
                    null) ?? Task.CompletedTask;
            }
            catch (Exception error)
            {
                // Notification failure must never escape the pre-lethal ECS
                // callback after its scheduler has accepted the damage event.
                return Task.FromException(error);
            }
        };
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckBeforeMedusaOwnerCommit()
    {
#if DEBUG
        _protocolCheckBeforeMedusaOwnerCommit?.Invoke();
#endif
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckAfterMedusaTransaction()
    {
#if DEBUG
        _protocolCheckAfterMedusaTransaction?.Invoke();
#endif
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckBeforeMedusaInterruptSubmit()
    {
#if DEBUG
        _protocolCheckBeforeMedusaInterruptSubmit?.Invoke();
#endif
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckAfterMedusaStatusCapture()
    {
#if DEBUG
        _protocolCheckAfterMedusaStatusCapture?.Invoke();
#endif
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckAfterMedusaLocalAggregateCapture()
    {
#if DEBUG
        _protocolCheckAfterMedusaLocalAggregateCapture?.Invoke();
#endif
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckAfterMedusaStatusSelfAdmission()
    {
#if DEBUG
        _protocolCheckAfterMedusaStatusSelfAdmission?.Invoke();
#endif
    }
}
