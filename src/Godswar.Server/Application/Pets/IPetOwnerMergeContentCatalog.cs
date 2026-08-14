namespace Godswar.Server.Application.Pets;

/// <summary>
/// One immutable, database-published owner-Merge balance revision pinned for
/// the process lifetime. Gameplay receives this catalog from composition and
/// never reads authoring tables directly.
/// </summary>
internal interface IPetOwnerMergeContentCatalog
{
    PetOwnerMergeContentRevision Revision { get; }

    IReadOnlyList<PetOwnerMergeEffectBaseContentDefinition> EffectBases
        { get; }

    IReadOnlyList<PetOwnerMergeBandContentDefinition> Bands { get; }

    IReadOnlyList<PetOwnerMergeRateContentDefinition> Rates { get; }
}
