namespace Godswar.Server.Application.Pets;

/// <summary>
/// One stock Pet_Alter Qualityadd threshold and its base rank-gain value.
/// Both fields use the native fixed-hundredths scale.
/// </summary>
internal sealed record PetMergeRankLookupContentDefinition(
    int MinimumRankDifference,
    ushort BaseIncrease);

/// <summary>
/// The stock multiplier selected by the deputy pet's species.
/// </summary>
internal sealed record PetMergeRankSpeciesFactorContentDefinition(
    short SpeciesId,
    decimal Factor);

/// <summary>
/// The inclusive rank-gain percentage interval selected by Merge Spirit count.
/// </summary>
internal sealed record PetMergeRankSpiritStepContentDefinition(
    short SpiritCount,
    short MinimumPercent,
    short MaximumPercent);
