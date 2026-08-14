namespace Godswar.Server.Application.Pets;

internal sealed record PetBasicSavvyPreviewSnapshot(
    Guid PreviewOperationId,
    long PetId,
    short PetLevel,
    long ExpectedPetRevision,
    PetContentStatVector BasicSavvyValues,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsValid =>
        PreviewOperationId != Guid.Empty &&
        PetId > 0 &&
        PetLevel is >= 1 and <= 120 &&
        ExpectedPetRevision >= 0 &&
        ExpiresAtUtc > DateTimeOffset.UnixEpoch &&
        ToOrderedValues().All(static value => value > 0m);

    public decimal[] ToOrderedValues() =>
    [
        BasicSavvyValues.Agility,
        BasicSavvyValues.Strength,
        BasicSavvyValues.Accuracy,
        BasicSavvyValues.Technique,
        BasicSavvyValues.Wisdom,
        BasicSavvyValues.Luck
    ];
}
