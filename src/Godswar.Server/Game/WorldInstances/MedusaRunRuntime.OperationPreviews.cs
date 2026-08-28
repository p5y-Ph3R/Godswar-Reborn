namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaRunRuntime
{
    /// <summary>
    /// Pure abandon eligibility check. A foreign identity or invalid clock is
    /// not an authoritative observation and cannot expose periodic work.
    /// </summary>
    internal MedusaRunAbandonOutcome PreviewAbandonRun(
        int requestedByCharacterId,
        DateTimeOffset abandonedAt)
    {
        if (!_admittedCharacters.Contains(requestedByCharacterId))
        {
            return MedusaRunAbandonOutcome.CharacterNotAdmitted;
        }

        return PreviewTime(abandonedAt) switch
        {
            MedusaRunClockOutcome.Active =>
                MedusaRunAbandonOutcome.Exited,
            MedusaRunClockOutcome.TimestampMovedBackward =>
                MedusaRunAbandonOutcome.TimestampMovedBackward,
            MedusaRunClockOutcome.DeadlineBoundaryUnresolved =>
                MedusaRunAbandonOutcome.DeadlineBoundaryUnresolved,
            MedusaRunClockOutcome.TimedOut =>
                MedusaRunAbandonOutcome.TimedOut,
            _ => MedusaRunAbandonOutcome.RunNotActive
        };
    }
}
