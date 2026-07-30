namespace Godswar.Server.Application.Characters;

internal readonly record struct CharacterCheckpointOwner(
    Guid OwnerId,
    long Generation)
{
    public void Validate()
    {
        if (OwnerId == Guid.Empty)
        {
            throw new ArgumentException(
                "Checkpoint owner ID cannot be empty.",
                nameof(OwnerId));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            Generation);
    }
}

internal readonly record struct CharacterPositionCheckpoint(
    int AccountId,
    int CharacterId,
    CharacterCheckpointOwner Owner,
    byte CurrentMap,
    float PositionX,
    float PositionZ,
    long Revision)
{
    public const float MaximumAbsoluteCoordinate = 4_096f;

    public void Validate()
    {
        CharacterCheckpointValidation.ValidateIdentity(
            AccountId,
            CharacterId,
            Owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Revision);
        CharacterCheckpointValidation.ValidateCoordinate(
            PositionX,
            nameof(PositionX));
        CharacterCheckpointValidation.ValidateCoordinate(
            PositionZ,
            nameof(PositionZ));
    }
}

internal readonly record struct CharacterVitalsCheckpoint(
    int AccountId,
    int CharacterId,
    CharacterCheckpointOwner Owner,
    int CurrentHp,
    int CurrentMp,
    long Revision)
{
    public void Validate()
    {
        CharacterCheckpointValidation.ValidateIdentity(
            AccountId,
            CharacterId,
            Owner);
        ArgumentOutOfRangeException.ThrowIfNegative(CurrentHp);
        ArgumentOutOfRangeException.ThrowIfNegative(CurrentMp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Revision);
    }
}

internal enum CharacterCheckpointWriteStatus : byte
{
    Applied = 1,
    AlreadyApplied = 2,
    Superseded = 3,
    RevisionConflict = 4,
    OwnershipLost = 5,
    CharacterNotFound = 6
}

internal readonly record struct CharacterCheckpointWriteResult(
    CharacterCheckpointWriteStatus Status,
    long? StoredRevision)
{
    public bool Satisfies(long requestedRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            requestedRevision);
        return (Status is
                    CharacterCheckpointWriteStatus.Applied or
                    CharacterCheckpointWriteStatus.AlreadyApplied or
                    CharacterCheckpointWriteStatus.Superseded) &&
            StoredRevision is { } stored &&
            stored >= requestedRevision;
    }
}

internal readonly record struct CharacterCheckpointOwnership(
    CharacterCheckpointOwner Owner,
    long PositionRevision,
    long VitalsRevision)
{
    public void Validate()
    {
        Owner.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(PositionRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(VitalsRevision);
    }
}

internal enum CharacterCheckpointReleaseStatus : byte
{
    Released = 1,
    AlreadyReleased = 2,
    OwnershipLost = 3,
    CharacterNotFound = 4
}

internal static class CharacterCheckpointValidation
{
    public static void ValidateIdentity(
        int accountId,
        int characterId,
        CharacterCheckpointOwner owner)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        owner.Validate();
    }

    public static void ValidateCoordinate(float value, string name)
    {
        if (!float.IsFinite(value) ||
            Math.Abs(value) >
                CharacterPositionCheckpoint.MaximumAbsoluteCoordinate)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"Checkpoint coordinates must be finite and within " +
                $"+/-{CharacterPositionCheckpoint.MaximumAbsoluteCoordinate}.");
        }
    }
}
