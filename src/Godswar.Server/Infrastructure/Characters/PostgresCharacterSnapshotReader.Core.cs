using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterSnapshotReader
{
    private static async Task<IReadOnlyList<CharacterCoreRow>>
        ReadCoreCharactersAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int accountId,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            CoreCharacterQuery,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        var rows = new List<CharacterCoreRow>(2);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadCoreCharacter(reader));
            if (rows.Count > 2)
            {
                throw new CharacterSnapshotUnavailableException(
                    CharacterSnapshotFailureReason.BoundsExceeded,
                    "Character slot query exceeded its two-row guard.");
            }
        }

        return rows;
    }

    private static CharacterCoreRow ReadCoreCharacter(
        NpgsqlDataReader reader)
    {
        var identity = new CharacterIdentitySnapshot(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            ToUtcOffset(reader.GetDateTime(17)));
        return new CharacterCoreRow(
            identity,
            new CharacterAppearanceSnapshot(
                reader.GetString(3) == "female" ? (byte)0 : (byte)1,
                ToByte(reader.GetInt16(4), "camp"),
                ToByte(reader.GetInt16(5), "profession"),
                ToByte(reader.GetInt16(6), "hair"),
                ToByte(reader.GetInt16(7), "face"),
                ToByte(reader.GetInt16(8), "faith")),
            new CharacterLocationSnapshot(
                ToByte(reader.GetInt16(9), "map"),
                reader.GetFloat(15),
                reader.GetFloat(16),
                reader.GetInt64(45)),
            new CharacterProgressionSnapshot(
                reader.GetInt32(10),
                reader.GetInt32(27),
                reader.GetInt32(20),
                reader.GetInt32(21),
                reader.GetInt32(22)),
            new CharacterVitalsSnapshot(
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetInt64(28)),
            new CharacterWalletSnapshot(
                reader.GetInt32(41),
                reader.GetInt32(42)),
            new CharacterLoadoutSnapshot(
                reader.GetString(18),
                reader.GetString(19),
                reader.GetInt16(23),
                reader.GetInt32(24),
                reader.GetInt16(25),
                reader.GetInt32(26)),
            new CharacterZodiacSnapshot(
                ToByte(reader.GetInt16(29), "zodiac type"),
                reader.GetInt32(30),
                reader.IsDBNull(31)
                    ? null
                    : ToUtcOffset(reader.GetDateTime(31)),
                ToByte(reader.GetInt16(32), "zodiac level"),
                reader.GetInt32(33),
                reader.GetInt32(36),
                reader.IsDBNull(37)
                    ? null
                    : reader.GetFieldValue<DateOnly>(37),
                reader.GetInt64(38),
                reader.IsDBNull(39)
                    ? null
                    : ToUtcOffset(reader.GetDateTime(39)),
                reader.IsDBNull(40)
                    ? null
                    : reader.GetFieldValue<DateOnly>(40),
                reader.GetInt32(34),
                reader.GetInt32(35),
                ImmutableArray.CreateRange(
                    reader.GetFieldValue<int[]>(43)),
                ImmutableArray.CreateRange(
                    reader.GetFieldValue<int[]>(44))));
    }

    private static byte ToByte(short value, string field)
    {
        if (value is < byte.MinValue or > byte.MaxValue)
        {
            throw new InvalidDataException(
                $"Character {field} is outside the byte range.");
        }

        return (byte)value;
    }

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(value.ToUniversalTime());

    private sealed record CharacterCoreRow(
        CharacterIdentitySnapshot Identity,
        CharacterAppearanceSnapshot Appearance,
        CharacterLocationSnapshot Location,
        CharacterProgressionSnapshot Progression,
        CharacterVitalsSnapshot Vitals,
        CharacterWalletSnapshot Wallet,
        CharacterLoadoutSnapshot Loadout,
        CharacterZodiacSnapshot Zodiac)
    {
        public CharacterLoadSnapshot ToSnapshot(
            CharacterRelatedReadResult related) =>
            new(
                Identity,
                Appearance,
                Location,
                Progression,
                Vitals,
                Wallet,
                Loadout,
                Zodiac,
                related.CalculatedStats,
                related.Skills,
                related.Talents,
                related.Pets,
                related.PersonalBoosts);
    }

    private const string CoreCharacterQuery =
        """
        SELECT
            cb.id,
            cb.account_id,
            cb.name,
            cb.gender,
            cb.camp,
            cb.profession,
            cb.hair_style,
            COALESCE(cb.face_shap, 0),
            cb.belief,
            cb."Map",
            cb.fighter_job_lv,
            cb."MaxHP",
            cb."MaxMP",
            cb."curHP",
            cb."curMP",
            cb."Pos_X",
            cb."Pos_Z",
            cb."Register_time",
            COALESCE(ck.equip, ''),
            COALESCE(ck.kitbag_1, ''),
            cb."SkillPoint",
            cb."SkillExp",
            COALESCE(cb.holy_suit_points, 0),
            COALESCE((
                SELECT rank.weapon_rank
                FROM character_rank_summary rank
                WHERE rank.user_id = cb.id
            ), 0::smallint),
            COALESCE((
                SELECT rank.weapon_aura_effect
                FROM character_rank_summary rank
                WHERE rank.user_id = cb.id
            ), 0),
            COALESCE((
                SELECT rank.armor_rank
                FROM character_rank_summary rank
                WHERE rank.user_id = cb.id
            ), 0::smallint),
            COALESCE((
                SELECT rank.armor_aura_effect
                FROM character_rank_summary rank
                WHERE rank.user_id = cb.id
            ), 0),
            cb.fighter_job_exp,
            cb.vitals_revision,
            cb.zodiac_type,
            cb.zodiac_lucky_status,
            cb.zodiac_lucky_expires_at,
            cb.zodiac_level,
            cb.zodiac_energy,
            cb.zodiac_accumulated_exp_x100,
            cb.zodiac_accumulated_talent_exp_x100,
            cb.zodiac_energy_remainder_x100,
            cb.zodiac_online_day,
            cb.zodiac_online_duration_ticks,
            cb.zodiac_last_online_at,
            cb.zodiac_last_compensation_day,
            cb."Money",
            cb."Stone",
            ARRAY(
                SELECT COALESCE(grid.level, 0)::integer
                FROM generate_series(0, 15) AS requested_grid(grid_index)
                LEFT JOIN character_zodiac_skill_grids grid
                  ON grid.user_id = cb.id
                 AND grid.grid_index = requested_grid.grid_index
                ORDER BY requested_grid.grid_index
            ),
            ARRAY(
                SELECT COALESCE(grid.selected_skill_id, -1)
                FROM generate_series(0, 15) AS requested_grid(grid_index)
                LEFT JOIN character_zodiac_skill_grids grid
                  ON grid.user_id = cb.id
                 AND grid.grid_index = requested_grid.grid_index
                ORDER BY requested_grid.grid_index
            ),
            cb.position_revision
        FROM character_base cb
        LEFT JOIN character_item_loadout ck ON ck.user_id = cb.id
        WHERE cb.account_id = @accountId
        ORDER BY cb.id
        LIMIT 2;
        """;
}
