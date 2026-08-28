using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
#if DEBUG
    internal bool ProtocolCheckMutateRuntimeStatusWhileGateHeld(
        ClientSession session,
        int ordinal,
        DateTimeOffset now)
    {
        if (!_playerStatusStates.TryGetValue(session, out var state) ||
            state.Gate.CurrentCount != 0)
        {
            return false;
        }

        var kind = checked(80_000 + ordinal);
        state.RuntimeStatuses[kind] = new ActiveRuntimeStatus(
            StatusId: checked((uint)(60_000 + ordinal)),
            Kind: kind,
            Priority: ordinal,
            Beneficial: false,
            ExpiresAt: now.AddMinutes(1),
            Modifiers: ClientStatusAggregate.Empty,
            Revision: checked(++state.Revision));
        RefreshSkillCastControlSnapshot(state);
        return true;
    }

    internal bool ProtocolCheckRemovePlayerLifeRevisionWhileGateHeld(
        ClientSession session) =>
        _playerLifeRevisions.TryRemove(session, out _);

    internal bool ProtocolCheckIsRegistryGateFree()
    {
        if (Monitor.IsEntered(_gate) || !Monitor.TryEnter(_gate))
        {
            return false;
        }
        Monitor.Exit(_gate);
        return true;
    }
#endif
}
