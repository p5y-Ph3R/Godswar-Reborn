using System.Collections.Frozen;
using Godswar.Server.Application.Items;

namespace Godswar.Server.Application.Pets;

internal sealed partial class PinnedPetContentCatalog : IPetContentCatalog
{
    private readonly FrozenDictionary<short, PetSpeciesContentDefinition>
        _speciesById;
    private readonly FrozenDictionary<uint, PetSpeciesContentDefinition>
        _speciesByEgg;
    private readonly FrozenDictionary<uint, PetSpeciesContentDefinition>
        _speciesByMagicJade;
    private readonly FrozenDictionary<short, PetAptitudeContentDefinition>
        _aptitudesById;
    private readonly FrozenDictionary<
        (short SpeciesId, short Aptitude),
        PetNativeProfileContentDefinition> _profilesByKey;
    private readonly FrozenDictionary<short, int> _experienceByLevel;
    private readonly FrozenDictionary<short, PetRebirthStepContentDefinition>
        _rebirthByNumber;
    private readonly FrozenDictionary<
        (short Aptitude, short SpiritCount),
        PetMergeSavvyStepContentDefinition> _mergeSavvyByKey;
    private readonly PetMergeSavvyLookupContentDefinition[] _mergeSavvyLookup;
    private readonly PetMergeRankLookupContentDefinition[] _mergeRankLookup;
    private readonly FrozenDictionary<
        short,
        PetMergeRankSpeciesFactorContentDefinition> _mergeRankFactorBySpecies;
    private readonly FrozenDictionary<
        short,
        PetMergeRankSpiritStepContentDefinition> _mergeRankSpiritByCount;

    private PinnedPetContentCatalog(
        PetContentRevision revision,
        PetContentSettings settings,
        PetSpeciesContentDefinition[] species,
        PetAptitudeContentDefinition[] aptitudes,
        PetNativeProfileContentDefinition[] nativeProfiles,
        PetExperienceStepContentDefinition[] experienceSteps,
        PetRebirthStepContentDefinition[] rebirthSteps,
        PetMergeSavvyStepContentDefinition[] mergeSavvySteps,
        PetMergeSavvyLookupContentDefinition[] mergeSavvyLookup,
        PetHatchRankStepContentDefinition[] hatchRankSteps,
        PetMergeRankLookupContentDefinition[] mergeRankLookup,
        PetMergeRankSpeciesFactorContentDefinition[] mergeRankSpeciesFactors,
        PetMergeRankSpiritStepContentDefinition[] mergeRankSpiritSteps)
    {
        Revision = revision;
        Settings = settings;
        Species = Array.AsReadOnly(species);
        Aptitudes = Array.AsReadOnly(aptitudes);
        NativeProfiles = Array.AsReadOnly(nativeProfiles);
        ExperienceSteps = Array.AsReadOnly(experienceSteps);
        RebirthSteps = Array.AsReadOnly(rebirthSteps);
        MergeSavvySteps = Array.AsReadOnly(mergeSavvySteps);
        MergeSavvyLookup = Array.AsReadOnly(mergeSavvyLookup);
        HatchRankSteps = Array.AsReadOnly(hatchRankSteps);
        MergeRankLookup = Array.AsReadOnly(mergeRankLookup);
        MergeRankSpeciesFactors = Array.AsReadOnly(mergeRankSpeciesFactors);
        MergeRankSpiritSteps = Array.AsReadOnly(mergeRankSpiritSteps);
        _speciesById = species.ToFrozenDictionary(
            static value => value.SpeciesId);
        _speciesByEgg = species
            .Where(static value => value.EggItemId.HasValue)
            .ToFrozenDictionary(
                static value => value.EggItemId!.Value);
        _speciesByMagicJade = species.ToFrozenDictionary(
            static value => value.MagicJadeItemId);
        _aptitudesById = aptitudes.ToFrozenDictionary(
            static value => value.Aptitude);
        _profilesByKey = nativeProfiles.ToFrozenDictionary(
            static value => (value.SpeciesId, value.Aptitude));
        _experienceByLevel = experienceSteps.ToFrozenDictionary(
            static value => value.CurrentLevel,
            static value => value.RequiredExperience);
        _rebirthByNumber = rebirthSteps.ToFrozenDictionary(
            static value => value.RebirthNumber);
        _mergeSavvyByKey = mergeSavvySteps.ToFrozenDictionary(
            static value => (value.Aptitude, value.SpiritCount));
        _mergeSavvyLookup = mergeSavvyLookup;
        _mergeRankLookup = mergeRankLookup;
        _mergeRankFactorBySpecies = mergeRankSpeciesFactors
            .ToFrozenDictionary(static value => value.SpeciesId);
        _mergeRankSpiritByCount = mergeRankSpiritSteps
            .ToFrozenDictionary(static value => value.SpiritCount);
    }

