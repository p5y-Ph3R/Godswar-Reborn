namespace Godswar.Server.Application.World;

internal static partial class WorldContentRevisionHasher
{
    /// <summary>
    /// Exact legacy gameplay-v2 hash used only to authenticate the predecessor
    /// during the forward v3 publication transition.
    /// </summary>
    internal static WorldContentFamilyRevision HashGameplayV2ForUpgrade(
        GameplayContentCatalog content)
    {
        using var hash = new CanonicalHashBuilder("gameplay-v2");
        HashGameplayV2Maps(hash, content.Maps);
        HashAddressPoints(hash, content.AddressPoints);
        HashLinks(hash, content.Links);
        HashGameplayV2MonsterTemplates(hash, content.MonsterTemplates);
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

    private static void HashGameplayV2Maps(
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

    private static void HashGameplayV2MonsterTemplates(
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
}
