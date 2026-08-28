using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryCaptureMonster(
        ClientSession routingSession,
        MonsterRuntimeSnapshot expected,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        ArgumentNullException.ThrowIfNull(routingSession);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Definition.MapId is < byte.MinValue or > byte.MaxValue ||
            !TryResolveWorldInstance(
                checked((byte)expected.Definition.MapId),
                routingSession,
                out var runtime))
        {
            result = default!;
            return false;
        }

        var attempt = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                var captured = map.TryCaptureMonster(
                    expected,
                    now,
                    out var value);
                return (Captured: captured, Value: value);
            });
        result = attempt.Value;
        return attempt.Captured;
    }
}
