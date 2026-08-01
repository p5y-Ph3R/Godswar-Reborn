using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCharacterSnapshotReaderIntegrationChecks
{
    private static async Task
        AssertPinnedItemRevisionSurvivesPointerAdvanceAsync(
            string connectionString,
            PostgresGameStore store,
            NpgsqlDataSource dataSource,
            ICollection<SnapshotFixture> fixtures,
            string token)
    {
        var fixture = await CreateAccountFixtureAsync(
            store,
            $"snap_item_pin_{token}");
        var character = await CreateCharacterAsync(
            store,
            fixture.AccountId,
            $"SnapPin{token}");
        fixture = fixture with { CharacterIds = [character.Id] };
        fixtures.Add(fixture);

        await using var reader = new PostgresCharacterSnapshotReader(
            connectionString,
            store.ItemContent.Templates);
        var snapshotBefore = (await reader.ReadAsync(fixture.AccountId))
            .Character ?? throw new InvalidOperationException(
                "Item-pin fixture character is missing.");
        var storeCharacterBefore = await store.GetFirstCharacterAsync(
            fixture.AccountId) ?? throw new InvalidOperationException(
                "Item-pin store character is missing.");
        var storeStatsBefore = await store.GetCharacterStatsAsync(
            fixture.AccountId,
            character.Id) ?? throw new InvalidOperationException(
                "Item-pin store stats are missing.");
        var compatibilityBefore = await ReadCompatibilityFingerprintAsync(
            dataSource,
            character.Id);

        var originalRevision = store.ItemContent.Templates.Revision.Sha256;
        var alternateRevision = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                "item-pointer-advance:" + Guid.NewGuid().ToString("N"))));
        try
        {
            await PublishAlternateItemRevisionAsync(
                dataSource,
                character.Id,
                originalRevision,
                alternateRevision);
            var compatibilityAfter = await ReadCompatibilityFingerprintAsync(
                dataSource,
                character.Id);
            Check.True(
                compatibilityBefore != compatibilityAfter,
                "compatibility projections follow an explicit item pointer advance");

            var snapshotAfter = (await reader.ReadAsync(fixture.AccountId))
                .Character ?? throw new InvalidOperationException(
                    "Pinned reader lost its character after pointer advance.");
            var storeCharacterAfter = await store.GetFirstCharacterAsync(
                fixture.AccountId) ?? throw new InvalidOperationException(
                    "Pinned store lost its character after pointer advance.");
            var storeStatsAfter = await store.GetCharacterStatsAsync(
                fixture.AccountId,
                character.Id) ?? throw new InvalidOperationException(
                    "Pinned store lost stats after pointer advance.");

            Check.Equal(
                ToFingerprint(snapshotBefore.CalculatedStats),
                ToFingerprint(snapshotAfter.CalculatedStats),
                "existing snapshot reader keeps item-derived stats and ranks pinned");
            Check.Equal(
                snapshotBefore.Loadout,
                snapshotAfter.Loadout,
                "existing snapshot reader keeps loadout aura fields pinned");
            Check.Equal(
                ToFingerprint(storeStatsBefore),
                ToFingerprint(storeStatsAfter),
                "existing game store keeps item-derived stats and ranks pinned");
            Check.Equal(
                ToRankFingerprint(storeCharacterBefore),
                ToRankFingerprint(storeCharacterAfter),
                "existing game store keeps character rank and aura fields pinned");
        }
        finally
        {
            await using var restore = dataSource.CreateCommand("""
                UPDATE item_template_content_publication
                SET revision = @revision,
                    published_at = now()
                WHERE family = 'items';
                """);
            restore.Parameters.AddWithValue("revision", originalRevision);
            await restore.ExecuteNonQueryAsync();
        }
    }

    private static async Task PublishAlternateItemRevisionAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        string originalRevision,
        string alternateRevision)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_template_content_revisions (
                revision, entry_count, source, manifest_version,
                attribute_count, equipment_rank_count,
                holy_suit_effect_count)
            SELECT
                @alternateRevision,
                (SELECT count(DISTINCT equipment.prop_id)::integer
                 FROM character_items equipment
                 WHERE equipment.user_id = @characterId
                   AND equipment.item_location = 0),
                'pointer-advance-integration',
                2,
                source.attribute_count,
                source.equipment_rank_count,
                source.holy_suit_effect_count
            FROM item_template_content_revisions source
            WHERE source.revision = @originalRevision;

            INSERT INTO item_template_content_definitions (
                revision, id, kind, name_key, display_name,
                equipment_slot, class_ids, min_level, max_level,
                hand, skill_flag, texture, icon, stats)
            SELECT DISTINCT
                @alternateRevision,
                definition.id,
                'weapon',
                definition.name_key,
                definition.display_name,
                definition.equipment_slot,
                definition.class_ids,
                definition.min_level,
                definition.max_level,
                definition.hand,
                definition.skill_flag,
                definition.texture,
                definition.icon,
                definition.stats || jsonb_build_object(
                    'Attack', array_to_string(
                        array_fill('900000'::text, ARRAY[25]), ','),
                    'BaseFraction', array_to_string(
                        array_fill('900000'::text, ARRAY[25]), ','),
                    'AppFraction', array_to_string(
                        array_fill('900000'::text, ARRAY[25]), ','))
            FROM character_items equipment
            JOIN item_template_content_definitions definition
              ON definition.revision = @originalRevision
             AND definition.id = equipment.prop_id
            WHERE equipment.user_id = @characterId
              AND equipment.item_location = 0;

            INSERT INTO item_attribute_content_definitions
            SELECT @alternateRevision, definition.id, definition.name_key,
                   definition.stat_type, definition.distribution,
                   definition.percent, definition.max_level,
                   definition.level_values, definition.stats
            FROM item_attribute_content_definitions definition
            WHERE definition.revision = @originalRevision;

            INSERT INTO equipment_rank_content_definitions
            SELECT @alternateRevision, definition.rank_kind,
                   definition.rank_level, definition.required_score,
                   definition.aura_effect, definition.source
            FROM equipment_rank_content_definitions definition
            WHERE definition.revision = @originalRevision;

            INSERT INTO holy_suit_effect_content_definitions
            SELECT @alternateRevision, definition.effect_key,
                   definition.stat_type, definition.unlock_points,
                   definition.effect_value, definition.source
            FROM holy_suit_effect_content_definitions definition
            WHERE definition.revision = @originalRevision;

            UPDATE item_template_content_publication
            SET revision = @alternateRevision,
                published_at = now()
            WHERE family = 'items';
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "originalRevision",
            originalRevision);
        command.Parameters.AddWithValue(
            "alternateRevision",
            alternateRevision);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<ProjectionFingerprint>
        ReadCompatibilityFingerprintAsync(
            NpgsqlDataSource dataSource,
            int characterId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT physical_attack, physical_defense, magic_attack,
                   magic_defense, hit, max_hp, max_mp,
                   weapon_score, weapon_rank, weapon_aura_effect,
                   armor_score, armor_rank, armor_aura_effect
            FROM character_stat_summary
            WHERE user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "Compatibility stat projection is missing.");
        }
        return ReadFingerprint(reader);
    }

    private static ProjectionFingerprint ToFingerprint(
        CharacterCalculatedStatsSnapshot stats) =>
        new(
            stats.PhysicalAttack,
            stats.PhysicalDefense,
            stats.MagicAttack,
            stats.MagicDefense,
            stats.Hit,
            stats.MaxHp,
            stats.MaxMp,
            stats.WeaponScore,
            stats.WeaponRank,
            stats.WeaponAuraEffect,
            stats.ArmorScore,
            stats.ArmorRank,
            stats.ArmorAuraEffect);

    private static ProjectionFingerprint ToFingerprint(CharacterStats stats) =>
        new(
            stats.PhysicalAttack,
            stats.PhysicalDefense,
            stats.MagicAttack,
            stats.MagicDefense,
            stats.Hit,
            stats.MaxHp,
            stats.MaxMp,
            stats.WeaponScore,
            stats.WeaponRank,
            stats.WeaponAuraEffect,
            stats.ArmorScore,
            stats.ArmorRank,
            stats.ArmorAuraEffect);

    private static ProjectionFingerprint ReadFingerprint(
        NpgsqlDataReader reader) =>
        new(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetInt32(6), reader.GetInt32(7), reader.GetInt16(8),
            reader.GetInt32(9), reader.GetInt32(10), reader.GetInt16(11),
            reader.GetInt32(12));

    private static RankFingerprint ToRankFingerprint(GameCharacter character) =>
        new(
            character.WeaponRank,
            character.WeaponAuraEffect,
            character.ArmorRank,
            character.ArmorAuraEffect);

    private sealed record ProjectionFingerprint(
        int PhysicalAttack,
        int PhysicalDefense,
        int MagicAttack,
        int MagicDefense,
        int Hit,
        int MaxHp,
        int MaxMp,
        int WeaponScore,
        short WeaponRank,
        int WeaponAuraEffect,
        int ArmorScore,
        short ArmorRank,
        int ArmorAuraEffect);

    private sealed record RankFingerprint(
        short WeaponRank,
        int WeaponAuraEffect,
        short ArmorRank,
        int ArmorAuraEffect);
}
