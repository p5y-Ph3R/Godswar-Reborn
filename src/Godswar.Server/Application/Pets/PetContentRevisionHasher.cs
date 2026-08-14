using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.Pets;

internal static class PetContentRevisionHasher
{
    public static string Compute(
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
            mergeRankSpiritSteps)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mergeSavvySteps);
        ArgumentNullException.ThrowIfNull(mergeSavvyLookup);
        ArgumentNullException.ThrowIfNull(hatchRankSteps);
        ArgumentNullException.ThrowIfNull(mergeRankLookup);
        ArgumentNullException.ThrowIfNull(mergeRankSpeciesFactors);
        ArgumentNullException.ThrowIfNull(mergeRankSpiritSteps);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "pet-content-manifest-v5");
        AppendSettings(hash, settings);

        Append(hash, species.Count);
        foreach (var value in species.OrderBy(static value => value.SpeciesId))
        {
            Append(hash, value.SpeciesId);
            Append(hash, value.DisplayName);
            Append(hash, value.FoodKind);
            Append(hash, value.StarterSkillId);
            Append(hash, value.StarterSkillName);
            Append(hash, value.LifetimeValues.Count);
            foreach (var lifetime in value.LifetimeValues)
            {
                Append(hash, lifetime);
            }
            AppendNullable(hash, value.EggItemId);
            AppendNullable(hash, value.EggDeclaredSpeciesId);
            Append(hash, value.MagicJadeItemId);
        }

        Append(hash, aptitudes.Count);
        foreach (var value in aptitudes.OrderBy(static value => value.Aptitude))
        {
            Append(hash, value.Aptitude);
            Append(hash, value.NameKey);
            Append(hash, value.DisplayName);
            Append(hash, value.IsServerExtension);
            Append(hash, value.MinimumTotalGrowth);
            Append(hash, value.MaximumTotalGrowth);
            Append(hash, value.MaximumGrowthStatDeviation);
            Append(hash, value.MinimumInitialSavvy);
            Append(hash, value.MaximumInitialSavvy);
            Append(hash, value.MaximumInitialSavvyStatDeviation);
            Append(hash, value.MinimumAddedSavvy);
            Append(hash, value.MaximumAddedSavvy);
            Append(hash, value.InnateTalentMask);
        }

        Append(hash, nativeProfiles.Count);
        foreach (var value in nativeProfiles
                     .OrderBy(static value => value.SpeciesId)
                     .ThenBy(static value => value.Aptitude))
        {
            Append(hash, value.SpeciesId);
            Append(hash, value.Aptitude);
            Append(hash, value.StartingTraits);
            Append(hash, value.GeniusTraits);
            Append(hash, value.NativeQuality);
            Append(hash, value.NativeSamsara);
            Append(hash, value.NativeGenius);
            Append(hash, value.StarterSkillId);
            Append(hash, value.NativeSkillCount);
            Append(hash, value.NativeProcreate);
            Append(hash, value.Lifetime);
        }

        Append(hash, experienceSteps.Count);
        foreach (var value in experienceSteps.OrderBy(
                     static value => value.CurrentLevel))
        {
            Append(hash, value.CurrentLevel);
            Append(hash, value.RequiredExperience);
        }

        Append(hash, rebirthSteps.Count);
        foreach (var value in rebirthSteps.OrderBy(
                     static value => value.RebirthNumber))
        {
            Append(hash, value.RebirthNumber);
            Append(hash, value.RequiredPetLevel);
            Append(hash, value.ChanceItemId);
            Append(hash, value.ChanceItemName);
            Append(hash, value.MinimumIncreasePerStat);
            Append(hash, value.MaximumIncreasePerStat);
        }

        Append(hash, mergeSavvySteps.Count);
        foreach (var value in mergeSavvySteps
                     .OrderBy(static value => value.Aptitude)
                     .ThenBy(static value => value.SpiritCount))
        {
            Append(hash, value.Aptitude);
            Append(hash, value.SpiritCount);
            Append(hash, value.MinimumIncreasePerStat);
            Append(hash, value.MaximumIncreasePerStat);
        }

        Append(hash, mergeSavvyLookup.Count);
        foreach (var value in mergeSavvyLookup.OrderBy(
                     static value => value.MinimumSavvyDifference))
        {
            Append(hash, value.MinimumSavvyDifference);
            Append(hash, value.BaseIncrease);
        }

        Append(hash, hatchRankSteps.Count);
        foreach (var value in hatchRankSteps
                     .OrderBy(static value => value.Aptitude)
                     .ThenBy(static value => value.OutcomeOrder))
        {
            Append(hash, value.Aptitude);
            Append(hash, value.OutcomeOrder);
            Append(hash, value.Rank);
            Append(hash, value.Weight);
        }

        Append(hash, mergeRankLookup.Count);
        foreach (var value in mergeRankLookup.OrderBy(
                     static value => value.MinimumRankDifference))
        {
            Append(hash, value.MinimumRankDifference);
            Append(hash, value.BaseIncrease);
        }

        Append(hash, mergeRankSpeciesFactors.Count);
        foreach (var value in mergeRankSpeciesFactors.OrderBy(
                     static value => value.SpeciesId))
        {
            Append(hash, value.SpeciesId);
            Append(hash, value.Factor);
        }

        Append(hash, mergeRankSpiritSteps.Count);
        foreach (var value in mergeRankSpiritSteps.OrderBy(
                     static value => value.SpiritCount))
        {
            Append(hash, value.SpiritCount);
            Append(hash, value.MinimumPercent);
            Append(hash, value.MaximumPercent);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendSettings(
        IncrementalHash hash,
        PetContentSettings value)
    {
        Append(hash, value.MinimumLevel);
        Append(hash, value.MaximumLevel);
        Append(hash, value.MaximumOwnedPetCount);
        Append(hash, value.MaximumSkillCount);
        Append(hash, value.MaximumRank);
        Append(hash, value.MinimumMergeLevel);
        Append(hash, value.MinimumOwnerMergeAmity);
        Append(hash, value.MaximumSpiritItems);
        Append(hash, value.MaximumRebirthCount);
        Append(hash, value.RequiredRebirthSpiritCount);
        Append(hash, value.EggHatchRuntimeSkillId);
        Append(hash, value.MergeSpiritItemId);
        Append(hash, value.RestrictedMergeSpiritItemId);
        Append(hash, value.RebirthSpiritItemId);
        Append(hash, value.RestrictedRebirthSpiritItemId);
        Append(hash, value.GrowthPolicyVersion);
        Append(hash, value.InitialSavvyPolicyVersion);
        Append(hash, value.AddedSavvyPolicyVersion);
        Append(hash, value.AddedSavvyWeights.Count);
        foreach (var weight in value.AddedSavvyWeights)
        {
            Append(hash, weight);
        }
    }

    private static void Append(
        IncrementalHash hash,
        PetContentStatVector value)
    {
        Append(hash, value.Agility);
        Append(hash, value.Strength);
        Append(hash, value.Accuracy);
        Append(hash, value.Technique);
        Append(hash, value.Wisdom);
        Append(hash, value.Luck);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, decimal value) =>
        Append(hash, value.ToString("G29", CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, bool value) =>
        hash.AppendData([value ? (byte)1 : (byte)0]);

    private static void Append(IncrementalHash hash, short value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendNullable(IncrementalHash hash, uint? value)
    {
        Append(hash, value.HasValue);
        if (value.HasValue)
        {
            Append(hash, value.Value);
        }
    }

    private static void AppendNullable(IncrementalHash hash, short? value)
    {
        Append(hash, value.HasValue);
        if (value.HasValue)
        {
            Append(hash, value.Value);
        }
    }
}
