namespace Godswar.Server.Application.Inventory;

internal enum HolySuitCommandResultStatus : byte
{
    ExperienceStored = 1,
    ExperienceTransferred = 2,
    WareConsumed = 3,
    ExperienceTransformed = 4,
    WrongSelection = 5,
    InsufficientCharacterExperience = 6,
    NotHolyBox = 7,
    HolyBoxFull = 8,
    DailyStoreLimitExceeded = 9,
    TargetNotEquipment = 10,
    HolyBoxEmpty = 11,
    EquipmentExperienceLimitReached = 12,
    EquipmentInsufficientExperience = 13,
    MaximumHolySuit = 14,
    WareNotRequired = 15,
    WareUpgradeFailedRoll = 16,
    WareTypeMismatch = 17,
    InsufficientWares = 18,
    SecondItemNotHolyBox = 19,
    RequestedExperienceLimitExceeded = 20,
    InsufficientFunds = 21,
    BagFull = 22,
    InsufficientPrisms = 23,
    StalePrimaryItem = 24,
    StaleSecondaryItem = 25,
    PrimaryItemMissing = 26,
    SecondaryItemMissing = 27,
    LevelRequirementNotMet = 28
}
