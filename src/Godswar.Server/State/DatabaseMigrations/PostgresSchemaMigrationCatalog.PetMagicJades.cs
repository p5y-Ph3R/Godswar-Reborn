namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetMagicJadeAppearanceGroups() => new(
        "20260812_085_pet_magic_jade_appearance_groups",
        "Expose versioned Magic Jade appearances and Merge-cap groups",
        """
        DO $magic_jade_preflight$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.pet_content_species_definitions
                WHERE magic_jade_item_id <> 11049 + species_id
            ) THEN
                RAISE EXCEPTION
                    'pet content contains a non-canonical Magic Jade mapping';
            END IF;
        END
        $magic_jade_preflight$;

        ALTER TABLE public.pet_content_species_definitions
            ADD CONSTRAINT ck_pet_content_species_magic_jade_range
                CHECK (magic_jade_item_id = 11049 + species_id);

        CREATE UNIQUE INDEX ux_pet_content_species_magic_jade
            ON public.pet_content_species_definitions
                (revision, magic_jade_item_id);

        CREATE VIEW public.pet_content_magic_jade_appearance_groups AS
        WITH lookup_cap AS (
            SELECT revision, max(base_increase) AS base_increase
            FROM public.pet_content_merge_savvy_lookup
            GROUP BY revision
        ), five_spirits AS (
            SELECT revision, minimum_percent, maximum_percent
            FROM public.pet_content_merge_rank_spirit_steps
            WHERE spirit_count = 5
        )
        SELECT species.revision,
               species.magic_jade_item_id,
               species.species_id,
               species.display_name AS appearance_name,
               factors.factor AS merge_factor,
               trunc(lookup_cap.base_increase * factors.factor) /
                   100.0 AS merge_cap,
               floor((
                   trunc(lookup_cap.base_increase * factors.factor) *
                       five_spirits.minimum_percent + 50) / 100.0) /
                   100.0 AS five_spirit_minimum,
               floor((
                   trunc(lookup_cap.base_increase * factors.factor) *
                       five_spirits.maximum_percent + 50) / 100.0) /
                   100.0 AS five_spirit_maximum,
               'stock-client:EquipName.dat+ItemBaseAttribute.xml'
                   ::varchar(96) AS appearance_provenance,
               'stock-client:Pet_Alter.xml'
                   ::varchar(96) AS merge_policy_provenance
        FROM public.pet_content_species_definitions species
        INNER JOIN public.pet_content_merge_rank_species_factors factors
          ON factors.revision = species.revision
         AND factors.species_id = species.species_id
        INNER JOIN lookup_cap
          ON lookup_cap.revision = species.revision
        INNER JOIN five_spirits
          ON five_spirits.revision = species.revision;

        COMMENT ON VIEW public.pet_content_magic_jade_appearance_groups IS
            'Versioned stock-client Magic Jade appearance mapping joined to the deputy-species Merge cap policy. Magic Jade consumption is not implemented.';

        CREATE VIEW public.current_pet_magic_jade_appearance_groups AS
        SELECT groups.*
        FROM public.pet_content_magic_jade_appearance_groups groups
        INNER JOIN public.pet_content_publication publication
          ON publication.family = 'pets'
         AND publication.revision = groups.revision;

        COMMENT ON VIEW public.current_pet_magic_jade_appearance_groups IS
            'All 45 Magic Jade appearance and Merge-cap groups for the official pet publication.';
        """);
}
