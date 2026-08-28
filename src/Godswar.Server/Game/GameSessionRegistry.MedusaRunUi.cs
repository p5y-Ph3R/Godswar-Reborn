using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const ushort MedusaRepetitionIndex = 0;
    private const ushort MedusaRepetitionGroupIndex = 0;
    private const ushort MedusaRepetitionActiveState = 5;
    private static readonly TimeSpan MedusaCompletionExitDelay =
        TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<
        WorldInstanceId,
        MedusaLeaderUiRegistration> _medusaLeaderUi = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _medusaCompletionExitRequested = [];

    internal bool TryRegisterMedusaLeaderUi(
        WorldInstanceId worldInstanceId,
        ClientSession session,
        int leaderCharacterId,
        ushort dailyEntryLimit)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!worldInstanceId.IsValid ||
            leaderCharacterId <= 0 ||
            dailyEntryLimit == 0 ||
            !_sessions.TryGetValue(session, out var context) ||
            context.CharacterId != leaderCharacterId ||
            context.WorldInstanceId != worldInstanceId ||
            !WorldInstances.TryFind(worldInstanceId, out var runtime))
        {
            return false;
        }

        var ownership = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                out var snapshot)
                    ? snapshot
                    : null);
        if (ownership is null ||
            ownership.Run.State != MedusaRunState.Active ||
            !ownership.Run.AdmittedCharacterIds.Contains(
                leaderCharacterId) ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                ownership.ContentMapId.Value,
                out var clientSceneId) ||
            clientSceneId <= 0)
        {
            return false;
        }

        return _medusaLeaderUi.TryAdd(
            worldInstanceId,
            new(
                session,
                leaderCharacterId,
                checked((ushort)clientSceneId),
                dailyEntryLimit));
    }

    internal bool TryEndMedusaRunFromLeader(
        ClientSession session,
        int repetitionId,
        int repetitionIndex,
        DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (repetitionIndex != MedusaRepetitionIndex ||
            !_sessions.TryGetValue(session, out var context) ||
            !_medusaLeaderUi.TryGetValue(
                context.WorldInstanceId,
                out var registration) ||
            !ReferenceEquals(registration.Session, session) ||
            registration.LeaderCharacterId != context.CharacterId ||
            !context.WorldReady ||
            !WorldInstances.TryFind(
                context.WorldInstanceId,
                out var runtime))
        {
            return false;
        }

        var run = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                    out var ownership)
                ? ownership.Run
                : null);
        if (run is { State: MedusaRunState.Completed })
        {
            _medusaCompletionExitRequested.TryAdd(
                context.WorldInstanceId,
                0);
            _medusaLeaderUi.TryRemove(
                new KeyValuePair<
                    WorldInstanceId,
                    MedusaLeaderUiRegistration>(
                    context.WorldInstanceId,
                    registration));
            return true;
        }

        var party = GetPartyMembership(session);
        if (repetitionId != registration.ClientSceneId ||
            party is { IsLeader: false })
        {
            return false;
        }

        var result = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                var routed = map.TryAbandonMedusaRun(
                    context.CharacterId,
                    requestedAt,
                    out var abandoned);
                return (routed, abandoned);
            });
        if (!result.routed ||
            result.abandoned.RunOutcome is not (
                MedusaRunAbandonOutcome.Exited or
                MedusaRunAbandonOutcome.TimedOut or
                MedusaRunAbandonOutcome.RunNotActive))
        {
            return false;
        }

        RequestMedusaTerminationExit(context.WorldInstanceId);
        _medusaLeaderUi.TryRemove(
            new KeyValuePair<WorldInstanceId, MedusaLeaderUiRegistration>(
                context.WorldInstanceId,
                registration));
        return true;
    }

    internal bool TryTerminateMedusaRunFromLeader(
        ClientSession session,
        DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryGetValue(session, out var context) ||
            !_medusaLeaderUi.TryGetValue(
                context.WorldInstanceId,
                out var registration) ||
            !ReferenceEquals(registration.Session, session) ||
            registration.LeaderCharacterId != context.CharacterId)
        {
            return false;
        }

        return TryEndMedusaRunFromLeader(
            session,
            registration.ClientSceneId,
            MedusaRepetitionIndex,
            requestedAt);
    }

    private MedusaLeaderUiDelivery? CaptureMedusaLeaderUiDelivery(
        WorldInstanceRuntime runtime,
        DateTimeOffset now)
    {
        if (!_medusaLeaderUi.TryGetValue(
                runtime.InstanceId,
                out var registration))
        {
            return null;
        }

        var state = InvokeWorldOwner(
            runtime,
            map =>
            {
                var ownership = map.TryGetMedusaOwnershipSnapshot(
                    out var snapshot)
                        ? snapshot
                        : null;
                var leader = map.Snapshot().SingleOrDefault(context =>
                    ReferenceEquals(
                        context.Session,
                        registration.Session) &&
                    context.CharacterId ==
                        registration.LeaderCharacterId);
                return (ownership, leader);
            });
        if (state.ownership is null ||
            state.leader is null ||
            registration.Session.IsDisconnected)
        {
            _medusaLeaderUi.TryRemove(
                new KeyValuePair<
                    WorldInstanceId,
                    MedusaLeaderUiRegistration>(
                    runtime.InstanceId,
                    registration));
            return null;
        }
        if (!state.leader.WorldReady)
        {
            return null;
        }

        var run = state.ownership.Run;
        if (run is
            {
                State: MedusaRunState.Completed,
                CompletionMarker: { } completion
            })
        {
            var completionRemainingSeconds = checked((int)Math.Clamp(
                Math.Ceiling((completion.CompletedAt +
                    MedusaCompletionExitDelay - now).TotalSeconds),
                0d,
                MedusaCompletionExitDelay.TotalSeconds));
            var completionPackets = registration.CaptureCompletion(
                completionRemainingSeconds,
                run.TeamScore);
            return completionPackets.Count == 0
                ? null
                : new(
                    runtime.InstanceId,
                    registration,
                    completionPackets,
                    RemoveAfterSend: false);
        }
        if (run.State != MedusaRunState.Active)
        {
            var terminalAt = run.CompletionMarker?.CompletedAt ?? now;
            var terminalRemainingSeconds = checked((int)Math.Clamp(
                Math.Ceiling((run.Deadline - terminalAt).TotalSeconds),
                0d,
                int.MaxValue));
            var terminalPackets = registration.CaptureTerminal(
                terminalRemainingSeconds,
                run.TeamScore);
            if (terminalPackets.Count == 0)
            {
                _medusaLeaderUi.TryRemove(
                    new KeyValuePair<
                        WorldInstanceId,
                        MedusaLeaderUiRegistration>(
                        runtime.InstanceId,
                        registration));
                return null;
            }
            return new(
                runtime.InstanceId,
                registration,
                terminalPackets,
                RemoveAfterSend: true);
        }

        var remainingSeconds = checked((int)Math.Clamp(
            Math.Ceiling((run.Deadline - now).TotalSeconds),
            0d,
            int.MaxValue));
        var packets = registration.CaptureUpdate(
            remainingSeconds,
            run.TeamScore);
        return packets.Count == 0
            ? null
            : new(
                runtime.InstanceId,
                registration,
                packets,
                RemoveAfterSend: false);
    }

    private async Task PublishMedusaLeaderUiDeliveryAsync(
        MedusaLeaderUiDelivery delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var packet in delivery.Packets)
            {
                await delivery.Registration.Session.SendAsync(
                    packet,
                    cancellationToken,
                    "MedusaLeaderInstancePanel");
            }
        }
        catch (Exception error) when (
            error is IOException or ObjectDisposedException)
        {
            Remove(delivery.Registration.Session);
        }
        finally
        {
            if (delivery.RemoveAfterSend)
            {
                _medusaLeaderUi.TryRemove(
                    new KeyValuePair<
                        WorldInstanceId,
                        MedusaLeaderUiRegistration>(
                        delivery.WorldInstanceId,
                        delivery.Registration));
            }
        }
    }

    private sealed class MedusaLeaderUiRegistration(
        ClientSession session,
        int leaderCharacterId,
        ushort clientSceneId,
        ushort dailyEntryLimit)
    {
        private readonly object _gate = new();
        private bool _synchronized;
        private bool _completionSynchronized;
        private bool _reset;
        private int _lastRemainingSeconds = -1;
        private int _lastTeamScore = -1;

        public ClientSession Session { get; } = session;

        public int LeaderCharacterId { get; } = leaderCharacterId;

        public ushort ClientSceneId { get; } = clientSceneId;

        public ushort DailyEntryLimit { get; } = dailyEntryLimit;

        public IReadOnlyList<byte[]> CaptureUpdate(
            int remainingSeconds,
            int teamScore)
        {
            lock (_gate)
            {
                if (_reset ||
                    _synchronized &&
                    _lastRemainingSeconds == remainingSeconds &&
                    _lastTeamScore == teamScore)
                {
                    return [];
                }

                _lastRemainingSeconds = remainingSeconds;
                _lastTeamScore = teamScore;
                var fight = PacketBuilder.RepetitionFightInfo(
                    remainingSeconds,
                    teamScore);
                if (_synchronized)
                {
                    return [fight];
                }

                _synchronized = true;
                return
                [
                    PacketBuilder.RepetitionSync(
                        ClientSceneId,
                        MedusaRepetitionIndex,
                        MedusaRepetitionGroupIndex,
                        MedusaRepetitionActiveState,
                        DailyEntryLimit),
                    fight
                ];
            }
        }

        public IReadOnlyList<byte[]> CaptureTerminal(
            int remainingSeconds,
            int teamScore)
        {
            lock (_gate)
            {
                if (_reset)
                {
                    return [];
                }

                var packets = new List<byte[]>(3);
                if (!_synchronized)
                {
                    packets.Add(PacketBuilder.RepetitionSync(
                        ClientSceneId,
                        MedusaRepetitionIndex,
                        MedusaRepetitionGroupIndex,
                        MedusaRepetitionActiveState,
                        DailyEntryLimit));
                    _synchronized = true;
                }
                packets.Add(PacketBuilder.RepetitionFightInfo(
                    remainingSeconds,
                    teamScore));
                packets.Add(PacketBuilder.RepetitionReset());
                _lastRemainingSeconds = remainingSeconds;
                _lastTeamScore = teamScore;
                _reset = true;
                return packets;
            }
        }

        public IReadOnlyList<byte[]> CaptureCompletion(
            int remainingSeconds,
            int teamScore)
        {
            lock (_gate)
            {
                if (_reset || _completionSynchronized)
                {
                    return [];
                }

                var packets = new List<byte[]>(5);
                if (!_synchronized)
                {
                    packets.Add(PacketBuilder.RepetitionSync(
                        ClientSceneId,
                        MedusaRepetitionIndex,
                        MedusaRepetitionGroupIndex,
                        MedusaRepetitionActiveState,
                        DailyEntryLimit));
                    _synchronized = true;
                }
                packets.Add(PacketBuilder.RepetitionFightInfo(
                    remainingSeconds,
                    teamScore));
                packets.Add(PacketBuilder.RepetitionPanelCompletion());
                packets.Add(PacketBuilder.RepetitionCompletionState(
                    ClientSceneId,
                    completed: true));
                packets.Add(PacketBuilder.RepetitionCountdown(
                    remainingSeconds));
                _lastRemainingSeconds = remainingSeconds;
                _lastTeamScore = teamScore;
                _completionSynchronized = true;
                return packets;
            }
        }
    }

    private sealed record MedusaLeaderUiDelivery(
        WorldInstanceId WorldInstanceId,
        MedusaLeaderUiRegistration Registration,
        IReadOnlyList<byte[]> Packets,
        bool RemoveAfterSend);
}
