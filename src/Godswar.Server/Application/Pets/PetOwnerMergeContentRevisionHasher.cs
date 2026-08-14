using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.Pets;

internal static class PetOwnerMergeContentRevisionHasher
{
    public static string Compute(
        string source,
        string policyVersion,
        IReadOnlyList<PetOwnerMergeEffectBaseContentDefinition> effectBases,
        IReadOnlyList<PetOwnerMergeBandContentDefinition> bands,
        IReadOnlyList<PetOwnerMergeRateContentDefinition> rates)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "pet-owner-merge-content-v1");
        Append(hash, source);
        Append(hash, policyVersion);
        Append(hash, effectBases.Count);
        foreach (var value in effectBases.OrderBy(static value => value.Effect))
        {
            Append(hash, (short)value.Effect);
            Append(hash, value.BaseValue);
        }

        Append(hash, bands.Count);
        foreach (var value in bands.OrderBy(static value => value.BandIndex))
        {
            Append(hash, value.BandIndex);
            Append(hash, value.MinimumSavvy);
            AppendNullable(hash, value.MaximumSavvy);
        }

        Append(hash, rates.Count);
        foreach (var value in rates
                     .OrderBy(static value => value.SourceSavvy)
                     .ThenBy(static value => value.Effect)
                     .ThenBy(static value => value.BandIndex))
        {
            Append(hash, (short)value.SourceSavvy);
            Append(hash, (short)value.Effect);
            Append(hash, value.BandIndex);
            Append(hash, value.RatePerSavvy);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, decimal value) =>
        Append(hash, value.ToString("G29", CultureInfo.InvariantCulture));

    private static void AppendNullable(IncrementalHash hash, decimal? value)
    {
        hash.AppendData([value.HasValue ? (byte)1 : (byte)0]);
        if (value.HasValue)
        {
            Append(hash, value.Value);
        }
    }

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
}
