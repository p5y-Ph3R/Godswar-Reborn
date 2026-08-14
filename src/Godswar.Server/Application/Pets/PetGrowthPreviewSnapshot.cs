namespace Godswar.Server.Application.Pets;

internal enum PetGrowthPreviewRateSemantics : byte
{
    LegacyBasePreserveAcceleration = 0,
    NatureBaseWithRebirthModifier = 1
}

internal sealed record PetGrowthPreviewSnapshot(
    Guid PreviewOperationId,
    long PetId,
    short PetLevel,
    long ExpectedPetRevision,
    PetContentStatVector GrowthRates,
    DateTimeOffset ExpiresAtUtc,
    PetContentStatVector? CurrentGrowthRates = null,
    PetGrowthPreviewRateSemantics RateSemantics =
        PetGrowthPreviewRateSemantics.LegacyBasePreserveAcceleration,
    short CompletedRebirths = 0,
    PetContentStatVector? RebirthModifiers = null)
{
    public bool IsValid =>
        PreviewOperationId != Guid.Empty &&
        PetId > 0 &&
        PetLevel is >= 1 and <= 120 &&
        ExpectedPetRevision >= 0 &&
        ExpiresAtUtc > DateTimeOffset.UnixEpoch &&
        GrowthRates.Agility > 0 &&
        GrowthRates.Strength > 0 &&
        GrowthRates.Accuracy > 0 &&
        GrowthRates.Technique > 0 &&
        GrowthRates.Wisdom > 0 &&
        GrowthRates.Luck > 0 &&
        Enum.IsDefined(RateSemantics) &&
        HasValidRateSemantics;

    public bool UsesRebirthCountWidenedRates =>
        RateSemantics ==
            PetGrowthPreviewRateSemantics.NatureBaseWithRebirthModifier;

    public bool HasAuthoritativeCurrentRates =>
        CurrentGrowthRates is { } current &&
        current.Agility > 0 &&
        current.Strength > 0 &&
        current.Accuracy > 0 &&
        current.Technique > 0 &&
        current.Wisdom > 0 &&
        current.Luck > 0;

    public decimal[] ToOrderedRates() =>
    [
        GrowthRates.Agility,
        GrowthRates.Strength,
        GrowthRates.Accuracy,
        GrowthRates.Technique,
        GrowthRates.Wisdom,
        GrowthRates.Luck
    ];

    public decimal[] ToOrderedCurrentRates()
    {
        if (CurrentGrowthRates is not { } current)
        {
            throw new InvalidOperationException(
                "The Growth preview has no authoritative current rates.");
        }
        return
        [
            current.Agility,
            current.Strength,
            current.Accuracy,
            current.Technique,
            current.Wisdom,
            current.Luck
        ];
    }

    public decimal[] ToOrderedRebirthModifiers()
    {
        if (RebirthModifiers is not { } modifier)
        {
            throw new InvalidOperationException(
                "The Growth preview has no Rebirth modifier vector.");
        }
        return
        [
            modifier.Agility,
            modifier.Strength,
            modifier.Accuracy,
            modifier.Technique,
            modifier.Wisdom,
            modifier.Luck
        ];
    }

    private bool HasValidRateSemantics => RateSemantics switch
    {
        PetGrowthPreviewRateSemantics.LegacyBasePreserveAcceleration =>
            CompletedRebirths == 0 && RebirthModifiers is null,
        PetGrowthPreviewRateSemantics.NatureBaseWithRebirthModifier =>
            RebirthModifiers is { } modifier &&
            PetPhoenixRebirthModifierContract.IsValid(
                CompletedRebirths,
                modifier),
        _ => false
    };
}
