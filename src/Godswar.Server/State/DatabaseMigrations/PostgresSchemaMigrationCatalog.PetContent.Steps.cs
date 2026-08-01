namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string PetContentStepSchemaSql =
        """
        CREATE TABLE public.pet_content_experience_steps (
            revision varchar(64) NOT NULL,
            current_level smallint NOT NULL,
            required_experience integer NOT NULL,
            CONSTRAINT pk_pet_content_experience
                PRIMARY KEY (revision, current_level),
            CONSTRAINT fk_pet_content_experience_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_experience_values CHECK (
                current_level BETWEEN 1 AND 254 AND
                required_experience > 0
            )
        );

        CREATE TABLE public.pet_content_rebirth_steps (
            revision varchar(64) NOT NULL,
            rebirth_number smallint NOT NULL,
            required_pet_level smallint NOT NULL,
            chance_item_id integer NOT NULL,
            chance_item_name varchar(128) NOT NULL,
            minimum_increase_per_stat numeric(18, 6) NOT NULL,
            maximum_increase_per_stat numeric(18, 6) NOT NULL,
            CONSTRAINT pk_pet_content_rebirth
                PRIMARY KEY (revision, rebirth_number),
            CONSTRAINT fk_pet_content_rebirth_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_rebirth_values CHECK (
                rebirth_number BETWEEN 1 AND 1000 AND
                required_pet_level BETWEEN 1 AND 255 AND
                chance_item_id > 0 AND
                btrim(chance_item_name) <> '' AND
                minimum_increase_per_stat >= 0 AND
                maximum_increase_per_stat >= minimum_increase_per_stat
            )
        );

        CREATE TABLE public.pet_content_publication (
            family varchar(32) PRIMARY KEY,
            revision varchar(64) NOT NULL,
            published_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_pet_content_publication_family
                CHECK (family = 'pets'),
            CONSTRAINT fk_pet_content_publication_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_content_revisions (revision)
                ON DELETE RESTRICT
        );
        """;
}
