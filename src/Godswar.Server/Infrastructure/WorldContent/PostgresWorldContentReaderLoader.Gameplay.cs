using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
{
    internal static async Task<GameplayContentCatalog>
        LoadPublishedGameplayContentAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var header = await ReadGameplayHeaderAsync(
            connection,
            transaction,
            cancellationToken);
        return await LoadGameplayRevisionAsync(
            connection,
            transaction,
            header,
            useLegacyV2Hash: false,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Loads the current publication through the exact gameplay-v2 hash domain.
    /// This seam is intentionally restricted to the one-time v2-to-v3 publisher
    /// transition; ordinary runtime reads always validate with the current hash.
    /// </summary>
    internal static async Task<GameplayContentCatalog>
        LoadPublishedGameplayV2ForUpgradeAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var header = await ReadGameplayHeaderAsync(
            connection,
            transaction,
            cancellationToken);
        return await LoadGameplayRevisionAsync(
            connection,
            transaction,
            header,
            useLegacyV2Hash: true,
            cancellationToken: cancellationToken);
    }

    internal static Task<GameplayContentCatalog> LoadGameplayRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        GameplayContentCatalog expectedShape,
        CancellationToken cancellationToken) =>
        LoadGameplayRevisionAsync(
            connection,
            transaction,
            new PublishedGameplayHeader(
                revision,
                expectedShape.Maps.Count,
                expectedShape.AddressPoints.Count,
                expectedShape.Links.Count,
                expectedShape.MonsterTemplates.Count,
                expectedShape.WorldBosses.Count,
                expectedShape.PendingWorldBossAreas.Count,
                expectedShape.Classes.Count,
                expectedShape.TalentEffects.Count,
                expectedShape.Talents.Count,
                expectedShape.SkillCombatDefinitions.Count,
                expectedShape.SkillBooks.Count),
            useLegacyV2Hash: false,
            cancellationToken: cancellationToken);

    private static async Task<GameplayContentCatalog>
        LoadGameplayRevisionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            PublishedGameplayHeader header,
            bool useLegacyV2Hash,
            CancellationToken cancellationToken)
    {
        ValidateGameplayHeader(header);

        var maps = await ReadGameplayMapsAsync(
            connection,
            transaction,
            header.Revision,
            cancellationToken);
        var expectedMapIds = Enumerable.Range(0, 70)
            .Concat(Enumerable.Range(200, 11))
            .Select(static value => (short)value)
            .ToHashSet();
        if (!maps.Select(static value => value.MapId)
                .ToHashSet()
                .SetEquals(expectedMapIds))
        {
            throw GameplayUnavailable(
                "The gameplay publication must contain maps 0-69 and " +
                "200-210 exactly.");
        }

        var content = new GameplayContentCatalog(
            maps,
            await ReadGameplayAddressPointsAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken),
            await ReadGameplayLinksAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken),
            await ReadGameplayMonsterTemplatesAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken),
            await ReadGameplayWorldBossesAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken),
            await ReadGameplayPendingWorldBossesAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken),
            await ReadGameplaySkillsAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken))
        {
            Classes = await ReadGameplayClassesAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken),
            TalentEffects = await ReadGameplayTalentEffectsAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken),
            Talents = await ReadGameplayTalentsAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken),
            SkillBooks = await ReadGameplaySkillBooksAsync(
                connection,
                transaction,
                header.Revision,
                cancellationToken)
        };
        ValidateGameplayCounts(header, content);

        var validator = PinnedWorldContentReader.Create(
            "gameplay-publication-read-validation-v1",
            maps.Select(static value => value.MapId),
            [],
            [],
            [],
            gameplay: content);
        var canonical = validator.Gameplay;
        var revision = useLegacyV2Hash
            ? WorldContentRevisionHasher.HashGameplayV2ForUpgrade(canonical)
            : WorldContentRevisionHasher.HashGameplay(canonical);
        if (!string.Equals(
                revision.Sha256,
                header.Revision,
                StringComparison.Ordinal) ||
            revision.EntryCount != header.EntryCount)
        {
            throw new WorldContentUnavailableException(
                "gameplay",
                WorldContentFailureReason.RevisionMismatch,
                "Published gameplay rows do not match their revision " +
                "pointer and declared counts.");
        }

        return canonical;
    }

    private static async Task<PublishedGameplayHeader>
        ReadGameplayHeaderAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision,
                   release.map_count,
                   release.address_point_count,
                   release.link_count,
                   release.monster_template_count,
                   release.world_boss_count,
                   release.pending_world_boss_count,
                   release.class_count,
                   release.talent_effect_count,
                   release.talent_count,
                   release.skill_count,
                   release.skill_book_count
            FROM gameplay_content_publication publication
            JOIN gameplay_content_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'gameplay';
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorldContentUnavailableException(
                "gameplay",
                WorldContentFailureReason.Missing,
                "No official gameplay publication is available.");
        }

        return new PublishedGameplayHeader(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11));
    }

    private static async Task<GameplayMapDefinition[]> ReadGameplayMapsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var values = new List<GameplayMapDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT map_id, scene_key, display_name, client_scene_id, map_mode
            FROM gameplay_map_definitions
            WHERE revision = @revision
            ORDER BY map_id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayMapDefinition(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt16(4)));
        }

        return values.ToArray();
    }

    private static async Task<GameplayMapAddressPointDefinition[]>
        ReadGameplayAddressPointsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayMapAddressPointDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT map_id, group_index, point_index, group_name, name,
                   pos_x, pos_z, source
            FROM gameplay_map_address_points
            WHERE revision = @revision
            ORDER BY map_id, group_index, point_index;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayMapAddressPointDefinition(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFloat(5),
                reader.GetFloat(6),
                reader.GetString(7)));
        }

        return values.ToArray();
    }

    private static async Task<GameplayMapLinkDefinition[]>
        ReadGameplayLinksAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayMapLinkDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT map_id, link_index, target_map_id, pos_x, pos_z, source,
                   confidence, activation, note
            FROM gameplay_map_links
            WHERE revision = @revision
            ORDER BY map_id, link_index, target_map_id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayMapLinkDefinition(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.GetFloat(3),
                reader.GetFloat(4),
                reader.GetString(5),
                GameplayContentDatabaseValues.ParseConfidence(
                    reader.GetString(6)),
                GameplayContentDatabaseValues.ParseActivation(
                    reader.GetString(7)),
                reader.GetString(8)));
        }

        return values.ToArray();
    }

    private static void ValidateGameplayHeader(
        PublishedGameplayHeader header)
    {
        if (header.Revision.Length != 64 ||
            header.Revision.Any(static value =>
                value is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')) ||
            header.MapCount is < 1 or > 1_024 ||
            header.AddressPointCount is < 0 or > 100_000 ||
            header.LinkCount is < 0 or > 10_000 ||
            header.MonsterTemplateCount is < 1 or > 100_000 ||
            header.WorldBossCount is < 0 or > 1_024 ||
            header.PendingWorldBossCount is < 0 or > 1_024 ||
            header.ClassCount is < 1 or > 128 ||
            header.TalentEffectCount is < 1 or > 100_000 ||
            header.TalentCount is < 1 or > 100_000 ||
            header.SkillCount is < 1 or > 100_000 ||
            header.SkillBookCount is < 0 or > 100_000)
        {
            throw GameplayUnavailable(
                "The gameplay publication header is malformed or unbounded.");
        }
    }

    private sealed record PublishedGameplayHeader(
        string Revision,
        int MapCount,
        int AddressPointCount,
        int LinkCount,
        int MonsterTemplateCount,
        int WorldBossCount,
        int PendingWorldBossCount,
        int ClassCount,
        int TalentEffectCount,
        int TalentCount,
        int SkillCount,
        int SkillBookCount)
    {
        public int EntryCount => checked(
            MapCount + AddressPointCount + LinkCount +
            MonsterTemplateCount + WorldBossCount +
            PendingWorldBossCount + ClassCount + TalentEffectCount +
            TalentCount + SkillCount + SkillBookCount);
    }
}
