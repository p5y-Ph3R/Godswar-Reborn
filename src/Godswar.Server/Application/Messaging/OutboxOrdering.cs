namespace Godswar.Server.Application.Messaging;

internal enum OutboxOrderingPolicy : byte
{
    StrictSequence = 1,
    VersionedState = 2
}

internal enum OutboxOrderingDecision : byte
{
    Deliver = 1,
    Stale = 2,
    Gap = 3
}

/// <summary>
/// Pure aggregate-version ordering rules. The caller owns durable checkpoints
/// and advances them only after successful delivery.
/// </summary>
internal static class OutboxOrderingRules
{
    public static OutboxOrderingDecision Decide(
        OutboxOrderingPolicy policy,
        long lastAppliedRevision,
        long incomingRevision)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (lastAppliedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastAppliedRevision));
        }

        if (incomingRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(incomingRevision));
        }

        if (incomingRevision <= lastAppliedRevision)
        {
            return OutboxOrderingDecision.Stale;
        }

        if (policy == OutboxOrderingPolicy.VersionedState)
        {
            return OutboxOrderingDecision.Deliver;
        }

        return incomingRevision - lastAppliedRevision == 1
            ? OutboxOrderingDecision.Deliver
            : OutboxOrderingDecision.Gap;
    }

    public static OutboxOrderingDecision Decide(
        OutboxOrderingPolicy policy,
        long lastAppliedRevision,
        OutboxEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Decide(
            policy,
            lastAppliedRevision,
            message.AggregateRevision);
    }
}
