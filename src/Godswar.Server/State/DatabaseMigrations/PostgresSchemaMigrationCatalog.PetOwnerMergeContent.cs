namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetOwnerMergeContentRelease() => new(
        "20260811_073_pet_owner_merge_content",
        "Create immutable, versioned pet owner-Merge balance content",
        PetOwnerMergeContentSchemaSql +
        PetOwnerMergeContentGuardSql);
}
