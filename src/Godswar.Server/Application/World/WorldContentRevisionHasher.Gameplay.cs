using System.Globalization;

namespace Godswar.Server.Application.World;

internal static partial class WorldContentRevisionHasher
{
    public static WorldContentFamilyRevision HashGameplay(
        GameplayContentCatalog content)
    {
        using var hash = new CanonicalHashBuilder("gameplay-v2");
        HashMaps(hash, content.Maps);
        HashAddressPoints(hash, content.AddressPoints);
        HashLinks(hash, content.Links);
        HashMonsterTemplates(hash, content.MonsterTemplates);
        HashWorldBosses(hash, content.WorldBosses);
        HashPendingWorldBosses(hash, content.PendingWorldBossAreas);
        HashClasses(hash, content.Classes);
        HashTalentEffects(hash, content.TalentEffects);
        HashTalents(hash, content.Talents);
        HashSkills(hash, content.SkillCombatDefinitions);
        HashSkillBooks(hash, content.SkillBooks);
        return new WorldContentFamilyRevision(
            "gameplay",
            hash.Finish(),
            checked(
                content.Maps.Count +
                content.AddressPoints.Count +
                content.Links.Count +
                content.MonsterTemplates.Count +
                content.WorldBosses.Count +
                content.PendingWorldBossAreas.Count +
                content.SkillCombatDefinitions.Count +
                content.Classes.Count +
                content.TalentEffects.Count +
                content.Talents.Count +
                content.SkillBooks.Count));
    }

