namespace Godswar.Server.Application.Pets;

/// <summary>
/// One installed-client Pet_Alter Restrict/Values lookup row. Differences and
/// increases use the native hundredths representation.
/// </summary>
internal sealed record PetMergeSavvyLookupContentDefinition(
    int MinimumSavvyDifference,
    ushort BaseIncrease);
