using System.Text.RegularExpressions;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetContentArchitectureChecks
{
    public const string CheckName =
        "Pinned PostgreSQL pet-content boundary";

    private static readonly string[] CompiledContentSymbols =
    [
        "PetSpeciesCatalog",
        "PetAptitudeCatalog",
        "PetGrowthPolicy",
        "PetInitialSavvyPolicy",
        "PetAddedSavvyPolicy",
        "PetInnateTalentPolicy",
        "PetNativeAptitudeProfileCatalog",
        "PetExperienceCatalog",
        "PetRebirthGrowthPolicy"
    ];

    private static readonly HashSet<string> AllowedCompiledConsumers =
        new(StringComparer.Ordinal)
        {
            "src/Godswar.Server/Infrastructure/Items/PetMagicJadeItemContentBaseline.cs",
            "src/Godswar.Server/Infrastructure/Pets/PetContentBaseline.cs",
            "src/Godswar.Server/Infrastructure/Pets/PetMergeRankContentBaseline.cs",
            "src/Godswar.Server/State/PetAddedSavvyPolicy.cs",
            "src/Godswar.Server/State/PetGrowthPolicy.cs",
            "src/Godswar.Server/State/PetInitialSavvyPolicy.cs",
            "src/Godswar.Server/State/PetInnateTalentPolicy.cs",
            "src/Godswar.Server/State/PetNativeAptitudeProfileCatalog.cs",
            "src/Godswar.Server/State/PetSkillSlotPolicy.cs"
        };

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static value => value.Id ==
                "20260801_042_pet_content_release");
        AssertContains(
            migration.Sql,
            "pet_content_revisions",
            "pet_content_settings",
            "pet_content_species_definitions",
            "pet_content_aptitude_definitions",
            "pet_content_native_profiles",
            "pet_content_experience_steps",
            "pet_content_rebirth_steps",
            "pet_content_publication",
            "sealed_at",
            "guard_pet_content_insert",
            "FOR UPDATE",
            "validate_pet_content_publication",
            "trg_pet_content_publication_no_delete");

        AssertCompiledSourceIsolation(root);
        AssertNoMutableRuntimeReads(root);
        AssertRuntimeUsesPinnedCatalog(root);
        AssertSealedPetReaderComposition(root);
        AssertPinnedCatalogIntegrity();
        return Task.CompletedTask;
    }

    private static void AssertCompiledSourceIsolation(string root)
    {
        var sourceRoot = Path.Combine(root, "src", "Godswar.Server");
        var pattern = new Regex(
            @"\b(" + string.Join("|", CompiledContentSymbols) + @")\.",
            RegexOptions.CultureInvariant);
        var consumers = Directory.EnumerateFiles(
                sourceRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Relative(root, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            consumers.SequenceEqual(
                AllowedCompiledConsumers.OrderBy(
                    static path => path,
                    StringComparer.Ordinal)),
            "compiled pet facts are isolated to the cold baseline and " +
            "definition-internal dependencies: " +
            string.Join(", ", consumers));
    }

    private static void AssertNoMutableRuntimeReads(string root)
    {
        var sourceRoot = Path.Combine(root, "src", "Godswar.Server");
        var mutableRead = new Regex(
            @"\b(?:FROM|JOIN)\s+(?:public\.)?" +
            @"(?:pet_templates|pet_aptitude_templates)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var offenders = Directory.EnumerateFiles(
                sourceRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !Relative(root, path).StartsWith(
                "src/Godswar.Server/State/DatabaseMigrations/",
                StringComparison.Ordinal))
            .Where(path => mutableRead.IsMatch(File.ReadAllText(path)))
            .Select(path => Relative(root, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            offenders.Length == 0,
            "mutable pet authoring tables have no runtime readers: " +
            string.Join(", ", offenders));
    }

    private static void AssertRuntimeUsesPinnedCatalog(string root)
    {
        string[] consumers =
        [
            "src/Godswar.Server/State/PetManagerPlanner.cs",
            "src/Godswar.Server/Packets/PacketBuilder.Pets.cs",
            "src/Godswar.Server/Packets/PacketBuilder.PetLevel.cs",
            "src/Godswar.Server/Infrastructure/Pets/" +
                "PostgresPetDurableCommandExecutor.cs",
            "src/Godswar.Server/Game/GameClientHandler.cs"
        ];
        foreach (var path in consumers)
        {
            Check.True(
                Read(root, path).Contains(
                    "IPetContentCatalog",
                    StringComparison.Ordinal),
                $"{path} receives a process-pinned pet catalog");
        }

        var loader = Read(
            root,
            "src/Godswar.Server/Infrastructure/Pets/" +
            "PostgresPetContentReader.cs");
        Check.True(
            loader.Contains(
                "IsolationLevel.RepeatableRead",
                StringComparison.Ordinal) &&
            loader.Contains(
                "PinnedPetContentCatalog.Create",
                StringComparison.Ordinal),
            "pet loader pins and validates one repeatable-read revision");
    }

    private static void AssertPinnedCatalogIntegrity()
    {
        var baseline = PetContentBaseline.Create();
        Check.True(
            baseline.Revision.SpeciesCount == baseline.Species.Count &&
            baseline.Revision.AptitudeCount == baseline.Aptitudes.Count &&
            baseline.Revision.NativeProfileCount ==
                baseline.NativeProfiles.Count &&
            baseline.Revision.ExperienceStepCount ==
                baseline.ExperienceSteps.Count &&
            baseline.Revision.RebirthStepCount ==
                baseline.RebirthSteps.Count &&
            baseline.Revision.MergeSavvyStepCount ==
                baseline.MergeSavvySteps.Count &&
            baseline.Revision.MergeSavvyLookupCount ==
                baseline.MergeSavvyLookup.Count &&
            baseline.Revision.HatchRankStepCount ==
                baseline.HatchRankSteps.Count &&
            baseline.Revision.MergeRankLookupCount ==
                baseline.MergeRankLookup.Count &&
            baseline.Revision.MergeRankSpeciesFactorCount ==
                baseline.MergeRankSpeciesFactors.Count &&
            baseline.Revision.MergeRankSpiritStepCount ==
                baseline.MergeRankSpiritSteps.Count,
            "pet revision manifest exactly counts every content family");
        baseline.ValidateItemReferences(
            CreateReferencedItemCatalog(baseline));
        AssertCalmCompatibilityProfiles(baseline);
        AssertMagicJadeAppearanceGroups(baseline);

        var lifetimeSource = baseline.Species[0].LifetimeValues.ToArray();
        var species = baseline.Species
            .Select((value, index) => index == 0
                ? value with { LifetimeValues = lifetimeSource }
                : value)
            .ToArray();
        var copied = PinnedPetContentCatalog.Create(
            baseline.Revision.Source,
            baseline.Settings,
            species,
            baseline.Aptitudes,
            baseline.NativeProfiles,
            baseline.ExperienceSteps,
            baseline.RebirthSteps,
            baseline.MergeSavvySteps,
            baseline.MergeSavvyLookup,
            baseline.HatchRankSteps,
            baseline.MergeRankLookup,
            baseline.MergeRankSpeciesFactors,
            baseline.MergeRankSpiritSteps,
            baseline.Revision.Sha256);
        lifetimeSource[0] = checked(lifetimeSource[0] + 1);
        Check.True(
            copied.Species[0].LifetimeValues[0] != lifetimeSource[0],
            "pinned pet content owns defensive collection copies");

        var changedAptitudes = baseline.Aptitudes
            .Select((value, index) => index == 0
                ? value with { DisplayName = value.DisplayName + " changed" }
                : value)
            .ToArray();
        var changed = PinnedPetContentCatalog.Create(
            baseline.Revision.Source,
            baseline.Settings,
            baseline.Species,
            changedAptitudes,
            baseline.NativeProfiles,
            baseline.ExperienceSteps,
            baseline.RebirthSteps,
            baseline.MergeSavvySteps,
            baseline.MergeSavvyLookup,
            baseline.HatchRankSteps,
            baseline.MergeRankLookup,
            baseline.MergeRankSpeciesFactors,
            baseline.MergeRankSpiritSteps);
        Check.True(
            changed.Revision.Sha256 != baseline.Revision.Sha256,
            "every active aptitude fact contributes to the pet revision hash");

        var changedTalentMasks = baseline.Aptitudes
            .Select((value, index) => index == 0
                ? value with
                {
                    InnateTalentMask = checked((short)(
                        value.InnateTalentMask + 1))
                }
                : value)
            .ToArray();
        var talentMaskHash = PetContentRevisionHasher.Compute(
            baseline.Settings,
            baseline.Species,
            changedTalentMasks,
            baseline.NativeProfiles,
            baseline.ExperienceSteps,
            baseline.RebirthSteps,
            baseline.MergeSavvySteps,
            baseline.MergeSavvyLookup,
            baseline.HatchRankSteps,
            baseline.MergeRankLookup,
            baseline.MergeRankSpeciesFactors,
            baseline.MergeRankSpiritSteps);
        Check.True(
            talentMaskHash != baseline.Revision.Sha256,
            "innate talent masks contribute to the pet revision hash");

        AssertHashMismatchRejected(baseline);
        AssertMissingItemReferenceRejected(baseline);
    }

    private static void AssertCalmCompatibilityProfiles(
        PinnedPetContentCatalog baseline)
    {
        Check.Equal(
            PetNativeAptitudeProfileCatalog.ProfileCount,
            PetNativeAptitudeProfileCatalog.All.Count,
            "stock native profile count remains fingerprint-compatible");
        Check.Equal(
            PetNativeAptitudeProfileCatalog.ProfileCount +
                PetSpeciesCatalog.SpeciesCount,
            baseline.NativeProfiles.Count,
            "the database baseline adds one Calm compatibility profile per species");

        foreach (var species in PetSpeciesCatalog.All)
        {
            Check.True(
                !PetNativeAptitudeProfileCatalog.TryGet(
                    species.Type,
                    PetAptitude.Calm,
                    out _),
                $"{species.DisplayName} stock aptitude 6 remains absent");
            if (!PetNativeAptitudeProfileCatalog.TryGet(
                    species.Type,
                    PetAptitude.Rational,
                    out var source))
            {
                throw new InvalidDataException(
                    $"{species.DisplayName} Rational compatibility source did not resolve.");
            }
            if (!baseline.TryGetNativeProfile(
                    species.Type,
                    (short)PetAptitude.Calm,
                    out var calm))
            {
                throw new InvalidDataException(
                    $"{species.DisplayName} Calm compatibility profile did not resolve.");
            }
            Check.True(
                calm == new PetNativeProfileContentDefinition(
                    checked((short)source.SpeciesType),
                    (short)PetAptitude.Calm,
                    new PetContentStatVector(
                        source.StartingTraits.Agility,
                        source.StartingTraits.Strength,
                        source.StartingTraits.Accuracy,
                        source.StartingTraits.Technique,
                        source.StartingTraits.Wisdom,
                        source.StartingTraits.Luck),
                    new PetContentStatVector(
                        source.GeniusTraits.Agility,
                        source.GeniusTraits.Strength,
                        source.GeniusTraits.Accuracy,
                        source.GeniusTraits.Technique,
                        source.GeniusTraits.Wisdom,
                        source.GeniusTraits.Luck),
                    source.NativeQuality,
                    source.NativeSamsara,
                    source.NativeGenius,
                    source.StarterSkillId,
                    source.NativeSkillCount,
                    source.NativeProcreate,
                    source.Lifetime),
                $"{species.DisplayName} Calm uses the documented Rational wire defaults");
        }
    }

    private static void AssertHashMismatchRejected(
        PinnedPetContentCatalog baseline)
    {
        try
        {
            _ = PinnedPetContentCatalog.Create(
                baseline.Revision.Source,
                baseline.Settings,
                baseline.Species,
                baseline.Aptitudes,
                baseline.NativeProfiles,
                baseline.ExperienceSteps,
                baseline.RebirthSteps,
                baseline.MergeSavvySteps,
                baseline.MergeSavvyLookup,
                baseline.HatchRankSteps,
                baseline.MergeRankLookup,
                baseline.MergeRankSpeciesFactors,
                baseline.MergeRankSpiritSteps,
                new string('0', 64));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Pinned pet content accepted a mismatched revision hash.");
    }

    private static void AssertMissingItemReferenceRejected(
        PinnedPetContentCatalog baseline)
    {
        var missingEgg = baseline.Species
            .Select(static value => value.EggItemId)
            .First(static value => value.HasValue)!.Value;
        var incomplete = CreateReferencedItemCatalog(
            baseline,
            missingEgg);
        try
        {
            baseline.ValidateItemReferences(incomplete);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "absent from the process-pinned item revision",
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            "Pet content accepted missing cross-catalog item references.");
    }

    private static PinnedItemTemplateCatalog CreateReferencedItemCatalog(
        PinnedPetContentCatalog baseline,
        uint? omit = null)
    {
        var ids = baseline.Species
            .Select(static value => value.EggItemId)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Concat(baseline.Species.Select(
                static value => value.MagicJadeItemId))
            .Concat(
            [
                baseline.Settings.MergeSpiritItemId,
                baseline.Settings.RestrictedMergeSpiritItemId,
                baseline.Settings.RebirthSpiritItemId,
                baseline.Settings.RestrictedRebirthSpiritItemId
            ])
            .Concat(baseline.RebirthSteps.Select(
                static value => value.ChanceItemId))
            .Where(value => value != omit)
            .Distinct()
            .Order()
            .Select(static value => new ItemTemplateDefinition(
                value,
                "consume item",
                $"PetReference{value}",
                $"Pet Reference {value}",
                0,
                [0],
                null,
                null,
                null,
                null,
                string.Empty,
                string.Empty,
                "{}"))
            .ToArray();
        return PinnedItemTemplateCatalog.Create(
            "pet-reference-check",
            ids);
    }

    private static void AssertContains(
        string value,
        params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            Check.True(
                value.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                $"pet-content migration contains {fragment}");
        }
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "GodswarServer.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root for pet-content checks.");
    }
}
