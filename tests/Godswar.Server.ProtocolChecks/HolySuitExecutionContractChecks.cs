using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySuitExecutionContractChecks
{
    public const string CheckName =
        "Holy Suit native result and receipt contract";

    private const string BoxBefore = "[9020,,,,,,1,1,1,1,0]";
    private const string BoxAfter = "[9020,,,,,,1,1,1,1,100000]";
    private const string GearBefore = "[1100,,,,,,1,1,1,1,0]";
    private const string GearAfter = "[1100,,,,,,1,1,1,1,100000]";
    private const string WareBefore = "[9010,,,,,,99,1,1,1,0]";
    private const string WareAfter = "[9010,,,,,,98,1,1,1,0]";
    private const string PrismBefore = "[9025,,,,,,12,1,1,1,0]";
    private const string PrismAfter = "[]";

    public static Task RunAsync()
    {
        CheckNativeResultMappings();
        CheckSuccessfulReceipts();
        CheckCommittedFailedWareRoll();
        CheckTerminalRejectionAndResultGuards();
        CheckRevisionAwareProjectionReplay();
        CheckProjectionPreservesLiveVitals();
        CheckStoreQuotaSnapshotContract();
        return Task.CompletedTask;
    }

    private static void CheckNativeResultMappings()
    {
        Check.Equal(
            400,
            HolySuitNativeResults.GetResultSubId(
                HolySuitCommandOperation.StoreExperience,
                HolySuitCommandResultStatus.ExperienceStored),
            "Store EXP success native result");
        Check.Equal(
            800,
            HolySuitNativeResults.GetResultSubId(
                HolySuitCommandOperation.TransferExperience,
                HolySuitCommandResultStatus.ExperienceTransferred),
            "Transfer EXP success native result");
        Check.Equal(
            1300,
            HolySuitNativeResults.GetResultSubId(
                HolySuitCommandOperation.ConsumeWare,
                HolySuitCommandResultStatus.WareConsumed),
            "Ware success native result");
        Check.Equal(
            1400,
            HolySuitNativeResults.GetResultSubId(
                HolySuitCommandOperation.ConsumeWare,
                HolySuitCommandResultStatus.WareUpgradeFailedRoll),
            "failed Ware roll native result");
        Check.Equal(
            2100,
            HolySuitNativeResults.GetResultSubId(
                HolySuitCommandOperation.TransformExperience,
                HolySuitCommandResultStatus.ExperienceTransformed),
            "Transform EXP success native result");
        Check.Equal(
            1600,
            HolySuitNativeResults.GetResultSubId(
                HolySuitCommandOperation.ConsumeWare,
                HolySuitCommandResultStatus.InsufficientPrisms),
            "automatic prism shortage uses the stock material message");
        Check.Equal(
            1700,
            HolySuitNativeResults.GetResultSubId(
                HolySuitCommandOperation.TransferExperience,
                HolySuitCommandResultStatus.SecondaryItemMissing),
            "missing transfer box uses the stock Holy Box message");
        foreach (var operation in Enum.GetValues<HolySuitCommandOperation>())
        {
            Check.Equal(
                2101,
                HolySuitNativeResults.GetResultSubId(
                    operation,
                    HolySuitCommandResultStatus.LevelRequirementNotMet),
                $"{operation} level-70 rejection uses the wired stock message");
        }
        Check.Throws<ArgumentOutOfRangeException>(
            () => HolySuitNativeResults.GetResultSubId(
                HolySuitCommandOperation.TransformExperience,
                HolySuitCommandResultStatus.WareTypeMismatch),
            "operation-inapplicable native status is rejected");
    }

    private static void CheckSuccessfulReceipts()
    {
        var store = CreateStoreReceipt();
        var storeResult = HolySuitExecutionResult.Committed(store);
        Check.True(
            storeResult.IsDurable && storeResult.IsSuccess,
            "stored EXP is a durable success");
        Check.True(
            HolySuitExecutionResult.Duplicate(store).IsSuccess,
            "replayed stored EXP remains successful without reapplying");
        var automaticStore = CreateStoreReceipt(requestedExperience: 0);
        Check.True(
            automaticStore.RequestedExperience == 0 &&
            automaticStore.CharacterExperienceBefore -
                automaticStore.CharacterExperienceAfter == 100_000,
            "Store Maximum receipt preserves auto intent and applied delta");

        var transfer = new HolySuitExecutionReceipt(
            characterId: 13,
            HolySuitCommandOperation.TransferExperience,
            HolySuitCommandEnvelope.AthensNpcId,
            HolySuitCommandEnvelope.DialogIndex,
            HolySuitCommandResultStatus.ExperienceTransferred,
            HolySuitNativeResults.ExperienceTransferredSubId,
            requestedExperience: 0,
            requestedPrisms: 0,
            characterExperienceBefore: 500_000_000,
            characterExperienceAfter: 500_000_000,
            dailyStoredExperienceBefore: 100_000,
            dailyStoredExperienceAfter: 100_000,
            battlePassDailyLimitExempt: false,
            prismsCreated: 0,
            prismsConsumed: 0,
            mutations:
            [
                Mutation(
                    HolySuitReceiptItemRole.Equipment,
                    slot: 1,
                    itemId: 1100,
                    instanceId: 101,
                    GearBefore,
                    GearAfter),
                Mutation(
                    HolySuitReceiptItemRole.HolyBox,
                    slot: 2,
                    itemId: 9020,
                    instanceId: 102,
                    BoxAfter,
                    "[]")
            ],
            progressionRevision: 8,
            inventoryRevision: 12,
            auditReference: "audit:holy-suit:transfer",
            outboxEventId: Guid.NewGuid());
        Check.True(
            HolySuitExecutionResult.Committed(transfer).IsSuccess,
            "gear and box transfer receipt succeeds");

        var transform = new HolySuitExecutionReceipt(
            characterId: 13,
            HolySuitCommandOperation.TransformExperience,
            HolySuitCommandEnvelope.SpartaNpcId,
            HolySuitCommandEnvelope.DialogIndex,
            HolySuitCommandResultStatus.ExperienceTransformed,
            HolySuitNativeResults.ExperienceTransformedSubId,
            requestedExperience: 0,
            requestedPrisms: 2,
            characterExperienceBefore: 300_000_000,
            characterExperienceAfter: 100_000_000,
            dailyStoredExperienceBefore: 100_000,
            dailyStoredExperienceAfter: 100_000,
            battlePassDailyLimitExempt: false,
            prismsCreated: 2,
            prismsConsumed: 0,
            mutations:
            [
                Mutation(
                    HolySuitReceiptItemRole.ExperiencePrism,
                    slot: 3,
                    itemId: 9025,
                    instanceId: 103,
                    "[]",
                    "[9025,,,,,,2,1,1,1,0]")
            ],
            progressionRevision: 9,
            inventoryRevision: 13,
            auditReference: "audit:holy-suit:transform",
            outboxEventId: Guid.NewGuid());
        Check.True(
            HolySuitExecutionResult.Committed(transform).IsSuccess &&
            transform.PrismsCreated == 2,
            "100m per prism is bound to the durable receipt");

        var wareWithPrisms = new HolySuitExecutionReceipt(
            characterId: 13,
            HolySuitCommandOperation.ConsumeWare,
            HolySuitCommandEnvelope.SpartaNpcId,
            HolySuitCommandEnvelope.DialogIndex,
            HolySuitCommandResultStatus.WareConsumed,
            HolySuitNativeResults.WareConsumedSubId,
            requestedExperience: 0,
            requestedPrisms: 0,
            characterExperienceBefore: 100_000_000,
            characterExperienceAfter: 100_000_000,
            dailyStoredExperienceBefore: 100_000,
            dailyStoredExperienceAfter: 100_000,
            battlePassDailyLimitExempt: false,
            prismsCreated: 0,
            prismsConsumed: 12,
            mutations:
            [
                Mutation(
                    HolySuitReceiptItemRole.Equipment,
                    4,
                    1100,
                    104,
                    GearBefore,
                    "[1100,,,,,,1,1,1,1,0,502]"),
                Mutation(
                    HolySuitReceiptItemRole.Ware,
                    5,
                    9014,
                    105,
                    WareBefore,
                    WareAfter),
                Mutation(
                    HolySuitReceiptItemRole.ExperiencePrism,
                    6,
                    9025,
                    106,
                    PrismBefore,
                    PrismAfter)
            ],
            progressionRevision: 9,
            inventoryRevision: 14,
            auditReference: "audit:holy-suit:ware-mithril",
            outboxEventId: Guid.NewGuid());
        Check.True(
            HolySuitExecutionResult.Committed(wareWithPrisms).IsSuccess &&
            wareWithPrisms.PrismsConsumed == 12,
            "Mithril+ ware receipts evidence automatic prisms");
    }

    private static void CheckCommittedFailedWareRoll()
    {
        var failedRoll = new HolySuitExecutionReceipt(
            characterId: 13,
            HolySuitCommandOperation.ConsumeWare,
            HolySuitCommandEnvelope.SpartaNpcId,
            HolySuitCommandEnvelope.DialogIndex,
            HolySuitCommandResultStatus.WareUpgradeFailedRoll,
            HolySuitNativeResults.WareUpgradeFailedSubId,
            requestedExperience: 0,
            requestedPrisms: 0,
            characterExperienceBefore: 100_000_000,
            characterExperienceAfter: 100_000_000,
            dailyStoredExperienceBefore: 100_000,
            dailyStoredExperienceAfter: 100_000,
            battlePassDailyLimitExempt: false,
            prismsCreated: 0,
            prismsConsumed: 0,
            mutations:
            [
                Mutation(
                    HolySuitReceiptItemRole.Equipment,
                    1,
                    1100,
                    107,
                    GearBefore,
                    GearBefore),
                Mutation(
                    HolySuitReceiptItemRole.Ware,
                    2,
                    9010,
                    108,
                    WareBefore,
                    WareAfter)
            ],
            progressionRevision: 9,
            inventoryRevision: 15,
            auditReference: "audit:holy-suit:ware-failed-roll",
            outboxEventId: Guid.NewGuid());
        var result = HolySuitExecutionResult.Committed(failedRoll);
        Check.True(
            result.IsDurable && !result.IsSuccess && failedRoll.Committed,
            "failed roll is durable so materials cannot be consumed twice");
    }

    private static void CheckTerminalRejectionAndResultGuards()
    {
        var rejected = new HolySuitExecutionReceipt(
            characterId: 13,
            HolySuitCommandOperation.StoreExperience,
            HolySuitCommandEnvelope.SpartaNpcId,
            HolySuitCommandEnvelope.DialogIndex,
            HolySuitCommandResultStatus.DailyStoreLimitExceeded,
            HolySuitNativeResults.DailyStoreLimitSubId,
            requestedExperience: 100_000,
            requestedPrisms: 0,
            characterExperienceBefore: 500_000,
            characterExperienceAfter: 500_000,
            dailyStoredExperienceBefore: 1_000_000,
            dailyStoredExperienceAfter: 1_000_000,
            battlePassDailyLimitExempt: false,
            prismsCreated: 0,
            prismsConsumed: 0,
            mutations: [],
            progressionRevision: 8,
            inventoryRevision: 15,
            auditReference: "audit:holy-suit:daily-limit",
            outboxEventId: null);
        var terminal = HolySuitExecutionResult.TerminalRejected(rejected);
        Check.True(
            terminal.IsDurable && !terminal.IsSuccess,
            "daily cap rejection is replayable but not successful");

        Check.Throws<ArgumentException>(
            () => HolySuitExecutionResult.Committed(rejected),
            "rejected receipt cannot claim committed disposition");
        Check.Throws<ArgumentException>(
            () => HolySuitExecutionResult.TerminalRejected(
                CreateStoreReceipt()),
            "successful receipt cannot claim terminal rejection");
        Check.Throws<ArgumentException>(
            () => new HolySuitExecutionReceipt(
                characterId: 13,
                HolySuitCommandOperation.TransformExperience,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                HolySuitCommandResultStatus.ExperienceTransformed,
                HolySuitNativeResults.ExperienceTransformedSubId,
                requestedExperience: 0,
                requestedPrisms: 2,
                characterExperienceBefore: 300_000_000,
                characterExperienceAfter: 200_000_000,
                dailyStoredExperienceBefore: 0,
                dailyStoredExperienceAfter: 0,
                battlePassDailyLimitExempt: false,
                prismsCreated: 2,
                prismsConsumed: 0,
                mutations:
                [
                    Mutation(
                        HolySuitReceiptItemRole.ExperiencePrism,
                        3,
                        9025,
                        109,
                        "[]",
                        "[9025,,,,,,2]")
                ],
                progressionRevision: 9,
                inventoryRevision: 16,
                auditReference: "audit:holy-suit:bad-exp-delta",
                outboxEventId: Guid.NewGuid()),
            "transform receipt enforces exactly 100m EXP per prism");
    }

    private static HolySuitExecutionReceipt CreateStoreReceipt(
        long requestedExperience = 100_000)
    {
        var canonicalBefore = CompactItemEntry.Parse(BoxBefore)
            .ToCompactString();
        var canonicalAfter = CompactItemEntry.Parse(BoxAfter)
            .ToCompactString();
        return new(
            characterId: 13,
            HolySuitCommandOperation.StoreExperience,
            HolySuitCommandEnvelope.SpartaNpcId,
            HolySuitCommandEnvelope.DialogIndex,
            HolySuitCommandResultStatus.ExperienceStored,
            HolySuitNativeResults.ExperienceStoredSubId,
            requestedExperience,
            requestedPrisms: 0,
            characterExperienceBefore: 500_000,
            characterExperienceAfter: 400_000,
            dailyStoredExperienceBefore: 0,
            dailyStoredExperienceAfter: 100_000,
            battlePassDailyLimitExempt: true,
            prismsCreated: 0,
            prismsConsumed: 0,
            mutations:
            [
                Mutation(
                    HolySuitReceiptItemRole.HolyBox,
                    slot: 0,
                    itemId: 9020,
                    instanceId: 100,
                    canonicalBefore,
                    canonicalAfter)
            ],
            progressionRevision: 8,
            inventoryRevision: 11,
            auditReference: "audit:holy-suit:store",
            outboxEventId: Guid.NewGuid());
    }

    private static void CheckRevisionAwareProjectionReplay()
    {
        var receipt = CreateStoreReceipt();
        var exact = new GameCharacter
        {
            Experience = checked((int)receipt.CharacterExperienceAfter),
            KitBag = KitBagSlots.SetSlot(
                "",
                0,
                receipt.Mutations[0].AfterCompactItemState)
        };
        GameClientHandler.ValidateHolySuitProjection(
            exact,
            receipt.ProgressionRevision,
            receipt.InventoryRevision,
            receipt);

        var afterOperationB = new GameCharacter
        {
            Experience = 123_456,
            KitBag = KitBagSlots.SetSlot("", 0, "[]")
        };
        GameClientHandler.ValidateHolySuitProjection(
            afterOperationB,
            receipt.ProgressionRevision + 1,
            receipt.InventoryRevision + 1,
            receipt);

        Check.Throws<InvalidDataException>(
            () => GameClientHandler.ValidateHolySuitProjection(
                exact,
                receipt.ProgressionRevision - 1,
                receipt.InventoryRevision,
                receipt),
            "replay rejects a progression snapshot older than operation A");
        Check.Throws<InvalidDataException>(
            () => GameClientHandler.ValidateHolySuitProjection(
                exact,
                receipt.ProgressionRevision,
                receipt.InventoryRevision - 1,
                receipt),
            "replay rejects an inventory snapshot older than operation A");
        Check.Throws<InvalidDataException>(
            () => GameClientHandler.ValidateHolySuitProjection(
                afterOperationB,
                receipt.ProgressionRevision,
                receipt.InventoryRevision + 1,
                receipt),
            "equal progression revision requires operation A's exact EXP");
        Check.Throws<InvalidDataException>(
            () => GameClientHandler.ValidateHolySuitProjection(
                afterOperationB,
                receipt.ProgressionRevision + 1,
                receipt.InventoryRevision,
                receipt),
            "equal inventory revision requires operation A's item state");
    }

    private static void CheckStoreQuotaSnapshotContract()
    {
        var quota = new HolySuitStoreQuotaSnapshot(
            characterId: 13,
            characterLevel: 80,
            new DateOnly(2026, 8, 1),
            storedExperienceToday: 50_000_000,
            dailyExperienceCredit: 80_000_000,
            battlePassDailyLimitExempt: false);
        Check.True(
            quota.UsageDay == new DateOnly(2026, 8, 1) &&
            quota.StoredExperienceToday == 50_000_000 &&
            quota.DailyExperienceCredit == 80_000_000 &&
            !quota.BattlePassDailyLimitExempt,
            "quota snapshot preserves bounded realm-day values");
        Check.Throws<ArgumentException>(
            () => new HolySuitStoreQuotaSnapshot(
                13,
                80,
                default,
                0,
                80_000_000,
                false),
            "quota snapshot rejects a missing realm usage day");
    }

    private static HolySuitReceiptMutation Mutation(
        HolySuitReceiptItemRole role,
        int slot,
        uint itemId,
        long instanceId,
        string before,
        string after) =>
        new(role, slot, itemId, instanceId, before, after);
}
