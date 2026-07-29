namespace Godswar.Server.Application.Characters;

internal enum CharacterSnapshotFailureReason : byte
{
    AccountNotFound = 1,
    AmbiguousCharacterSlot = 2,
    CharacterNotFound = 3,
    MissingCalculatedStats = 4,
    OwnershipMismatch = 5,
    InvalidData = 6,
    BoundsExceeded = 7,
    UnsupportedContractVersion = 8,
    ProviderUnavailable = 9
}
internal sealed class CharacterSnapshotUnavailableException : Exception
{
    public CharacterSnapshotUnavailableException(
        CharacterSnapshotFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public CharacterSnapshotFailureReason Reason { get; }
}

internal static class CharacterSnapshotLimits
{
    public const int ProviderSnapshotTokenLength = 256;
    public const int CharacterNameLength = 32;
    public const int EquipmentProjectionLength = 16 * 1024;
    public const int KitBagProjectionLength = 64 * 1024;
    public const int ZodiacGridCount = 16;
    public const int SkillCount = 1_024;
    public const int TalentCount = 256;
    public const int OwnedPetCount = 8;
    public const int PetNameLength = 64;
    public const int PetActivityStateLength = 32;
    public const int PetStatValueCount = 16;
    public const int PetCharacterBonusCount = 64;
    public const int PetSkillCount = 12;
    public const int PersonalBoostCount = 64;
    public const int BoostSourceLength = 128;
}
