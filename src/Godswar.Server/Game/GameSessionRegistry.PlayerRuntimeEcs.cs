using System.Runtime.CompilerServices;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly PlayerRuntimeMode _playerRuntimeMode =
        PlayerRuntimeMode.Ecs;
    private readonly ConditionalWeakTable<
        ClientSession,
        PlayerRuntimeEcsAdapters> _playerRuntimeEcs = new();

    public GameSessionRegistry(
        IGameStore? store,
        ZodiacEnergyOptions? zodiacEnergyOptions,
        MonsterRuntimeMode monsterRuntimeMode,
        PlayerRuntimeMode playerRuntimeMode)
        : this(store, zodiacEnergyOptions, monsterRuntimeMode)
    {
        if (!Enum.IsDefined(playerRuntimeMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(playerRuntimeMode),
                playerRuntimeMode,
                "Unsupported player runtime mode.");
        }

        _playerRuntimeMode = playerRuntimeMode;
    }

    internal PlayerRuntimeMode PlayerRuntimeMode =>
        _playerRuntimeMode;

    internal PlayerOnlineDurationEcsSnapshot
        GetPlayerOnlineDurationEcsDiagnostics(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerRuntimeEcs.TryGetValue(
            session,
            out var adapters)
            ? adapters.OnlineDuration.Snapshot()
            : default;
    }

    internal PlayerRecoveryEcsDecision?
        GetPlayerRecoveryEcsDiagnostics(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerRuntimeEcs.TryGetValue(
            session,
            out var adapters)
            ? adapters.Recovery.Snapshot()
            : null;
    }

    internal PlayerMonsterDamageEcsDecision?
        GetPlayerVitalsDamageEcsDiagnostics(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerRuntimeEcs.TryGetValue(
            session,
            out var adapters)
            ? adapters.IncomingDamage.Snapshot()
            : null;
    }

    internal PlayerStatusEcsDecision?
        GetPlayerStatusEcsDiagnostics(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerRuntimeEcs.TryGetValue(
            session,
            out var adapters)
            ? adapters.Status.Snapshot()
            : null;
    }

    private PlayerRuntimeEcsAdapters GetPlayerRuntimeEcs(
        ClientSession session) =>
        _playerRuntimeEcs.GetValue(
            session,
            static _ => new PlayerRuntimeEcsAdapters());

    private void RemovePlayerRuntimeEcs(ClientSession session) =>
        _playerRuntimeEcs.Remove(session);

    private void ResetPlayerRecoveryEcs(ClientSession session)
    {
        if (_playerRuntimeEcs.TryGetValue(
                session,
                out var adapters))
        {
            adapters.Recovery.Reset();
        }
    }

    private void ResetPlayerVitalsDamageEcs(
        ClientSession session)
    {
        if (_playerRuntimeEcs.TryGetValue(
                session,
                out var adapters))
        {
            adapters.IncomingDamage.Reset();
        }
    }

    private PlayerStatusEcsDecision EvaluatePlayerStatusEcsLocked(
        ClientSession session,
        PlayerStatusState state,
        GameSessionContext context,
        DateTimeOffset observedAt)
    {
        if (!ReferenceEquals(context.Session, session))
        {
            throw new ArgumentException(
                "The status context belongs to a different session.",
                nameof(context));
        }

        var decision = GetPlayerRuntimeEcs(session).Status.Evaluate(
            context.Character,
            context.ObjectId,
            state.ExperienceBoosts,
            state.RuntimeStatuses.Values,
            observedAt);
        state.RuntimeStatuses.Clear();
        foreach (var status in decision.ActiveRuntimeStatuses)
        {
            state.RuntimeStatuses.Add(status.Kind, status);
        }

        return decision;
    }

    private void ObserveCommittedOnlineDurationEcs(
        ClientSession session,
        int accountId,
        int characterId,
        PlayerOnlineDurationTarget target,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil)
    {
        if (_playerRuntimeMode != PlayerRuntimeMode.Ecs)
        {
            return;
        }

        GetPlayerRuntimeEcs(session).OnlineDuration.ObserveCommitted(
            accountId,
            characterId,
            target,
            onlineFrom,
            onlineUntil);
    }

    private sealed class PlayerRuntimeEcsAdapters
    {
        public PlayerCombatEcsAdapter Combat { get; } = new();

        public PlayerVitalsDamageEcsAdapter IncomingDamage { get; } =
            new();

        public PlayerRecoveryEcsAdapter Recovery { get; } = new();

        public PlayerStatusEcsAdapter Status { get; } = new();

        public PlayerOnlineDurationEcsDiagnostics OnlineDuration { get; } =
            new();
    }
}
