using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private const string ClassSuitAttributeSlotsMigrationId =
        "20260803_053_class_suit_attribute_slots";

    private static void AssertReleaseState(SchemaReleaseSnapshot snapshot)
    {
        Check.Equal(
            PostgresSchemaMigrationCatalog.All.Count,
            snapshot.AppliedMigrations.Count,
            "release has the exact registered migration count");
        for (var index = 0;
             index < PostgresSchemaMigrationCatalog.All.Count;
             index++)
        {
            var expected = PostgresSchemaMigrationCatalog.All[index];
            var actual = snapshot.AppliedMigrations[index];
            Check.Equal(expected.Id, actual.Id, $"release migration {index} ID");
            Check.Equal(
                expected.Checksum,
                actual.Checksum,
                $"release migration {expected.Id} checksum");
        }

        Check.Equal(3, snapshot.PacketRelationCount, "all packet metadata tables exist");
        Check.True(
            snapshot.HasOpcodeNameFunction,
            "packet opcode-name trigger function exists");
        Check.Equal(
            1,
            snapshot.OpcodeNameTriggerCount,
            "packet transaction opcode-name trigger exists once");
        Check.Equal(
            1,
            snapshot.PacketCaptureForeignKeyCount,
            "packet transactions retain the capture-session cascade foreign key");
        Check.Equal(
            3,
            snapshot.CheckpointColumnCount,
            "all additive character checkpoint columns exist");
        Check.Equal(
            4,
            snapshot.CheckpointConstraintCount,
            "all character checkpoint constraints exist and validate");
        Check.Equal(
            6,
            snapshot.LifecycleColumnCount,
            "all additive character lifecycle columns exist");
        Check.Equal(
            5,
            snapshot.LifecycleConstraintCount,
            "all character lifecycle constraints exist and validate");
        Check.Equal(
            3,
            snapshot.LifecycleIndexCount,
            "all character lifecycle indexes exist and validate");
        Check.Equal(
            1,
            snapshot.AccountLifecycleColumnCount,
            "account aggregate lifecycle version exists");
        Check.Equal(
            1,
            snapshot.AccountLifecycleConstraintCount,
            "account aggregate lifecycle version is constrained");
        Check.Equal(
            2,
            snapshot.ClassSuitAttributeColumnCount,
            "both dedicated Class Suit attribute columns exist");
        Check.Equal(
            0,
            snapshot.UnvalidatedConstraintCount,
            "all constraints validate");
        Check.Equal(
            0,
            snapshot.InvalidIndexCount,
            "all indexes are valid and ready");
    }

    private static async Task AssertDurableStatePreservedAsync(
        Npgsql.NpgsqlDataSource dataSource,
        SchemaReleaseSnapshot before,
        SchemaReleaseSnapshot after)
    {
        if (before.InventoryFingerprint is not null)
        {
            var crossedClassSuitAttributeSlots =
                !HasMigration(
                    before,
                    ClassSuitAttributeSlotsMigrationId) &&
                HasMigration(
                    after,
                    ClassSuitAttributeSlotsMigrationId);
            if (crossedClassSuitAttributeSlots)
            {
                Check.Equal(
                    0,
                    before.ClassSuitAttributeColumnCount,
                    "migration 053 starts without dedicated Class Suit columns");
                Check.Equal(
                    2,
                    after.ClassSuitAttributeColumnCount,
                    "migration 053 adds both dedicated Class Suit columns");
                await AssertClassSuitMigrationPreservedAsync(
                    dataSource,
                    before.InventoryRows
                    ?? throw new InvalidOperationException(
                        "Legacy authoritative inventory rows disappeared."));
            }
            else
            {
                Check.Equal(
                    before.InventoryFingerprint,
                    after.InventoryFingerprint
                    ?? throw new InvalidOperationException(
                        "Authoritative inventory disappeared during migration."),
                    "schema release preserves authoritative inventory byte-for-byte");
            }
        }

        if (before.AccountCharacterFingerprint is not null)
        {
            Check.Equal(
                before.AccountCharacterFingerprint,
                after.AccountCharacterFingerprint
                ?? throw new InvalidOperationException(
                    "Account or character state disappeared during migration."),
                "schema release preserves account and character identity rows");
        }

        if (before.PacketPayloadFingerprint is not null)
        {
            Check.Equal(
                before.PacketPayloadFingerprint,
                after.PacketPayloadFingerprint
                ?? throw new InvalidOperationException(
                    "Captured packet payloads disappeared during migration."),
                "schema release preserves captured packet bytes");
        }

        if (before.AppliedMigrations.Count ==
                PostgresSchemaMigrationCatalog.All.Count &&
            before.PetFingerprint is not null)
        {
            Check.Equal(
                before.PetFingerprint,
                after.PetFingerprint
                ?? throw new InvalidOperationException(
                    "Authoritative pet state disappeared during startup."),
                "current release startup preserves authoritative pet rows");
        }

        if (before.AppliedMigrations.Count ==
                PostgresSchemaMigrationCatalog.All.Count &&
            before.EconomyFingerprint is not null)
        {
            Check.Equal(
                before.EconomyFingerprint,
                after.EconomyFingerprint
                ?? throw new InvalidOperationException(
                    "Economy baseline or ledger evidence disappeared during startup."),
                "current release startup preserves economy evidence rows");
        }

        if (before.CheckpointFingerprint is not null)
        {
            Check.Equal(
                before.CheckpointFingerprint,
                after.CheckpointFingerprint
                ?? throw new InvalidOperationException(
                    "Character checkpoint state disappeared during startup."),
                "schema release preserves owner fences and checkpoint revisions");
        }

        if (before.LifecycleFingerprint is not null)
        {
            Check.Equal(
                before.LifecycleFingerprint,
                after.LifecycleFingerprint
                ?? throw new InvalidOperationException(
                    "Character lifecycle state disappeared during startup."),
                "current release startup preserves character lifecycle state");
        }
    }

    private static bool HasMigration(
        SchemaReleaseSnapshot snapshot,
        string migrationId) =>
        snapshot.AppliedMigrations.Any(migration =>
            string.Equals(
                migration.Id,
                migrationId,
                StringComparison.Ordinal));
}
