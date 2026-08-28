namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaRunRuntime
{
    internal readonly record struct MonsterHitClockSnapshot(
        DateTimeOffset LastObservedAt);

    internal MonsterHitClockSnapshot CaptureMonsterHitClockSnapshot() =>
        new(_lastObservedAt);

    internal void RestoreMonsterHitClockSnapshot(
        in MonsterHitClockSnapshot snapshot) =>
        _lastObservedAt = snapshot.LastObservedAt;
}
