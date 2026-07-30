namespace Godswar.Server.Application.Characters;

/// <summary>
/// Monotonic PostgreSQL-backed ownership identity for one active player
/// character session.
/// </summary>
internal readonly record struct PlayerOwnershipFence(
    Guid OwnerId,
    long Generation)
{
    public bool IsValid =>
        OwnerId != Guid.Empty &&
        Generation > 0;

    public void Validate()
    {
        if (OwnerId == Guid.Empty)
        {
            throw new ArgumentException(
                "Player owner ID cannot be empty.",
                nameof(OwnerId));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            Generation);
    }
}
