using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetDurableCommandContractChecks
{
    public static Task RunAsync()
    {
        CheckBagIdentity();
        CheckPetIdentities();
        CheckReceiptRoundTrip();
        CheckPresenceProjectionReconciliation();
        CheckInventoryProjectionRoundTrip();
        CheckMigration();
        return Task.CompletedTask;
    }

    private static void CheckPresenceProjectionReconciliation()
    {
        var historicalCallOut = new PetDurableReceipt(
            CommandFamily.PetPresenceTransition,
            PetDurableReceiptStatus.PresenceChanged,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: -1,
            EquipmentSlot: -1,
            PetId: 71,
            PetLevel: 20,
            PetExperience: 100,
            PetRevision: 5,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 2,
            AggregateRevision: 4,
            AuditReference: "presence-projection-check",
            OutboxEventId: Guid.NewGuid());
        Check.Equal(
            (int)PetOperationResultCode.CallOutSucceeded,
            (int)GameClientHandler.ResolveAuthoritativePresenceResult(
                historicalCallOut,
                currentPetExists: true,
                currentIsCarried: true,
                currentIsSummoned: true),
            "matching current presence preserves the original result");
        Check.Equal(
            (int)PetOperationResultCode.RecallSucceeded,
            (int)GameClientHandler.ResolveAuthoritativePresenceResult(
                historicalCallOut,
                currentPetExists: true,
                currentIsCarried: true,
                currentIsSummoned: false),
            "old CallOut retry presents a later authoritative Recall");
    }

    private static void CheckInventoryProjectionRoundTrip()
    {
        var receipt = new PetBagActivationInventoryReceipt(
            2,
            7,
            2,
            Guid.NewGuid());
        var decoded =
            PetBagActivationInventoryPersistenceCodec.Decode(
                PetBagActivationInventoryPersistenceCodec.Encode(
                    receipt));
        Check.Equal(
            receipt,
            decoded,
            "pet bag inventory event canonical round trip");
        Check.Throws<InvalidDataException>(
            () => PetBagActivationInventoryPersistenceCodec.Encode(
                receipt with { LedgerEntryCount = 3 }),
            "pet bag inventory event bounds its ledger count");
    }

    private static void CheckBagIdentity()
    {
        var operation = Guid.NewGuid();
        var command = new BagItemActivationCommand(operation, 31);
        var envelope = BagItemActivationCommandEnvelope.Create(
            Subject(),
            Correlation(),
            DateTimeOffset.UtcNow,
            command);
        var retry = BagItemActivationCommandEnvelope.Create(
            Subject(),
            Correlation(),
            envelope.ReceivedAt.AddSeconds(1),
            command);
        var changedObservation =
            BagItemActivationCommandEnvelope.Create(
                Subject(),
                Correlation(),
                envelope.ReceivedAt.AddSeconds(2),
                command with
                {
                    ExecutionConstraint =
                        BagItemActivationExecutionConstraint
                            .RideRuntimeBlocked
                });
        Check.True(
            BagItemActivationCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid,
            "bag activation command validates");
        Check.Equal(
            envelope.OperationId,
            retry.OperationId,
            "bag activation UUID is stable across retries");
        Check.Equal(
            envelope.RequestHash,
            retry.RequestHash,
            "bag activation request does not depend on an item hint");
        Check.Equal(
            envelope.RequestHash,
            changedObservation.RequestHash,
            "transient Ride observation does not change retry identity");
        Check.True(
            BagItemActivationCommandEnvelope.Validate(
                changedObservation) ==
                CommandEnvelopeValidation.Valid,
            "server-derived Ride observation validates");
    }

    private static void CheckPetIdentities()
    {
        var operation = Guid.NewGuid();
        var level = PetLevelUpgradeCommandEnvelope.Create(
            Subject(),
            Correlation(),
            DateTimeOffset.UtcNow,
            new PetLevelUpgradeCommand(operation, 7));
        var call = PetPresenceTransitionCommandEnvelope.Create(
            Subject(),
            Correlation(),
            DateTimeOffset.UtcNow,
            new PetPresenceTransitionCommand(
                operation,
                7,
                PetPresenceCommandOperation.CallOut));
        Check.True(
            PetLevelUpgradeCommandEnvelope.Validate(level) ==
                CommandEnvelopeValidation.Valid,
            "pet level command validates");
        Check.True(
            PetPresenceTransitionCommandEnvelope.Validate(call) ==
                CommandEnvelopeValidation.Valid,
            "pet presence command validates");
        Check.True(
            level.OperationId != call.OperationId,
            "command families domain-separate one UUID");
        Check.Throws<ArgumentException>(
            () => PetLevelUpgradeCommandEnvelope.Create(
                Subject(),
                Correlation(),
                DateTimeOffset.UtcNow,
                new PetLevelUpgradeCommand(Guid.Empty, 7)),
            "empty pet operation UUID is rejected");
    }

    private static void CheckReceiptRoundTrip()
    {
        var receipt = new PetDurableReceipt(
            CommandFamily.PetPresenceTransition,
            PetDurableReceiptStatus.PresenceChanged,
            13,
            2,
            -1,
            -1,
            71,
            4,
            12_345,
            9,
            true,
            true,
            PresenceOperation: 2,
            AggregateRevision: 5,
            AuditReference: "42",
            OutboxEventId: Guid.NewGuid());
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            System.Text.Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));
        Check.Equal(receipt, decoded, "pet receipt canonical round trip");
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.DecodeAndVerify(
                System.Text.Encoding.UTF8.GetString(payload),
                new byte[32]),
            "pet receipt rejects a forged result hash");
    }

    private static void CheckMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => string.Equals(
                candidate.Id,
                "20260731_034_pet_durability_foundation",
                StringComparison.Ordinal));
        Check.True(
            migration.Sql.Contains(
                "CREATE TABLE public.pet_durable_stream_versions",
                StringComparison.Ordinal),
            "pet durability migration owns a bounded stream");
        Check.True(
            migration.Sql.Contains(
                "ON DELETE CASCADE",
                StringComparison.Ordinal),
            "pet stream projection cannot block controlled character purge");
        Check.True(
            migration.Sql.Contains(
                "character_pet_value",
                StringComparison.Ordinal),
            "pet evidence view scopes durable commands");
        Check.True(
            migration.Sql.Contains(
                "event.consumer_key = 'pet_durable_v1'",
                StringComparison.Ordinal),
            "pet evidence view excludes its coupled inventory event");
    }

    private static CommandSubject Subject() => new(13, 2);

    private static CommandConnectionCorrelation Correlation() =>
        new(Guid.NewGuid(), CommandTransportKind.SecureTlsLegacy);
}
