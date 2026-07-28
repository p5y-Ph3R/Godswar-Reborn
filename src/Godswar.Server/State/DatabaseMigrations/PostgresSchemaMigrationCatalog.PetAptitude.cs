namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetAptitudeRangeCorrection() => new(
        "20260728_011_pet_aptitude_range",
        "Allow every pet aptitude value handled by the installed client",
        """
        ALTER TABLE public.character_pets
            DROP CONSTRAINT IF EXISTS character_pets_aptitude_check;

        ALTER TABLE public.character_pets
            ADD CONSTRAINT character_pets_aptitude_check
            CHECK (aptitude BETWEEN 1 AND 16) NOT VALID;

        ALTER TABLE public.character_pets
            VALIDATE CONSTRAINT character_pets_aptitude_check;
        """);
}
