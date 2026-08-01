using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal readonly record struct MountRideDefinition(
    uint ItemId,
    string DisplayName,
    int MountLevel,
    uint StatusId,
    float SpeedBonus);

internal readonly record struct MountRideActivationCommit(
    GameCharacter Character,
    int CurrentMana,
    bool StatusChanged);

/// <summary>
/// Native mount facts sourced from ItemBaseAttribute.xml, Magic.ini,
/// Status.ini, and Ride.ini. Every grantable client mount is mapped to the
/// matching Ride.ini visual status; item IDs remain authoritative because
/// several client NameKey and display-name values are intentionally reused.
/// </summary>
internal sealed class MountCatalog
{
    public const int RideSkillId = 4904;

    public const int RuntimeStatusKind = 110;

    public const int RideManaCost = 50;

    public static readonly TimeSpan RideCastTime = TimeSpan.FromSeconds(6);

    public static readonly TimeSpan RideCooldown = TimeSpan.FromSeconds(6);

    private readonly FrozenDictionary<uint, MountRideDefinition>
        _rideDefinitions;

    private readonly FrozenDictionary<uint, float[]>
        _rideQualitySpeedBonuses;

    public MountCatalog(
        IItemTemplateCatalog templates,
        DeveloperMountCatalog developerMounts)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(developerMounts);
        _rideDefinitions = developerMounts.Grantable
            .Select(static mount => (Mount: mount, StatusId: ResolveStatusId(mount.ItemId)))
            .Where(static candidate => candidate.StatusId.HasValue)
            .Select(static candidate => new MountRideDefinition(
                candidate.Mount.ItemId,
                candidate.Mount.DisplayName,
                candidate.Mount.RequiredLevel,
                candidate.StatusId!.Value,
                Math.Max(0f, candidate.Mount.SpeedBonus)))
            .ToFrozenDictionary(static definition => definition.ItemId);