    public PetContentRevision Revision { get; }

    public PetContentSettings Settings { get; }

    public IReadOnlyList<PetSpeciesContentDefinition> Species { get; }

    public IReadOnlyList<PetAptitudeContentDefinition> Aptitudes { get; }

    public IReadOnlyList<PetNativeProfileContentDefinition> NativeProfiles
        { get; }

    public IReadOnlyList<PetExperienceStepContentDefinition> ExperienceSteps
        { get; }

    public IReadOnlyList<PetRebirthStepContentDefinition> RebirthSteps
        { get; }

    public IReadOnlyList<PetMergeSavvyStepContentDefinition> MergeSavvySteps
        { get; }

    public IReadOnlyList<PetMergeSavvyLookupContentDefinition> MergeSavvyLookup
        { get; }

    public IReadOnlyList<PetHatchRankStepContentDefinition> HatchRankSteps
        { get; }

    public IReadOnlyList<PetMergeRankLookupContentDefinition> MergeRankLookup
        { get; }

    public IReadOnlyList<PetMergeRankSpeciesFactorContentDefinition>
        MergeRankSpeciesFactors { get; }

    public IReadOnlyList<PetMergeRankSpiritStepContentDefinition>
        MergeRankSpiritSteps { get; }

    public bool TryGetSpecies(
        int speciesId,
        out PetSpeciesContentDefinition definition)
    {
        if (speciesId is >= short.MinValue and <= short.MaxValue &&
            _speciesById.TryGetValue((short)speciesId, out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public bool TryGetSpeciesByEggItemId(
        uint itemId,
        out PetSpeciesContentDefinition definition) =>
        _speciesByEgg.TryGetValue(itemId, out definition!);

    public bool TryGetSpeciesByMagicJadeItemId(
        uint itemId,
        out PetSpeciesContentDefinition definition) =>
        _speciesByMagicJade.TryGetValue(itemId, out definition!);

    public bool TryGetAptitude(
        short aptitude,
        out PetAptitudeContentDefinition definition) =>
        _aptitudesById.TryGetValue(aptitude, out definition!);

    public bool TryGetNativeProfile(
        int speciesId,
        short aptitude,
        out PetNativeProfileContentDefinition definition)
    {
        if (speciesId is >= short.MinValue and <= short.MaxValue &&
            _profilesByKey.TryGetValue(
                ((short)speciesId, aptitude),
                out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public bool TryGetRebirthStep(
        int rebirthNumber,
        out PetRebirthStepContentDefinition definition)
    {
        if (rebirthNumber is >= short.MinValue and <= short.MaxValue &&
            _rebirthByNumber.TryGetValue(
                (short)rebirthNumber,
                out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public bool TryGetMergeSavvyStep(
        int aptitude,
        int spiritCount,
        out PetMergeSavvyStepContentDefinition definition)
    {
        if (aptitude is >= short.MinValue and <= short.MaxValue &&
            spiritCount is >= short.MinValue and <= short.MaxValue &&
            _mergeSavvyByKey.TryGetValue(
                ((short)aptitude, (short)spiritCount),
                out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public bool TryResolveMergeSavvyLookup(
        int savvyDifferenceHundredths,
        out PetMergeSavvyLookupContentDefinition definition)
    {
        for (var index = _mergeSavvyLookup.Length - 1; index >= 0; index--)
        {
            var candidate = _mergeSavvyLookup[index];
            if (savvyDifferenceHundredths >=
                candidate.MinimumSavvyDifference)
            {
                definition = candidate;
                return true;
            }
        }

        definition = null!;
        return false;
    }

    public bool TryResolveMergeRankLookup(
        int rankDifferenceHundredths,
        out PetMergeRankLookupContentDefinition definition)
    {
        for (var index = _mergeRankLookup.Length - 1; index >= 0; index--)
        {
            var candidate = _mergeRankLookup[index];
            if (rankDifferenceHundredths >= candidate.MinimumRankDifference)
            {
                definition = candidate;
                return true;
            }
        }

        definition = null!;
        return false;
    }

    public bool TryGetMergeRankSpeciesFactor(
        int speciesId,
        out PetMergeRankSpeciesFactorContentDefinition definition)
    {
        if (speciesId is >= short.MinValue and <= short.MaxValue &&
            _mergeRankFactorBySpecies.TryGetValue(
                checked((short)speciesId),
                out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public bool TryGetMergeRankSpiritStep(
        int spiritCount,
        out PetMergeRankSpiritStepContentDefinition definition)
    {
        if (spiritCount is >= short.MinValue and <= short.MaxValue &&
            _mergeRankSpiritByCount.TryGetValue(
                checked((short)spiritCount),
                out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public int RequiredExperienceForNextLevel(int currentLevel)
    {
        if (currentLevel < Settings.MinimumLevel ||
            currentLevel > Settings.MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentLevel),
                currentLevel,
                $"Pet level must be between {Settings.MinimumLevel} and {Settings.MaximumLevel}.");
        }

        return currentLevel == Settings.MaximumLevel
            ? 0
            : _experienceByLevel[checked((short)currentLevel)];
    }

    public PetHatchRankRoll RollHatchRank(short aptitude, int roll) =>
        PetHatchRankContentPolicy.Roll(HatchRankSteps, aptitude, roll);

    public void ValidateItemReferences(IItemTemplateCatalog items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var references = Species
            .SelectMany(static value =>
                value.EggItemId is { } egg ? new[] { egg } : [])
            .Concat(Species.Select(
                static value => value.MagicJadeItemId))
            .Concat(
            [
                Settings.MergeSpiritItemId,
                Settings.RestrictedMergeSpiritItemId,
                Settings.RebirthSpiritItemId,
                Settings.RestrictedRebirthSpiritItemId
            ])
            .Concat(RebirthSteps.Select(static value => value.ChanceItemId))
            .Distinct()
            .Order()
            .ToArray();
        var missing = references
            .Where(itemId => !items.TryGet(itemId, out _))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException(
                "Pet content references item IDs absent from the process-pinned " +
                $"item revision {items.Revision.Sha256}: " +
                string.Join(", ", missing));
        }
    }

    public static PinnedPetContentCatalog Create(
        string source,
        PetContentSettings settings,
        IReadOnlyList<PetSpeciesContentDefinition> species,
        IReadOnlyList<PetAptitudeContentDefinition> aptitudes,
        IReadOnlyList<PetNativeProfileContentDefinition> nativeProfiles,
        IReadOnlyList<PetExperienceStepContentDefinition> experienceSteps,
        IReadOnlyList<PetRebirthStepContentDefinition> rebirthSteps,
        IReadOnlyList<PetMergeSavvyStepContentDefinition> mergeSavvySteps,
        IReadOnlyList<PetMergeSavvyLookupContentDefinition> mergeSavvyLookup,
        IReadOnlyList<PetHatchRankStepContentDefinition> hatchRankSteps,
        IReadOnlyList<PetMergeRankLookupContentDefinition> mergeRankLookup,
        IReadOnlyList<PetMergeRankSpeciesFactorContentDefinition>
            mergeRankSpeciesFactors,
        IReadOnlyList<PetMergeRankSpiritStepContentDefinition>
            mergeRankSpiritSteps,
        string? expectedRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(species);
        ArgumentNullException.ThrowIfNull(aptitudes);
        ArgumentNullException.ThrowIfNull(nativeProfiles);
        ArgumentNullException.ThrowIfNull(experienceSteps);
        ArgumentNullException.ThrowIfNull(rebirthSteps);
        ArgumentNullException.ThrowIfNull(mergeSavvySteps);
        ArgumentNullException.ThrowIfNull(mergeSavvyLookup);
        ArgumentNullException.ThrowIfNull(hatchRankSteps);
        ArgumentNullException.ThrowIfNull(mergeRankLookup);
        ArgumentNullException.ThrowIfNull(mergeRankSpeciesFactors);
        ArgumentNullException.ThrowIfNull(mergeRankSpiritSteps);

        var settingsSnapshot = settings with
        {
            AddedSavvyWeights = Array.AsReadOnly(
                settings.AddedSavvyWeights.ToArray())
        };
        var speciesSnapshot = species
            .Select(static value => value with
            {
                LifetimeValues = Array.AsReadOnly(
                    value.LifetimeValues.ToArray())
            })
            .OrderBy(static value => value.SpeciesId)
            .ToArray();
        var aptitudeSnapshot = aptitudes
            .OrderBy(static value => value.Aptitude)
            .ToArray();
        var profileSnapshot = nativeProfiles
            .OrderBy(static value => value.SpeciesId)
            .ThenBy(static value => value.Aptitude)
            .ToArray();
        var experienceSnapshot = experienceSteps
            .OrderBy(static value => value.CurrentLevel)
            .ToArray();
        var rebirthSnapshot = rebirthSteps
            .OrderBy(static value => value.RebirthNumber)
            .ToArray();
        var mergeSavvySnapshot = mergeSavvySteps
            .OrderBy(static value => value.Aptitude)
            .ThenBy(static value => value.SpiritCount)
            .ToArray();
        var mergeSavvyLookupSnapshot = mergeSavvyLookup
            .OrderBy(static value => value.MinimumSavvyDifference)
            .ToArray();
        var hatchRankSnapshot = hatchRankSteps
            .OrderBy(static value => value.Aptitude)
            .ThenBy(static value => value.OutcomeOrder)
            .ToArray();
        var mergeRankLookupSnapshot = mergeRankLookup
            .OrderBy(static value => value.MinimumRankDifference)
            .ToArray();
        var mergeRankSpeciesFactorSnapshot = mergeRankSpeciesFactors
            .OrderBy(static value => value.SpeciesId)
            .ToArray();
        var mergeRankSpiritStepSnapshot = mergeRankSpiritSteps
            .OrderBy(static value => value.SpiritCount)
            .ToArray();

        Validate(
            settingsSnapshot,
            speciesSnapshot,
            aptitudeSnapshot,
            profileSnapshot,
            experienceSnapshot,
            rebirthSnapshot,
            mergeSavvySnapshot,
            mergeSavvyLookupSnapshot,
            hatchRankSnapshot,
            mergeRankLookupSnapshot,
            mergeRankSpeciesFactorSnapshot,
            mergeRankSpiritStepSnapshot);
        var revision = PetContentRevisionHasher.Compute(
            settingsSnapshot,
            speciesSnapshot,
            aptitudeSnapshot,
            profileSnapshot,
            experienceSnapshot,
            rebirthSnapshot,
            mergeSavvySnapshot,
            mergeSavvyLookupSnapshot,
            hatchRankSnapshot,
            mergeRankLookupSnapshot,
            mergeRankSpeciesFactorSnapshot,
            mergeRankSpiritStepSnapshot);
        if (expectedRevision is not null &&
            !revision.Equals(expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The published pet-content snapshot does not match its revision.");
        }

        return new PinnedPetContentCatalog(
            new PetContentRevision(
                revision,
                speciesSnapshot.Length,
                aptitudeSnapshot.Length,
                profileSnapshot.Length,
                experienceSnapshot.Length,
                rebirthSnapshot.Length,
                mergeSavvySnapshot.Length,
                mergeSavvyLookupSnapshot.Length,
                hatchRankSnapshot.Length,
                mergeRankLookupSnapshot.Length,
                mergeRankSpeciesFactorSnapshot.Length,
                mergeRankSpiritStepSnapshot.Length,
                source),
            settingsSnapshot,
            speciesSnapshot,
            aptitudeSnapshot,
            profileSnapshot,
            experienceSnapshot,
            rebirthSnapshot,
            mergeSavvySnapshot,
            mergeSavvyLookupSnapshot,
            hatchRankSnapshot,
            mergeRankLookupSnapshot,
            mergeRankSpeciesFactorSnapshot,
            mergeRankSpiritStepSnapshot);
    }
}
