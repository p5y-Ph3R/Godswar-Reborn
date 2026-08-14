namespace Godswar.Server.Application.Pets;

internal sealed class PinnedPetOwnerMergeContentCatalog :
    IPetOwnerMergeContentCatalog
{
    private PinnedPetOwnerMergeContentCatalog(
        PetOwnerMergeContentRevision revision,
        PetOwnerMergeEffectBaseContentDefinition[] effectBases,
        PetOwnerMergeBandContentDefinition[] bands,
        PetOwnerMergeRateContentDefinition[] rates)
    {
        Revision = revision;
        EffectBases = Array.AsReadOnly(effectBases);
        Bands = Array.AsReadOnly(bands);
        Rates = Array.AsReadOnly(rates);
    }

    public PetOwnerMergeContentRevision Revision { get; }

    public IReadOnlyList<PetOwnerMergeEffectBaseContentDefinition> EffectBases
        { get; }

    public IReadOnlyList<PetOwnerMergeBandContentDefinition> Bands { get; }

    public IReadOnlyList<PetOwnerMergeRateContentDefinition> Rates { get; }

    public static PinnedPetOwnerMergeContentCatalog Create(
        string source,
        string policyVersion,
        IReadOnlyList<PetOwnerMergeEffectBaseContentDefinition> effectBases,
        IReadOnlyList<PetOwnerMergeBandContentDefinition> bands,
        IReadOnlyList<PetOwnerMergeRateContentDefinition> rates,
        string? expectedRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        ArgumentNullException.ThrowIfNull(effectBases);
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(rates);

        var baseSnapshot = effectBases
            .OrderBy(static value => value.Effect)
            .ToArray();
        var bandSnapshot = bands
            .OrderBy(static value => value.BandIndex)
            .ToArray();
        var rateSnapshot = rates
            .OrderBy(static value => value.SourceSavvy)
            .ThenBy(static value => value.Effect)
            .ThenBy(static value => value.BandIndex)
            .ToArray();
        PetOwnerMergeContentValidation.Validate(
            source,
            policyVersion,
            baseSnapshot,
            bandSnapshot,
            rateSnapshot);
        var revision = PetOwnerMergeContentRevisionHasher.Compute(
            source,
            policyVersion,
            baseSnapshot,
            bandSnapshot,
            rateSnapshot);
        if (expectedRevision is not null &&
            !revision.Equals(expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The published pet owner-Merge balance does not match its revision.");
        }

        return new PinnedPetOwnerMergeContentCatalog(
            new PetOwnerMergeContentRevision(
                revision,
                policyVersion,
                baseSnapshot.Length,
                bandSnapshot.Length,
                rateSnapshot.Length,
                source),
            baseSnapshot,
            bandSnapshot,
            rateSnapshot);
    }
}
