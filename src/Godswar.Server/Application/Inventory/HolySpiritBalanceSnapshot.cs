using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.Application.Inventory;

/// <summary>
/// One startup-pinned view of the mutable PostgreSQL Holy Spirit balance row.
/// All worker paths retain this revision until the coordinated restart that
/// activates a later management update.
/// </summary>
internal sealed record HolySpiritBalanceSnapshot(
    int CooledPhysicalReductionGradeOneMaximum,
    int CooledMagicReductionGradeOneMaximum,
    int CooledCriticalReductionGradeOneMaximum,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy)
{
    public static HolySpiritBalanceSnapshot HistoricalAcceptanceEnvelope
        { get; } = new(
            HolySpiritImplementationPolicy
                .CooledPhysicalReductionGradeOneAcceptedMaximum,
            HolySpiritImplementationPolicy
                .CooledMagicReductionGradeOneAcceptedMaximum,
            HolySpiritImplementationPolicy
                .CooledCriticalReductionGradeOneAcceptedMaximum,
            0,
            DateTimeOffset.UnixEpoch,
            "compiled-historical-envelope");

    public void Validate()
    {
        ValidateMaximum(
            CooledPhysicalReductionGradeOneMaximum,
            22,
            HolySpiritImplementationPolicy
                .CooledPhysicalReductionGradeOneAcceptedMaximum,
            nameof(CooledPhysicalReductionGradeOneMaximum));
        ValidateMaximum(
            CooledMagicReductionGradeOneMaximum,
            22,
            HolySpiritImplementationPolicy
                .CooledMagicReductionGradeOneAcceptedMaximum,
            nameof(CooledMagicReductionGradeOneMaximum));
        ValidateMaximum(
            CooledCriticalReductionGradeOneMaximum,
            28,
            HolySpiritImplementationPolicy
                .CooledCriticalReductionGradeOneAcceptedMaximum,
            nameof(CooledCriticalReductionGradeOneMaximum));
        ArgumentOutOfRangeException.ThrowIfNegative(Revision);
        if (UpdatedAtUtc == default ||
            string.IsNullOrWhiteSpace(UpdatedBy) ||
            UpdatedBy.Length > 128)
        {
            throw new InvalidDataException(
                "Holy Spirit balance provenance is invalid.");
        }
    }

    public int GradeOneMaximumFor(short effectId) => effectId switch
    {
        HolySpiritImplementationPolicy
            .CooledPhysicalDamageReductionEffectId =>
            CooledPhysicalReductionGradeOneMaximum,
        HolySpiritImplementationPolicy
            .CooledMagicDamageReductionEffectId =>
            CooledMagicReductionGradeOneMaximum,
        HolySpiritImplementationPolicy
            .CooledCriticalDamageReductionEffectId =>
            CooledCriticalReductionGradeOneMaximum,
        _ => throw new ArgumentOutOfRangeException(
            nameof(effectId),
            effectId,
            "The Holy Spirit effect is not management-adjustable.")
    };

    public string CoordinationRevision()
    {
        Validate();
        var canonical = Encoding.UTF8.GetBytes(
            "holy-spirit-balance-v1\n" +
            $"revision:{Revision.ToString(CultureInfo.InvariantCulture)}\n" +
            "cooled-physical:" +
            CooledPhysicalReductionGradeOneMaximum.ToString(
                CultureInfo.InvariantCulture) + "\n" +
            "cooled-magic:" +
            CooledMagicReductionGradeOneMaximum.ToString(
                CultureInfo.InvariantCulture) + "\n" +
            "cooled-critical:" +
            CooledCriticalReductionGradeOneMaximum.ToString(
                CultureInfo.InvariantCulture) + "\n");
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static void ValidateMaximum(
        int value,
        int minimum,
        int acceptedMaximum,
        string parameterName)
    {
        if (value < minimum || value > acceptedMaximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The Holy Spirit balance value is outside its safe bounds.");
        }
    }
}