        _rideQualitySpeedBonuses = templates.All
            .Where(static template =>
                template.Id > 0 &&
                string.Equals(template.Kind, "mount", StringComparison.OrdinalIgnoreCase))
            .Select(static template => (
                ItemId: (uint)template.Id,
                Values: ReadFloatVector(template.StatsJson, "Speed")))
            .Where(static candidate => candidate.Values.Length > 0)
            .ToFrozenDictionary(
                static candidate => candidate.ItemId,
                static candidate => candidate.Values);
    }

    public bool TryGetRideDefinition(uint itemId, out MountRideDefinition definition) =>
        _rideDefinitions.TryGetValue(itemId, out definition);

    public bool TryGetEquippedRideDefinition(
        GameCharacter character,
        out MountRideDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(character);
        var mount = EquipmentSlots.GetItem(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Mount);
        if (!TryGetRideDefinition(mount.Id, out definition))
        {
            return false;
        }

        if (_rideQualitySpeedBonuses.TryGetValue(mount.Id, out var speedBonuses))
        {
            var qualityIndex = Math.Clamp(
                Math.Max((int)mount.Quality, 1) - 1,
                0,
                speedBonuses.Length - 1);
            definition = definition with
            {
                SpeedBonus = Math.Max(0f, speedBonuses[qualityIndex])
            };
        }

        return true;
    }

    private static float[] ReadFloatVector(string statsJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var property))
            {
                return [];
            }

            var values = property.GetString()?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static value =>
                    float.TryParse(
                        value.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsed) &&
                    float.IsFinite(parsed)
                        ? parsed
                        : float.NaN)
                .ToArray() ?? [];
            return values.All(static value => float.IsFinite(value))
                ? values
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static uint? ResolveStatusId(uint itemId)
    {
        if (itemId is >= 6000 and <= 6005)
        {
            return 1100 + (itemId - 6000);
        }

        if (itemId is >= 6010 and <= 6015)
        {
            return 1110 + (itemId - 6010);
        }

        if (itemId is >= 6020 and <= 6026)
        {
            return 1120 + (itemId - 6020);
        }

        if (itemId is >= 6030 and <= 6035)
        {
            return 1130 + (itemId - 6030);
        }

        if (itemId == 6040)
        {
            return 1149;
        }

        if (itemId is >= 6041 and <= 6046)
        {
            return 1140 + (itemId - 6041);
        }

        var tieredStatus = ResolveSixStatusFamily(itemId, 14220, 1100) ??
                           ResolveSixStatusFamily(itemId, 14240, 1110) ??
                           ResolveSixStatusFamily(itemId, 14260, 1120) ??
                           ResolveSixStatusFamily(itemId, 14280, 1140) ??
                           ResolveSixStatusFamily(itemId, 14300, 1130) ??
                           ResolveSixStatusFamily(itemId, 14320, 1150) ??
                           ResolveSixStatusFamily(itemId, 14340, 1160) ??
                           ResolveSixStatusFamily(itemId, 14360, 1170) ??
                           ResolveSixStatusFamily(itemId, 14380, 1180) ??
                           ResolveSixStatusFamily(itemId, 14400, 1190);
        if (tieredStatus.HasValue)
        {
            return tieredStatus;
        }

        if (itemId is >= 14440 and <= 14449)
        {
            // Ride.ini has one status for every normal Leatherback level.
            // The separate 50%-speed item reuses the final normal visual.
            return 1201 + Math.Min(itemId - 14440, 8);
        }

        var pairedStatus = ResolveTwoStatusFamily(itemId, 14460, 1220) ??
                           ResolveTwoStatusFamily(itemId, 14480, 1230) ??
                           ResolveTwoStatusFamily(itemId, 14490, 1310) ??
                           ResolveTwoStatusFamily(itemId, 14510, 1410) ??
                           ResolveTwoStatusFamily(itemId, 14520, 1330) ??
                           ResolveTwoStatusFamily(itemId, 16000, 1240) ??
                           ResolveTwoStatusFamily(itemId, 16020, 1250) ??
                           ResolveTwoStatusFamily(itemId, 16040, 1260) ??
                           ResolveTwoStatusFamily(itemId, 16060, 1270) ??
                           ResolveTwoStatusFamily(itemId, 16080, 1280) ??
                           ResolveTwoStatusFamily(itemId, 16100, 1290) ??
                           ResolveTwoStatusFamily(itemId, 16120, 1300) ??
                           ResolveTwoStatusFamily(itemId, 16130, 1340) ??
                           ResolveTwoStatusFamily(itemId, 16140, 1350) ??
                           ResolveTwoStatusFamily(itemId, 16150, 1360) ??
                           ResolveTwoStatusFamily(itemId, 16160, 1370) ??
                           ResolveTwoStatusFamily(itemId, 16170, 1380) ??
                           ResolveTwoStatusFamily(itemId, 16180, 1420) ??
                           ResolveTwoStatusFamily(itemId, 16190, 1430);
        if (pairedStatus.HasValue)
        {
            return pairedStatus;
        }

        if (itemId is >= 16200 and <= 16209)
        {
            // Custom Erebus Lion family: all authored levels share the one
            // animation-safe black-lion visual in Ride.ini section 117. The
            // status ID occupies an unused gap in the stock client catalog;
            // locomotion speed is synchronized separately through opcode 10166.
            return 1390;
        }

        return itemId switch
        {
            14420 => 1146,
            14421 => 1136,
            14422 => 1176,
            14423 => 1186,
            14424 => 1166,
            14425 => 1210,
            14426 => 1137,
            _ => null
        };
    }

    private static uint? ResolveSixStatusFamily(
        uint itemId,
        uint familyBaseItemId,
        uint familyBaseStatusId)
    {
        if (itemId < familyBaseItemId || itemId > familyBaseItemId + 9)
        {
            return null;
        }

        return familyBaseStatusId + Math.Min(itemId - familyBaseItemId, 5);
    }

    private static uint? ResolveTwoStatusFamily(
        uint itemId,
        uint familyBaseItemId,
        uint familyBaseStatusId)
    {
        if (itemId < familyBaseItemId || itemId > familyBaseItemId + 9)
        {
            return null;
        }

        return familyBaseStatusId + (itemId - familyBaseItemId >= 5 ? 1u : 0u);
    }
}
