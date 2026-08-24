using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.Warehouse;

internal readonly record struct WarehouseExpansionPolicyLevel(
    int Capacity,
    int KeyCost,
    int KeyItemId);

/// <summary>
/// Startup-pinned warehouse expansion balance. PostgreSQL owns the concrete
/// key identity and costs; compiled code enforces only native capacity and
/// bounded-value invariants.
/// </summary>
internal sealed record WarehouseExpansionPolicySnapshot(
    long Revision,
    string Sha256,
    IReadOnlyList<WarehouseExpansionPolicyLevel> Levels)
{
    public const int MaximumKeyCost = 99;

    public int MaximumCapacity =>
        Levels?.Count > 0
            ? Levels.Max(static level => level.Capacity)
            : 0;

    public void Validate()
    {
        if (Revision <= 0 ||
            string.IsNullOrWhiteSpace(Sha256) ||
            Levels is null ||
            Levels.Count is < 1 or >
                WarehouseCapacityPolicy.MaximumSupportedBoxCount ||
            !string.Equals(
                Sha256,
                ComputeSha256(Levels),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The warehouse expansion policy is invalid.");
        }

        var ordered = Levels
            .OrderBy(static level => level.Capacity)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var level = ordered[index];
            var expectedCapacity = checked(
                WarehouseCapacityPolicy.DefaultCapacity +
                index * WarehouseCapacityPolicy.SlotsPerBox);
            if (level.Capacity != expectedCapacity ||
                !WarehouseCapacityPolicy.IsValidCapacity(level.Capacity) ||
                level.KeyItemId <= 0 ||
                index == 0 && level.KeyCost != 0 ||
                index > 0 && level.KeyCost is < 1 or > MaximumKeyCost)
            {
                throw new InvalidDataException(
                    "Warehouse expansion levels are incomplete or unsafe.");
            }
        }
    }

    public WarehouseExpansionPolicyLevel ForCapacity(int capacity)
    {
        Validate();
        return Levels.Single(level => level.Capacity == capacity);
    }

    public WarehouseExpansionPolicyLevel NextLevelForCapacity(int capacity)
    {
        Validate();
        if (capacity >= MaximumCapacity)
        {
            return ForCapacity(MaximumCapacity);
        }

        return ForCapacity(WarehouseCapacityPolicy.NextCapacity(capacity));
    }

    public string CoordinationRevision()
    {
        Validate();
        return Sha256;
    }

    public static string ComputeSha256(
        IEnumerable<WarehouseExpansionPolicyLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var builder = new StringBuilder(
            "warehouse-expansion-policy-v1\n");
        foreach (var level in levels.OrderBy(static value => value.Capacity))
        {
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "level:{0},{1},{2}\n",
                level.Capacity,
                level.KeyCost,
                level.KeyItemId);
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
