using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static class ClassSuitExecutionContractChecks
{
    public const string CheckName =
        "Class Suit native result and replay contract";

    public static Task RunAsync()
    {
        CheckSuccessResults();
        CheckConversionFailureResults();
        CheckAttributeFailureResults();
        CheckExecutionDispositions();
        CheckPersistenceMutationBound();
        ClassSuitLegacyNativeResultCompatibilityChecks.Run();
        CheckEquippedMutationPersistence();
        CheckRejectedEquippedSelectionRefresh();
        return Task.CompletedTask;
    }

    private static void CheckEquippedMutationPersistence()
    {
        Check.True(
            ClassSuitReplayIntent.TryCreate(
                ClassSuitCommandOperation.ExchangeTierI,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                ClassSuitItemLocation.Equipment,
                ClassSuitCommandEnvelope.EquippedWeaponSlot,
                3,
                ClassSuitReplayIntent.NoKitBagSlot,
                out var replayIntent),
            "equipped Class Suit replay intent is valid");
        var receipt = new ClassSuitExecutionReceipt(
            CommandFamily.ClassSuitExchangeTierI,
            CharacterId: 13,
            ClassSuitCommandOperation.ExchangeTierI,
            ClassSuitCommandEnvelope.SpartaNpcId,
            ClassSuitCommandEnvelope.DialogIndex,
            replayIntent,
            ClassSuitCommandResultStatus.Succeeded,
            NativeResultSubId: 120,
            Mutations:
            [
                new ClassSuitReceiptMutation(
                    ClassSuitCommandEnvelope.EquippedWeaponSlot,
                    BeforeItemId: 1013,
                    AfterItemId: 1032,
                    BeforeCompactItemState: "weapon-before",
                    AfterCompactItemState: "weapon-after",
                    ClassSuitItemLocation.Equipment),
                new ClassSuitReceiptMutation(
                    KitBagSlot: 3,
                    BeforeItemId: 7001,
                    AfterItemId: 7001,
                    BeforeCompactItemState: "insignia-before",
                    AfterCompactItemState: "insignia-after")
            ],
            InventoryRevision: 44,
            AuditReference: "44",
            OutboxEventId: Guid.NewGuid());

        var decoded = ClassSuitPersistenceCodec.Decode(
            ClassSuitPersistenceCodec.Encode(receipt));
        Check.True(
            decoded.Family == receipt.Family &&
            decoded.CharacterId == receipt.CharacterId &&
            decoded.ReplayIntent == receipt.ReplayIntent &&
            decoded.Mutations.SequenceEqual(receipt.Mutations) &&
            decoded.ReplayIntent.GearLocation ==
                ClassSuitItemLocation.Equipment &&
            decoded.Mutations[0].Location ==
                ClassSuitItemLocation.Equipment,
            "durable receipt preserves equipped location evidence");
        Check.Throws<InvalidDataException>(
            () => ClassSuitPersistenceCodec.Encode(
                receipt with
                {
                    Mutations = [receipt.Mutations[1]]
                }),
            "successful receipt requires its selected equipment mutation");
        Check.Throws<InvalidDataException>(
            () => ClassSuitPersistenceCodec.Encode(
                receipt with
                {
                    Mutations =
                    [
                        receipt.Mutations[0] with { KitBagSlot = 9 },
                        receipt.Mutations[1]
                    ]
                }),
            "receipt rejects unknown equipped slots");
    }

    private static void CheckRejectedEquippedSelectionRefresh()
    {
        Check.True(
            ClassSuitReplayIntent.TryCreate(
                ClassSuitCommandOperation.ExchangeTierI,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                ClassSuitItemLocation.Equipment,
                ClassSuitCommandEnvelope.EquippedWeaponSlot,
                3,
                ClassSuitReplayIntent.NoKitBagSlot,
                out var replayIntent),
            "rejected equipped fixture intent is valid");
        var receipt = new ClassSuitExecutionReceipt(
            CommandFamily.ClassSuitExchangeTierI,
            CharacterId: 13,
            ClassSuitCommandOperation.ExchangeTierI,
            ClassSuitCommandEnvelope.SpartaNpcId,
            ClassSuitCommandEnvelope.DialogIndex,
            replayIntent,
            ClassSuitCommandResultStatus.PlayerLevelTooLow,
            NativeResultSubId: 147,
            Mutations: [],
            InventoryRevision: 44,
            AuditReference: "45",
            OutboxEventId: null);

        Check.True(
            GameClientHandler.ResolveClassSuitEquipmentRefreshSlots(receipt)
                .SequenceEqual(
                    [ClassSuitCommandEnvelope.EquippedWeaponSlot]),
            "terminal rejection refreshes its authoritative equipped weapon");
    }

    private static void CheckSuccessResults()
    {
        var expected = new[]
        {
            (ClassSuitCommandOperation.ExchangeTierI, 120),
            (ClassSuitCommandOperation.AddAttribute, 121),
            (ClassSuitCommandOperation.DeleteAttribute, 122),
            (ClassSuitCommandOperation.ConvertToCommon, 152),
            (ClassSuitCommandOperation.UpgradeTierII, 300),
            (ClassSuitCommandOperation.UpgradeTierIII, 157),
            (ClassSuitCommandOperation.UpgradeTierIV, 169)
        };
        foreach (var (operation, resultSubId) in expected)
        {
            Check.Equal(
                resultSubId,
                ClassSuitNativeResults.Resolve(
                    operation,
                    ClassSuitCommandResultStatus.Succeeded),
                $"{operation} native success result");
        }
        Check.Equal(
            149,
            ClassSuitNativeResults.GenericWrongSelection,
            "stock generic wrong-selection result");
        Check.Equal(
            159,
            ClassSuitNativeResults.UnsupportedFifthAttribute,
            "unresolved fifth-attribute operation is explicit and non-mutating");
    }

    private static void CheckConversionFailureResults()
    {
        CheckResult(
            ClassSuitCommandOperation.ExchangeTierI,
            ClassSuitCommandResultStatus.SelectionMissing,
            146,
            "Tier-I target requirement");
        CheckResult(
            ClassSuitCommandOperation.ExchangeTierI,
            ClassSuitCommandResultStatus.PlayerLevelTooLow,
            147,
            "Tier-I level requirement");
        CheckResult(
            ClassSuitCommandOperation.ExchangeTierI,
            ClassSuitCommandResultStatus.InvalidMaterial,
            148,
            "Tier-I insignia missing");
        CheckResult(
            ClassSuitCommandOperation.ExchangeTierI,
            ClassSuitCommandResultStatus.InsufficientMaterial,
            148,
            "Tier-I insignia quantity missing");
        CheckResult(
            ClassSuitCommandOperation.ExchangeTierI,
            ClassSuitCommandResultStatus.InvalidEquipment,
            149,
            "Tier-I item unsuitable");

        CheckResult(
            ClassSuitCommandOperation.ConvertToCommon,
            ClassSuitCommandResultStatus.InvalidEquipment,
            150,
            "reverse source ineligible");
        CheckResult(
            ClassSuitCommandOperation.ConvertToCommon,
            ClassSuitCommandResultStatus.InsufficientCapacity,
            151,
            "reverse requires free bag slots");

        CheckResult(
            ClassSuitCommandOperation.UpgradeTierII,
            ClassSuitCommandResultStatus.InvalidMaterial,
            301,
            "Tier-II insignia missing");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierII,
            ClassSuitCommandResultStatus.InsufficientMaterial,
            301,
            "Tier-II insignia quantity missing");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierII,
            ClassSuitCommandResultStatus.InsufficientCapacity,
            302,
            "Tier-II bag capacity");

        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIII,
            ClassSuitCommandResultStatus.SelectionMissing,
            153,
            "Tier-III Suit II missing");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIII,
            ClassSuitCommandResultStatus.UnsupportedSource,
            153,
            "Tier-III source is not Suit II");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIII,
            ClassSuitCommandResultStatus.InvalidMaterial,
            154,
            "Tier-III insignia missing");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIII,
            ClassSuitCommandResultStatus.InsufficientMaterial,
            155,
            "Tier-III insignia quantity missing");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIII,
            ClassSuitCommandResultStatus.PlayerLevelTooLow,
            156,
            "Tier-III level unsuitable");

        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIV,
            ClassSuitCommandResultStatus.SelectionMissing,
            165,
            "Tier-IV Suit III missing");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIV,
            ClassSuitCommandResultStatus.UnsupportedSource,
            165,
            "Tier-IV source is not Suit III");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIV,
            ClassSuitCommandResultStatus.InvalidMaterial,
            166,
            "Tier-IV insignia missing");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIV,
            ClassSuitCommandResultStatus.InsufficientMaterial,
            167,
            "Tier-IV insignia quantity missing");
        CheckResult(
            ClassSuitCommandOperation.UpgradeTierIV,
            ClassSuitCommandResultStatus.PlayerLevelTooLow,
            168,
            "Tier-IV level unsuitable");
    }

    private static void CheckAttributeFailureResults()
    {
        CheckResult(
            ClassSuitCommandOperation.AddAttribute,
            ClassSuitCommandResultStatus.SelectionMissing,
            149,
            "Add Attribute selection missing");
        CheckResult(
            ClassSuitCommandOperation.AddAttribute,
            ClassSuitCommandResultStatus.InvalidMaterial,
            144,
            "Add Attribute material unsuitable");
        CheckResult(
            ClassSuitCommandOperation.AddAttribute,
            ClassSuitCommandResultStatus.InsufficientMaterial,
            144,
            "Add Attribute material stack empty");
        CheckResult(
            ClassSuitCommandOperation.AddAttribute,
            ClassSuitCommandResultStatus.AttributeAlreadyPresent,
            143,
            "class attribute already present");
        CheckResult(
            ClassSuitCommandOperation.AddAttribute,
            ClassSuitCommandResultStatus.ProfessionMismatch,
            142,
            "class stone profession mismatch");
        CheckResult(
            ClassSuitCommandOperation.AddAttribute,
            ClassSuitCommandResultStatus.AttributeSlotsFull,
            149,
            "Add Attribute generic rule failure");

        CheckResult(
            ClassSuitCommandOperation.DeleteAttribute,
            ClassSuitCommandResultStatus.SelectionMissing,
            139,
            "Delete Attribute selection missing");
        CheckResult(
            ClassSuitCommandOperation.DeleteAttribute,
            ClassSuitCommandResultStatus.InvalidMaterial,
            141,
            "Delete Attribute water missing");
        CheckResult(
            ClassSuitCommandOperation.DeleteAttribute,
            ClassSuitCommandResultStatus.InsufficientMaterial,
            141,
            "Delete Attribute material stack empty");
        CheckResult(
            ClassSuitCommandOperation.DeleteAttribute,
            ClassSuitCommandResultStatus.AttributeMissing,
            140,
            "class attribute not present");
        CheckResult(
            ClassSuitCommandOperation.DeleteAttribute,
            ClassSuitCommandResultStatus.InvalidEquipment,
            139,
            "Delete Attribute generic rule failure");
    }

    private static void CheckExecutionDispositions()
    {
        var receipt = new ClassSuitExecutionReceipt(
            CommandFamily.ClassSuitExchangeTierI,
            CharacterId: 13,
            ClassSuitCommandOperation.ExchangeTierI,
            ClassSuitCommandEnvelope.SpartaNpcId,
            ClassSuitCommandEnvelope.DialogIndex,
            CreateReplayIntent(
                ClassSuitCommandOperation.ExchangeTierI,
                gearSlot: 0,
                primarySlot: 1),
            ClassSuitCommandResultStatus.Succeeded,
            NativeResultSubId: 120,
            Mutations:
            [
                new ClassSuitReceiptMutation(
                    KitBagSlot: 0,
                    BeforeItemId: 1013,
                    AfterItemId: 1032,
                    BeforeCompactItemState: "gear-before",
                    AfterCompactItemState: "gear-after")
            ],
            InventoryRevision: 42,
            AuditReference: "audit-ref",
            OutboxEventId: Guid.NewGuid());

        foreach (var result in new[]
                 {
                     ClassSuitExecutionResult.Committed(receipt),
                     ClassSuitExecutionResult.Duplicate(receipt),
                     ClassSuitExecutionResult.TerminalRejected(receipt)
                 })
        {
            Check.True(
                result.IsDurable && ReferenceEquals(result.Receipt, receipt),
                $"{result.Disposition} retains its durable receipt");
        }
        foreach (var result in new[]
                 {
                     ClassSuitExecutionResult.ReplayNotFound(),
                     ClassSuitExecutionResult.RequestHashConflict(),
                     ClassSuitExecutionResult.InvalidIntent(),
                     ClassSuitExecutionResult.PreconditionFailed()
                 })
        {
            Check.True(
                !result.IsDurable && result.Receipt is null,
                $"{result.Disposition} cannot fabricate a durable receipt");
        }
    }

    private static void CheckPersistenceMutationBound()
    {
        var mutations = Enumerable.Range(
                0,
                ClassSuitPersistenceCodec.MaximumMutationCount)
            .Select(index => new ClassSuitReceiptMutation(
                index,
                checked((uint)(1000 + index)),
                checked((uint)(1100 + index)),
                $"before-{index}",
                $"after-{index}"))
            .ToArray();
        var receipt = new ClassSuitExecutionReceipt(
            CommandFamily.ClassSuitConvertToCommon,
            CharacterId: 13,
            ClassSuitCommandOperation.ConvertToCommon,
            ClassSuitCommandEnvelope.SpartaNpcId,
            ClassSuitCommandEnvelope.DialogIndex,
            CreateReplayIntent(
                ClassSuitCommandOperation.ConvertToCommon,
                gearSlot: 0),
            ClassSuitCommandResultStatus.Succeeded,
            NativeResultSubId: 152,
            mutations,
            InventoryRevision: 43,
            AuditReference: "43",
            OutboxEventId: Guid.NewGuid());

        var decoded = ClassSuitPersistenceCodec.Decode(
            ClassSuitPersistenceCodec.Encode(receipt));
        Check.Equal(
            ClassSuitPersistenceCodec.MaximumMutationCount,
            decoded.Mutations.Count,
            "Tier-IV reverse split refunds fit the durable receipt");
        Check.True(
            decoded.ReplayIntent == receipt.ReplayIntent,
            "durable codec preserves the exact stable replay intent");

        var historicalFiveMutationReceipt = receipt with
        {
            Mutations = mutations.Take(5).ToArray()
        };
        var historicalDecoded = ClassSuitPersistenceCodec.Decode(
            ClassSuitPersistenceCodec.Encode(
                historicalFiveMutationReceipt));
        Check.True(
            historicalDecoded.Mutations.SequenceEqual(
                historicalFiveMutationReceipt.Mutations) &&
            historicalDecoded.ReplayIntent ==
                historicalFiveMutationReceipt.ReplayIntent,
            "the expanded bound preserves historical five-mutation receipts");

        Check.Throws<InvalidDataException>(
            () => ClassSuitPersistenceCodec.Encode(
                receipt with
                {
                    Mutations =
                    [
                        .. mutations,
                        new ClassSuitReceiptMutation(
                            95,
                            2000,
                            2001,
                            "before-overflow",
                            "after-overflow")
                    ]
                }),
            "Class Suit receipt mutation evidence stays bounded");

        Check.Throws<InvalidDataException>(
            () => ClassSuitPersistenceCodec.Encode(
                receipt with
                {
                    ReplayIntent = receipt.ReplayIntent with
                    {
                        NpcId = ClassSuitCommandEnvelope.AthensNpcId
                    }
                }),
            "receipt endpoint fields and replay intent cannot disagree");
    }

    private static void CheckResult(
        ClassSuitCommandOperation operation,
        ClassSuitCommandResultStatus status,
        int expectedSubId,
        string description)
    {
        Check.Equal(
            expectedSubId,
            ClassSuitNativeResults.Resolve(operation, status),
            description);
    }

    private static ClassSuitReplayIntent CreateReplayIntent(
        ClassSuitCommandOperation operation,
        int gearSlot,
        int primarySlot = ClassSuitReplayIntent.NoKitBagSlot,
        int secondarySlot = ClassSuitReplayIntent.NoKitBagSlot)
    {
        Check.True(
            ClassSuitReplayIntent.TryCreate(
                operation,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                gearSlot,
                primarySlot,
                secondarySlot,
                out var intent),
            "Class Suit replay fixture intent is valid");
        return intent;
    }
}