    private static void HashMaps(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplayMapDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt16(value.MapId);
            hash.AppendString(value.SceneKey);
            hash.AppendString(value.DisplayName);
            hash.AppendInt32(value.ClientSceneId.HasValue ? 1 : 0);
            if (value.ClientSceneId.HasValue)
            {
                hash.AppendInt32(value.ClientSceneId.Value);
            }
        }
    }

    private static void HashAddressPoints(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplayMapAddressPointDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt16(value.MapId);
            hash.AppendInt16(value.GroupIndex);
            hash.AppendInt16(value.PointIndex);
            hash.AppendString(value.GroupName);
            hash.AppendString(value.Name);
            hash.AppendSingle(value.X);
            hash.AppendSingle(value.Z);
            hash.AppendString(value.Source);
        }
    }

    private static void HashLinks(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplayMapLinkDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt16(value.SourceMapId);
            hash.AppendInt16(value.LinkIndex);
            hash.AppendInt16(value.TargetMapId);
            hash.AppendSingle(value.X);
            hash.AppendSingle(value.Z);
            hash.AppendString(value.Source);
            hash.AppendInt32((int)value.Confidence);
            hash.AppendInt32((int)value.Activation);
            hash.AppendString(value.Note);
        }
    }

    private static void HashMonsterTemplates(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplayMonsterTemplateDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendString(value.SourceKey);
            hash.AppendString(value.SourceKind);
            hash.AppendInt32(value.SourceMapId ?? int.MinValue);
            hash.AppendString(value.SceneKey);
            hash.AppendString(value.TemplateKey);
            hash.AppendString(value.DisplayName);
            hash.AppendString(value.Rank);
            hash.AppendInt32(value.IsBoss ? 1 : 0);
            hash.AppendInt32(value.IsElite ? 1 : 0);
            hash.AppendInt32(value.IsPet ? 1 : 0);
            hash.AppendInt32(value.CollisionRange.HasValue ? 1 : 0);
            if (value.CollisionRange.HasValue)
            {
                hash.AppendSingle(value.CollisionRange.Value);
            }
        }
    }

    private static void HashWorldBosses(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplayWorldBossDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt16(value.MapId);
            hash.AppendString(value.SceneKey);
            hash.AppendString(value.TemplateKey);
            hash.AppendString(value.DisplayName);
            hash.AppendInt32(value.BonusBasisPoints);
            hash.AppendInt32(checked((int)value.RespawnInterval.TotalSeconds));
        }
    }

    private static void HashPendingWorldBosses(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplayPendingWorldBossArea> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt16(value.MapId);
            hash.AppendString(value.SceneKey);
            hash.AppendString(value.Reason);
        }
    }

    private static void HashSkills(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplaySkillCombatDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt32(value.SkillId);
            hash.AppendInt32(value.Target);
            hash.AppendInt32(value.AffectObj);
            hash.AppendSingle(value.Distance);
            hash.AppendSingle(value.Range);
            hash.AppendInt32(value.Property);
            hash.AppendInt32(value.Mp);
            hash.AppendString(value.Power1.ToString(
                "G29",
                CultureInfo.InvariantCulture));
            hash.AppendString(value.Power2.ToString(
                "G29",
                CultureInfo.InvariantCulture));
            hash.AppendInt64(value.CastTime.Ticks);
            hash.AppendInt64(value.Cooldown.Ticks);
            hash.AppendString(value.DisplayName);
            hash.AppendString(value.BaseName);
            AppendOptionalInt16(hash, value.SkillLevel);
            hash.AppendInt32(value.ClassIds.Count);
            foreach (var classId in value.ClassIds)
            {
                hash.AppendInt16(classId);
            }
            AppendOptionalInt32(hash, value.PreviousSkillId);
            AppendOptionalInt32(hash, value.MinLevel);
            AppendOptionalInt32(hash, value.MaxLevel);
            hash.AppendString(value.Description);
            hash.AppendString(value.StatsJson);
        }
    }

    private static void HashClasses(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplayClassDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt16(value.Id);
            hash.AppendString(value.Name);
            hash.AppendString(value.DisplayName);
            hash.AppendString(value.Source);
        }
    }

    private static void HashTalentEffects(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplayTalentEffectDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt16(value.Id);
            hash.AppendString(value.Key);
            hash.AppendString(value.DisplayName);
            hash.AppendInt32(value.Percent ? 1 : 0);
        }
    }

    private static void HashTalents(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplayTalentDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt32(value.Id);
            hash.AppendInt16(value.ClassId);
            hash.AppendInt16(value.TreeOrder);
            hash.AppendString(value.Name);
            hash.AppendInt32(value.PrefixId);
            hash.AppendInt32(value.RequiredPrefixRank);
            hash.AppendInt32(value.RequiredTotalRank);
            hash.AppendInt32(value.EquipRequest);
            hash.AppendString(value.EffectType);
            hash.AppendInt16(value.EffectId);
            hash.AppendString(value.EffectValue.ToString(
                "G29",
                CultureInfo.InvariantCulture));
            hash.AppendInt32(value.IsPercent ? 1 : 0);
            hash.AppendInt32(value.IconX);
            hash.AppendInt32(value.IconY);
            hash.AppendInt32(value.IconWidth);
            hash.AppendInt32(value.IconHeight);
            hash.AppendString(value.StatsJson);
        }
    }

    private static void HashSkillBooks(
        CanonicalHashBuilder hash,
        IReadOnlyList<GameplaySkillBookDefinition> values)
    {
        hash.AppendInt32(values.Count);
        foreach (var value in values)
        {
            hash.AppendInt32(value.ItemId);
            hash.AppendString(value.NameKey);
            hash.AppendString(value.DisplayName);
            hash.AppendInt32(value.SkillId);
            hash.AppendString(value.BaseName);
            AppendOptionalInt16(hash, value.SkillLevel);
            hash.AppendInt32(value.ClassIds.Count);
            foreach (var classId in value.ClassIds)
            {
                hash.AppendInt16(classId);
            }
            AppendOptionalInt32(hash, value.MinLevel);
            AppendOptionalInt32(hash, value.MaxLevel);
            AppendOptionalInt32(hash, value.PreviousSkillId);
            hash.AppendString(value.StatsJson);
        }
    }

    private static void AppendOptionalInt16(
        CanonicalHashBuilder hash,
        short? value)
    {
        hash.AppendInt32(value.HasValue ? 1 : 0);
        if (value.HasValue)
        {
            hash.AppendInt16(value.Value);
        }
    }

    private static void AppendOptionalInt32(
        CanonicalHashBuilder hash,
        int? value)
    {
        hash.AppendInt32(value.HasValue ? 1 : 0);
        if (value.HasValue)
        {
            hash.AppendInt32(value.Value);
        }
    }
}
