namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetAptitudeCatalog() => new(
        "20260728_012_pet_aptitude_catalog",
        "Create the authoritative pet aptitude name catalog",
        """
        CREATE TABLE IF NOT EXISTS public.pet_aptitude_templates (
            aptitude smallint PRIMARY KEY
                CHECK (aptitude BETWEEN 1 AND 16),
            name_key varchar(32) NOT NULL UNIQUE
                CHECK (btrim(name_key) <> ''),
            display_name varchar(32) NOT NULL UNIQUE
                CHECK (btrim(display_name) <> ''),
            is_server_extension boolean NOT NULL DEFAULT false,
            source_path varchar(255) NOT NULL
                CHECK (btrim(source_path) <> '')
        );

        INSERT INTO public.pet_aptitude_templates (
            aptitude,
            name_key,
            display_name,
            is_server_extension,
            source_path
        )
        VALUES
            (1, 'PETAPTITUDE1', 'Weak', false, 'Localization/en_us/UI/Base/text.lua'),
            (2, 'PETAPTITUDE2', 'Fool', false, 'Localization/en_us/UI/Base/text.lua'),
            (3, 'PETAPTITUDE3', 'Cowish', false, 'Localization/en_us/UI/Base/text.lua'),
            (4, 'PETAPTITUDE4', 'Moderate', false, 'Localization/en_us/UI/Base/text.lua'),
            (5, 'PETAPTITUDE5', 'Rational', false, 'Localization/en_us/UI/Base/text.lua'),
            (6, 'PETAPTITUDE6', 'Calm', false, 'Localization/en_us/UI/Base/text.lua'),
            (7, 'PETAPTITUDE7', 'Smart', false, 'Localization/en_us/UI/Base/text.lua'),
            (8, 'PETAPTITUDE8', 'Zealous', false, 'Localization/en_us/UI/Base/text.lua'),
            (9, 'PETAPTITUDE9', 'Grumpy', false, 'Localization/en_us/UI/Base/text.lua'),
            (10, 'PETAPTITUDE10', 'Brave', false, 'Localization/en_us/UI/Base/text.lua'),
            (11, 'PETAPTITUDE11', 'Overbearing', false, 'Localization/en_us/UI/Base/text.lua'),
            (12, 'PETAPTITUDE12', 'Ferocious', false, 'Localization/en_us/UI/Base/text.lua'),
            (13, 'PETAPTITUDE13', 'Almighty', false, 'Localization/en_us/UI/Base/text.lua'),
            (14, 'PETAPTITUDE14', 'Godly', false, 'Localization/en_us/UI/Base/text.lua'),
            (15, 'PETAPTITUDE15', 'Celestial', true, 'Localization/en_us/UI/Base/text.lua'),
            (16, 'PETAPTITUDE16', 'Transcendent', true, 'Localization/en_us/UI/Base/text.lua')
        ON CONFLICT (aptitude) DO UPDATE
        SET name_key = EXCLUDED.name_key,
            display_name = EXCLUDED.display_name,
            is_server_extension = EXCLUDED.is_server_extension,
            source_path = EXCLUDED.source_path;

        UPDATE public.character_pets
        SET aptitude = 1
        WHERE aptitude IS NULL;

        ALTER TABLE public.character_pets
            ALTER COLUMN aptitude SET DEFAULT 1,
            ALTER COLUMN aptitude SET NOT NULL;

        ALTER TABLE public.character_pets
            DROP CONSTRAINT IF EXISTS
                fk_character_pets_aptitude_templates;

        ALTER TABLE public.character_pets
            ADD CONSTRAINT fk_character_pets_aptitude_templates
            FOREIGN KEY (aptitude)
            REFERENCES public.pet_aptitude_templates(aptitude)
            ON DELETE RESTRICT
            NOT VALID;

        ALTER TABLE public.character_pets
            VALIDATE CONSTRAINT
                fk_character_pets_aptitude_templates;
        """);
}
