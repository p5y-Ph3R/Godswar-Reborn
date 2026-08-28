using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _medusaTerminationExitRequested = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _medusaTerminationEgressInFlight = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _medusaTerminationExitSettled = [];

    private void RequestMedusaTerminationExit(
        WorldInstanceId worldInstanceId) =>
        _medusaTerminationExitRequested.TryAdd(worldInstanceId, 0);

    private MedusaTerminationEgress? CaptureMedusaTerminationEgress(
        WorldInstanceRuntime runtime)
    {
        if (!_medusaTerminationExitRequested.ContainsKey(
                runtime.InstanceId) ||
            _medusaTerminationExitSettled.ContainsKey(runtime.InstanceId))
        {
            return null;
        }

        var state = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                    out var ownership)
                ? ownership.Run.State
                : (MedusaRunState?)null);
        if (state is not (
                MedusaRunState.TimedOut or
                MedusaRunState.VoluntarilyExited))
        {
            return null;
        }

        var members = new List<MedusaTerminationEgressMember>();
        lock (_gate)
        {
            foreach (var context in _sessions.Values)
            {
                if (!context.WorldReady ||
                    context.Session.IsDisconnected ||
                    context.WorldInstanceId != runtime.InstanceId ||
                    context.Character.CurrentMap != context.MapId ||
                    context.MapId is not (200 or 204) ||
                    !context.Ownership.IsValid ||
                    !IsCurrentAccountSession(
                        context.AccountId,
                        context.Session,
                        context.Ownership))
                {
                    continue;
                }

                members.Add(new(
                    context.Session,
                    context.CharacterId,
                    context.WorldInstanceId,
                    context.MapId,
                    context.Ownership,
                    context.Character.Camp));
            }
        }

        return new(runtime.InstanceId, members);
    }

    private async Task PublishMedusaTerminationEgressAsync(
        MedusaTerminationEgress egress,
        CancellationToken cancellationToken)
    {
        if (!_medusaTerminationEgressInFlight.TryAdd(
                egress.SourceWorldInstanceId,
                0))
        {
            return;
        }

        try
        {
            var allTransferred = true;
            foreach (var member in egress.Members)
            {
                var targetMapId = member.Camp == GameDefaults.SpartaCamp
                    ? GameDefaults.SpartaCapitalMap
                    : GameDefaults.AthensCapitalMap;
                try
                {
                    var target = GetOrCreateDefaultWorldInstance(
                        targetMapId);
                    var command = new MedusaInstanceTransitionCommand(
                        member.CharacterId,
                        member.SourceWorldInstanceId,
                        member.SourceMapId,
                        member.Ownership,
                        target.InstanceId,
                        targetMapId,
                        GameDefaults.StartingPositionX,
                        GameDefaults.StartingPositionZ);
                    if (!await TransitionPartyMemberToInstanceAsync(
                            member.Session,
                            command,
                            cancellationToken))
                    {
                        allTransferred = false;
                        Console.WriteLine(
                            "[instance] Medusa termination egress will " +
                            $"retry character={member.CharacterId} " +
                            $"instance={egress.SourceWorldInstanceId}");
                    }
                }
                catch (Exception error) when (
                    error is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
                {
                    allTransferred = false;
                    Console.WriteLine(
                        "[instance] Medusa termination egress failed " +
                        $"character={member.CharacterId}: " +
                        error.Message);
                }
            }

            if (allTransferred)
            {
                _medusaTerminationExitSettled.TryAdd(
                    egress.SourceWorldInstanceId,
                    0);
                _medusaTerminationExitRequested.TryRemove(
                    egress.SourceWorldInstanceId,
                    out _);
            }
        }
        finally
        {
            _medusaTerminationEgressInFlight.TryRemove(
                egress.SourceWorldInstanceId,
                out _);
        }
    }

    private sealed record MedusaTerminationEgress(
        WorldInstanceId SourceWorldInstanceId,
        IReadOnlyList<MedusaTerminationEgressMember> Members);

    private readonly record struct MedusaTerminationEgressMember(
        ClientSession Session,
        int CharacterId,
        WorldInstanceId SourceWorldInstanceId,
        byte SourceMapId,
        PlayerOwnershipFence Ownership,
        byte Camp);
}
