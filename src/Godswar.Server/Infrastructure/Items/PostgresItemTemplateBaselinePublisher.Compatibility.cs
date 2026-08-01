using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task ApplyCompatibilityOverridesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE item_templates
            SET stats = jsonb_set(
                jsonb_set(
                    jsonb_set(
                        stats,
                        '{Hit}',
                        to_jsonb(CASE id
                            WHEN 2834 THEN '197,213,229,245,262,278,294,310,326,342,358,374,390,406,422,438,454,470,486,502'
                            WHEN 2844 THEN '207,225,242,259,277,294,311,328,345,362'
                            WHEN 2854 THEN '200,216,232,248,265,281,297,313,329,345,361,377,393,409,425,441,457,473,489,505'
                            WHEN 2864 THEN '202,218,234,251,268,284,300,317,333,350,367,384,401,418,435,452,469,486,503,520'
                        END::text)
                    ),
                    '{MainAttribute}',
                    to_jsonb('0,1,20,21,40,60,80,90,110,180,240,250,250,250,250,250,250,250,250,250,250,250,250,250,250'::text)
                ),
                '{BaseFraction}',
                to_jsonb('0,8,18,28,40,54,74,100,140,200,230,260,295,330,370,410,455,500,550,600'::text)
            )
            WHERE id IN (2834, 2854, 2864);

            UPDATE item_templates
            SET stats = jsonb_set(
                stats,
                '{AppFraction}',
                to_jsonb('15,19,24,30,36,42,48,60,75,90,120,150,174,199,227,255,285,317,350,384,420,458,498,548,600'::text))
            WHERE id IN (2144, 2244);

            UPDATE item_templates
            SET stats = jsonb_set(
                jsonb_set(
                    stats,
                    '{DefendFraction}',
                    to_jsonb('330,475,750,950,1350,1720,2225,3860,5250,8000,12000,17000,22000,25300,-1'::text)
                ),
                '{DefendEff}',
                to_jsonb('1,2,3,4,5,6,7,8,9,10,11,12,13,14,14'::text)
            )
            WHERE kind IN ('armor', 'cloth')
              AND min_level >= 135;

            UPDATE item_templates
            SET stats = stats || jsonb_build_object(
                'Attack', @zero20,
                'AttackRadius', @zero20,
                'AttackSpeed', @zero20,
                'MaxHP', @zero20,
                'MaxMP', @zero20,
                'Defence', @zero20,
                'MagicAk', @zero20,
                'MagicRec', @zero20,
                'Miss', @zero20,
                'State', @zero20,
                'StateImmunity', @zero20,
                'AcceptCure', @zero20,
                'Cure', @zero20,
                'PhysicalDamage', @zero20,
                'MagicDamage', @zero20,
                'MagicDamageAbsorb', @zero20,
                'PhysicalDamageAbsorb', @zero20,
                'Speed', @zero20,
                'FuryAddAk', @zero20,
                'FuryAddRec', @zero20,
                'InjureImbibe', @zero20
            )
            WHERE id = 2844;

            UPDATE item_templates
            SET stats = stats - 'DefendFraction' - 'DefendEff'
            WHERE kind IN ('head', 'glove')
              AND min_level >= 135;
            """, connection, transaction);
        command.Parameters.AddWithValue(
            "zero20",
            "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
