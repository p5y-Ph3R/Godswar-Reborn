namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// A preallocated recovery deadline whose UTC tick value can be replaced
    /// atomically during a lethal transaction without replacing a dictionary
    /// node or allocating after player HP has committed.
    /// </summary>
    private sealed class PlayerRecoveryDeadline
    {
        private long _utcTicks;

        public PlayerRecoveryDeadline(DateTimeOffset value)
        {
            _utcTicks = value.UtcDateTime.Ticks;
        }

        public DateTimeOffset Read() =>
            new(
                Volatile.Read(ref _utcTicks),
                TimeSpan.Zero);

        public void Write(DateTimeOffset value) =>
            Volatile.Write(
                ref _utcTicks,
                value.UtcDateTime.Ticks);
    }
}
