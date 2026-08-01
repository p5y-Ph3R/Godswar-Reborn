using System.Collections.Frozen;
using Godswar.Server.Application.Items;

namespace Godswar.Server.Application.Pets;

internal sealed partial class PinnedPetContentCatalog : IPetContentCatalog
{
    private readonly FrozenDictionary<short, PetSpeciesContentDefinition>
        _speciesById;
    private readonly FrozenDictionary<uint, PetSpeciesContentDefinition>
        _speciesByEgg;
    private readonly FrozenDictionary<short, PetAptitudeContentDefinition>
        _aptitudesById;
    private readonly FrozenDictionary<
        (short SpeciesId, short Aptitude),
        PetNativeProfileContentDefinition> _profilesByKey;
    private readonly FrozenDictionary<short, int> _experienceByLevel;
    private readonly FrozenDictionary<short, PetRebirthStepContentDefinition>
        _rebirthByNumber;

    private PinnedPetContentCatalog(
        PetContentRevision revision,
        PetContentSettings settings,
        PetSpeciesContentDefinition[] species,
        PetAptitudeContentDefinition[] aptitudes,
        PetNativeProfileContentDefinition[] nativeProfiles,
        PetExperienceStepContentDefinition[] experienceSteps,
        PetRebirthStepContentDefinition[] rebirthSteps)
    {
        Revision = revision;
        Settings = settings;
        Species = Array.AsReadOnly(species);
        Aptitudes = Array.AsReadOnly(aptitudes);
        NativeProfiles = Array.AsReadOnly(nativeProfiles);
        ExperienceSteps = Array.AsReadOnly(experienceSteps);
        RebirthSteps = Array.AsReadOnly(rebirthSteps);
        _speciesById = species.ToFrozenDictionary(
            static value => value.SpeciesId);
        _speciesByEgg = species
            .Where(static value => value.EggItemId.HasValue)
            .ToFrozenDictionary(
                static value => value.EggItemId!.Value);
        _aptitudesById = aptitudes.ToFrozenDictionary(
            static value => value.Aptitude);
        _profilesByKey = nativeProfiles.ToFrozenDictionary(
            static value => (value.SpeciesId, value.Aptitude));
        _experienceByLevel = experienceSteps.ToFrozenDictionary(
            static value => value.CurrentLevel,
            static value => value.RequiredExperience);
        _rebirthByNumber = rebirthSteps.ToFrozenDictionary(
            static value => value.RebirthNumber);
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

    public void ValidateItemReferences(IItemTemplateCatalog items)
    {
        ArgumentNullException.ThrowIfNull(items);
        // Egg consumption and the merge/rebirth planners are active runtime
        // paths. Magic Jade species-change is captured content but has no
        // implemented command yet, and those 45 client IDs are not part of
        // the current reviewed item publication. Validate that future command
        // when its item family is published rather than weakening item pinning.
        var references = Species
            .SelectMany(static value =>
                value.EggItemId is { } egg ? new[] { egg } : [])
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
        string? expectedRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(species);
        ArgumentNullException.ThrowIfNull(aptitudes);
        ArgumentNullException.ThrowIfNull(nativeProfiles);
        ArgumentNullException.ThrowIfNull(experienceSteps);
        ArgumentNullException.ThrowIfNull(rebirthSteps);

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

        Validate(
            settingsSnapshot,
            speciesSnapshot,
            aptitudeSnapshot,
            profileSnapshot,
            experienceSnapshot,
            rebirthSnapshot);
        var revision = PetContentRevisionHasher.Compute(
            settingsSnapshot,
            speciesSnapshot,
            aptitudeSnapshot,
            profileSnapshot,
            experienceSnapshot,
            rebirthSnapshot);
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
                source),
            settingsSnapshot,
            speciesSnapshot,
            aptitudeSnapshot,
            profileSnapshot,
            experienceSnapshot,
            rebirthSnapshot);
    }
}
