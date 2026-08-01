namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetContentRelease() => new(
        "20260801_042_pet_content_release",
        "Create immutable, versioned pet gameplay content and one publication pointer",
        PetContentSchemaSql +
        PetContentStepSchemaSql +
        PetContentGuardSql);
}
