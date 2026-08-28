namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateMedusaExternalHealth() => new(
        "20260828_120_medusa_external_health",
        "Use valid captured Medusa health as Normal with 2x/5x higher modes",
        """
        -- The external test run advertised Stheno and Medusa as 1/1 HP. Keep
        -- their prior authored baselines instead of publishing test sentinels.
        -- B_eliteA_centaur_004 was absent; its 2.5m baseline follows the other
        -- captured B_eliteA_centaur_002/006/008 variants.
        WITH captured(template_alias, normal_health) AS (
            VALUES
                ('boss-stheno',3000000),
                ('boss-euryale',5000000),
                ('boss-chrysaor',2000000),
                ('boss-medusa',3500000),
                ('elite-gorgon-archer',2500000),
                ('elite-crazy-axeman-a',2500000),
                ('elite-gorgon-shaman-006',2500000),
                ('elite-gorgon-shaman-008',2500000),
                ('elite-mud-crocodile',1500000),
                ('elite-gorgon-demon',1500000),
                ('elite-jungle-wizard-c5',1500000),
                ('elite-jungle-wizard-c6',1500000),
                ('elite-dark-gorgon-shaman',1500000),
                ('elite-dark-gorgon-priest',1500000),
                ('elite-gorgon-astrologer',1500000),
                ('elite-gorgon-guardian-a',1500000),
                ('elite-gorgon-axeman',1500000),
                ('elite-gorgon-hammer-soldier',1500000),
                ('elite-crazy-axeman-c',800000),
                ('elite-jungle-wizard-b',500000),
                ('elite-gorgon-guardian-b',800000),
                ('elite-gorgon-wizard',8000000),
                ('elite-cyclops-swordsman',8000000),
                ('elite-priest-a-012',250000),
                ('elite-priest-b-012',800000),
                ('elite-shaman-c-009',800000),
                ('elite-shaman-c-008',800000),
                ('elite-gorgon-priest-c-014',500000),
                ('elite-astrologer-b-009',1500000),
                ('elite-astrologer-a-006',1500000),
                ('normal-gorgon-pikeman-b',800000),
                ('normal-gorgon-pikeman-a',800000),
                ('normal-gorgon-shaman',800000),
                ('normal-mud-crocodile',800000),
                ('normal-jungle-deer',800000),
                ('normal-gorgon-jungle-wizard',800000),
                ('normal-giant-gorgon-axeman',800000),
                ('normal-gorgon-astrologer',800000),
                ('normal-gorgon-axeman-a',800000),
                ('normal-gorgon-axeman-b',800000)
        ), difficulties(difficulty, health_multiplier) AS (
            VALUES (1,1), (2,2), (3,5)
        )
        UPDATE public.medusa_monster_rules AS rule
        SET maximum_health =
                captured.normal_health * difficulties.health_multiplier,
            updated_at = now()
        FROM captured CROSS JOIN difficulties
        WHERE rule.template_alias = captured.template_alias
          AND rule.difficulty = difficulties.difficulty;
        """);
}
