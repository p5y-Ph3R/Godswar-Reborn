using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private static void AssertDurableStatePreserved(
        SchemaReleaseSnapshot before,
        SchemaReleaseSnapshot after)
    {
        if (before.InventoryFingerprint is not null)
        {
            Check.Equal(
                before.InventoryFingerprint,
                after.InventoryFingerprint
                ?? throw new InvalidOperationException(
                    "Authoritative inventory disappeared during migration."),
                "schema release preserves authoritative inventory byte-for-byte");
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
}
