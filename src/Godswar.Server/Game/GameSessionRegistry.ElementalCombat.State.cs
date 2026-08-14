using System.Collections.Concurrent;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<
        ClientSession,
        ElementalCombatSessionState> _elementalCombatSessions = [];

    internal bool HasElementalCombatSession(ClientSession session) =>
        _elementalCombatSessions.ContainsKey(session);

    private bool TryGetElementalCombatSession(
        ClientSession session,
        ElementalCombatSessionFence fence,
        out ElementalCombatSessionState state)
    {
        ArgumentNullException.ThrowIfNull(session);
        state = null!;
        if (!fence.IsValid)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                context.CharacterId != fence.CharacterId ||
                context.MapId != fence.MapId ||
                context.Ownership != fence.Ownership ||
                !IsCurrentAccountSession(
                    context.AccountId,
                    session,
                    fence.Ownership))
            {
                return false;
            }

            state = GetOrCreateElementalCombatSession(
                session,
                new ElementalCombatSessionIdentity(
                    fence.CharacterId,
                    fence.MapId,
                    context.WorldInstanceId,
                    fence.Ownership));
            return true;
        }
    }

    private ElementalCombatSessionState GetOrCreateElementalCombatSession(
        ClientSession session,
        ElementalCombatSessionIdentity identity) =>
        _elementalCombatSessions.AddOrUpdate(
            session,
            _ => new ElementalCombatSessionState(identity),
            (_, existing) =>
            {
                if (existing.Identity == identity)
                {
                    return existing;
                }

                lock (existing.Gate)
                {
                    existing.RequestReconnectClear();
                }

                return new ElementalCombatSessionState(identity);
            });

    private void RemoveElementalCombatSession(ClientSession session)
    {
        if (_elementalCombatSessions.TryRemove(session, out var state))
        {
            lock (state.Gate)
            {
                state.RequestReconnectClear();
            }
        }
    }

    private void ClearElementalCombatLifeState(ClientSession session)
    {
        if (_elementalCombatSessions.TryGetValue(session, out var state))
        {
            lock (state.Gate)
            {
                state.RequestDeathClear();
            }
        }
    }

    internal readonly record struct ElementalCombatSessionIdentity(
        int CharacterId,
        byte MapId,
        WorldInstanceId WorldInstanceId,
        Godswar.Server.Application.Characters.PlayerOwnershipFence Ownership);

    internal sealed class ElementalCombatSessionState(
        ElementalCombatSessionIdentity identity)
    {
        private int _activeCommitLeases;
        private ElementalSessionClearKind _pendingClear;

        public ElementalCombatSessionIdentity Identity { get; } = identity;

        public object Gate { get; } = new();

        public ElementalStatusState Statuses { get; } =
            new(identity.CharacterId);

        public ElementalResonanceState Resonance { get; } =
            new(identity.CharacterId);

        public long RecoveryRevision { get; private set; }

        public long AcceptRecoveryPulse() =>
            RecoveryRevision = checked(RecoveryRevision + 1);

        public void ResetRecoveryRevision() => RecoveryRevision = 0;

        public void AcquireCommitLease() =>
            _activeCommitLeases = checked(_activeCommitLeases + 1);

        public void ReleaseCommitLease()
        {
            if (_activeCommitLeases <= 0)
            {
                throw new InvalidOperationException(
                    "Elemental commit lease underflow.");
            }

            _activeCommitLeases--;
            if (_activeCommitLeases == 0)
            {
                ApplyPendingClear();
            }
        }

        public void RequestDeathClear() =>
            RequestClear(ElementalSessionClearKind.Death);

        public void RequestReconnectClear() =>
            RequestClear(ElementalSessionClearKind.Reconnect);

        private void RequestClear(ElementalSessionClearKind kind)
        {
            if (_activeCommitLeases > 0)
            {
                _pendingClear = (ElementalSessionClearKind)Math.Max(
                    (byte)_pendingClear,
                    (byte)kind);
                return;
            }

            ApplyClear(kind);
        }

        private void ApplyPendingClear()
        {
            var pending = _pendingClear;
            _pendingClear = ElementalSessionClearKind.None;
            ApplyClear(pending);
        }

        private void ApplyClear(ElementalSessionClearKind kind)
        {
            if (kind == ElementalSessionClearKind.Reconnect)
            {
                Statuses.ClearOnReconnect();
                Resonance.ClearOnReconnect();
            }
            else if (kind == ElementalSessionClearKind.Death)
            {
                Statuses.ClearOnDeath();
                Resonance.ClearOnDeath();
            }

            if (kind != ElementalSessionClearKind.None)
            {
                ResetRecoveryRevision();
            }
        }
    }

    private enum ElementalSessionClearKind : byte
    {
        None,
        Death,
        Reconnect
    }
}
