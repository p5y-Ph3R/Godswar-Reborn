namespace Godswar.Server.Application.Characters;

internal enum PlayerOwnershipValidationStatus : byte
{
    Current = 1,
    OwnershipLost = 2,
    CharacterNotFound = 3
}

internal readonly record struct PlayerOwnershipValidationResult(
    PlayerOwnershipValidationStatus Status,
    long? StoredGeneration)
{
    public bool IsCurrent =>
        Status == PlayerOwnershipValidationStatus.Current;

    public PlayerOwnershipValidationResult RequireCurrent()
    {
        if (!IsCurrent)
        {
            throw new PlayerOwnershipValidationException(Status);
        }

        return this;
    }
}

internal sealed class PlayerOwnershipValidationException :
    InvalidOperationException
{
    public PlayerOwnershipValidationException(
        PlayerOwnershipValidationStatus status)
        : base(MessageFor(status))
    {
        if (status == PlayerOwnershipValidationStatus.Current ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Only rejected ownership outcomes can be raised.");
        }

        Status = status;
    }

    public PlayerOwnershipValidationStatus Status { get; }

    private static string MessageFor(
        PlayerOwnershipValidationStatus status) =>
        status switch
        {
            PlayerOwnershipValidationStatus.OwnershipLost =>
                "The player ownership fence is no longer current.",
            PlayerOwnershipValidationStatus.CharacterNotFound =>
                "The account-owned character does not exist.",
            _ => "The player ownership validation result is invalid."
        };
}
