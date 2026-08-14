namespace Godswar.Server.Application.Pets;

internal sealed record PetAppearanceChangeEvidence(
    short OldSpeciesId,
    string OldSpeciesName,
    short NewSpeciesId,
    string NewSpeciesName,
    uint MagicJadeItemId,
    string MagicJadeDisplayName,
    long MagicJadeItemInstanceId,
    int KitBagSlot,
    string PetContentRevision,
    string ItemContentRevision)
{
    public bool IsValid =>
        OldSpeciesId > 0 &&
        IsLabel(OldSpeciesName) &&
        NewSpeciesId > 0 &&
        IsLabel(NewSpeciesName) &&
        OldSpeciesId != NewSpeciesId &&
        MagicJadeItemId > 0 &&
        IsLabel(MagicJadeDisplayName) &&
        MagicJadeItemInstanceId > 0 &&
        KitBagSlot is >= PetDurableCommandContract.MinimumKitBagSlot and
            <= PetDurableCommandContract.MaximumKitBagSlot &&
        IsRevision(PetContentRevision) &&
        IsRevision(ItemContentRevision);

    private static bool IsLabel(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        !value.Any(char.IsControl);

    private static bool IsRevision(string? value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
