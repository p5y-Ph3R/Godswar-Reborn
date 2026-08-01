namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string GameplayContentPolicySql =
        """
        ALTER TABLE public.map_links
            ADD COLUMN IF NOT EXISTS confidence varchar(48) NOT NULL
                DEFAULT 'captured-span-map',
            ADD COLUMN IF NOT EXISTS activation varchar(48) NOT NULL
                DEFAULT 'automatic',
            ADD COLUMN IF NOT EXISTS note varchar(512) NOT NULL
                DEFAULT 'Captured SpanMap boundary with a matching reciprocal.';

        ALTER TABLE public.map_links
            ADD CONSTRAINT ck_map_links_confidence CHECK (
                confidence IN (
                    'captured-span-map',
                    'reciprocal-address-point',
                    'excluded-by-observed-topology'
                )
            ),
            ADD CONSTRAINT ck_map_links_activation CHECK (
                activation IN (
                    'automatic',
                    'disabled-by-world-topology'
                )
            ),
            ADD CONSTRAINT ck_map_links_note CHECK (btrim(note) <> '');

        ALTER TABLE public.world_boss_areas
            ADD CONSTRAINT ck_world_boss_areas_bonus
                CHECK (bonus_basis_points BETWEEN 0 AND 100000),
            ADD CONSTRAINT ck_world_boss_areas_respawn
                CHECK (respawn_interval_seconds BETWEEN 1 AND 2592000);

        ALTER TABLE public.skill_templates
            ADD COLUMN IF NOT EXISTS intonate_time numeric NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS cooling_time numeric NOT NULL DEFAULT 0;

        ALTER TABLE public.skill_templates
            ADD CONSTRAINT ck_skill_templates_intonate_time
                CHECK (intonate_time BETWEEN 0 AND 3600),
            ADD CONSTRAINT ck_skill_templates_cooling_time
                CHECK (cooling_time BETWEEN 0 AND 2592000);

        CREATE TABLE public.pending_world_boss_areas (
            map_id smallint PRIMARY KEY
                REFERENCES public.map_templates (map_id) ON DELETE RESTRICT,
            scene_key varchar(96) NOT NULL,
            reason varchar(512) NOT NULL,
            CONSTRAINT ck_pending_world_boss_text CHECK (
                btrim(scene_key) <> '' AND btrim(reason) <> ''
            )
        );
        """;
}
