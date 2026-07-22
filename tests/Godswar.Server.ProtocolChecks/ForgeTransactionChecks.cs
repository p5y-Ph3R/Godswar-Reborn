using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ForgeTransactionChecks
{
    public static async Task RunAsync()
    {
        await CheckJsonSuccessAndReplayRejectionAsync();
        CheckDeterministicFailedRoll();
        CheckMythicalBoundarySilverTransactions();
        CheckBackendRejections();
    }

    private static async Task CheckJsonSuccessAndReplayRejectionAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-forge-transaction-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            var equipment = CompactItemEntry.Parse(
                "[1000,11,22,33,44,55,1,1,1,1,7,305,2,3,4,5,6,2,7,1,8,2]");
            var primary = CompactItemEntry.Parse("[4212,,,,,,1,1,1,2,0,0]");
            var odds = CompactItemEntry.Parse("[4232,,,,,,1,1,1,10,0,0]");
            var kitBag = GameDefaults.EmptyKitBag;
            kitBag = KitBagSlots.SetSlot(kitBag, 0, equipment.ToCompactString());
            kitBag = KitBagSlots.SetSlot(kitBag, 18, primary.ToCompactString());
            kitBag = KitBagSlots.SetSlot(kitBag, 19, odds.ToCompactString());

            var database = new GameDatabase
            {
                NextAccountId = 2,
                NextCharacterId = 2,
                Accounts =
                [
                    new GameAccount
                    {
                        Id = 1,
                        Username = "forge-transaction-check",
                        Password = string.Empty,
                        CreatedUtc = DateTime.UtcNow
                    }
                ],
                Characters =
                [
                    new GameCharacter
                    {
                        Id = 1,
                        AccountId = 1,
                        Name = "ForgeHero",
                        Profession = 1,
                        Silver = 100,
                        KitBag = kitBag,
                        Equipment = GameDefaults.DefaultEquipment(1),
                        CreatedUtc = DateTime.UtcNow
                    }
                ]
            };
            await File.WriteAllTextAsync(
                Path.Combine(dataPath, "state.json"),
                JsonSerializer.Serialize(database, JsonDefaults.Indented));

            var request = new ForgeTransactionRequest(
                ForgeSlotSelection.Capture(kitBag, 0),
                ForgeSlotSelection.Capture(kitBag, 18),
                ForgeSlotSelection.Capture(kitBag, 19, 5));

            await using (var store = new JsonGameStore(dataPath))
            await using (var competingStore = new JsonGameStore(dataPath))
            {
                var wrongOwner = await store.ForgeEquipmentAsync(2, 1, request);
                Check.Equal(
                    (int)ForgeTransactionStatus.CharacterNotFound,
                    (int)wrongOwner.Status,
                    "forge cannot target a character owned by another account");

                var concurrentResults = await Task.WhenAll(
                    store.ForgeEquipmentAsync(1, 1, request),
                    competingStore.ForgeEquipmentAsync(1, 1, request));
                Check.Equal(
                    1,
                    concurrentResults.Count(result => result.Committed),
                    "two JSON-store instances can commit the staged forge only once");
                var result = concurrentResults.Single(candidate => candidate.Committed);
                var concurrentRejection = concurrentResults.Single(candidate => !candidate.Committed);
                Check.Equal(
                    (int)ForgeTransactionStatus.StaleSelection,
                    (int)concurrentRejection.Status,
                    "the concurrent duplicate is rejected after authoritative revalidation");
                Check.Equal(
                    (int)ForgeTransactionStatus.Succeeded,
                    (int)result.Status,
                    "100-percent forge commits as a success");
                Check.True(result.Committed, "successful forge is a committed attempt");
                Check.Equal(
                    (int)EquipmentForgeOperation.Sapphire,
                    result.MaterialType,
                    "forge result exposes the primary material operation");
                Check.Equal(100, result.Probability, "crystals clamp forge probability to 100 percent");
                Check.Equal(1, result.SilverSpent, "quality round-zero forge silver cost");

                var forgedCharacter = result.Character
                    ?? throw new InvalidOperationException("Successful forge omitted the refreshed character.");
                Check.Equal(99, forgedCharacter.Silver, "forge atomically deducts silver");
                Check.Equal(
                    equipment with { Quality = 2 },
                    KitBagSlots.GetItem(forgedCharacter.KitBag, 0),
                    "forge changes only equipment quality and preserves all other fields");
                Check.Equal(
                    primary with { Stack = 1 },
                    KitBagSlots.GetItem(forgedCharacter.KitBag, 18),
                    "forge consumes one primary material");
                Check.Equal(
                    odds with { Stack = 5 },
                    KitBagSlots.GetItem(forgedCharacter.KitBag, 19),
                    "forge consumes the selected odds-crystal quantity");

                var replay = await store.ForgeEquipmentAsync(1, 1, request);
                Check.Equal(
                    (int)ForgeTransactionStatus.StaleSelection,
                    (int)replay.Status,
                    "replaying staged snapshots is rejected as stale");
                Check.True(!replay.Committed, "stale replay is not a committed forge attempt");
                var replayCharacter = replay.Character
                    ?? throw new InvalidOperationException("Stale forge rejection omitted the refreshed character.");
                Check.Equal(99, replayCharacter.Silver, "stale replay does not deduct silver twice");
                Check.Equal(
                    primary with { Stack = 1 },
                    KitBagSlots.GetItem(replayCharacter.KitBag, 18),
                    "stale replay does not consume the primary material twice");
                Check.Equal(
                    odds with { Stack = 5 },
                    KitBagSlots.GetItem(replayCharacter.KitBag, 19),
                    "stale replay does not consume odds crystals twice");
            }

            await using var reopenedStore = new JsonGameStore(dataPath);
            var persistedCharacter = (await reopenedStore.GetCharactersAsync(1)).Single();
            Check.Equal(99, persistedCharacter.Silver, "committed forge silver survives a JSON-store reload");
            Check.Equal(
                equipment with { Quality = 2 },
                KitBagSlots.GetItem(persistedCharacter.KitBag, 0),
                "committed forged equipment survives a JSON-store reload");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static void CheckDeterministicFailedRoll()
    {
        var equipment = CompactItemEntry.Parse(
            "[1000,11,22,33,44,55,7,1,1,1,7,305,2,3,4,5,6,2,7,1,8,2]");
        var primary = CompactItemEntry.Parse("[4210,,,,,,1,1,1,2,0,0]");
        var kitBag = GameDefaults.EmptyKitBag;
        kitBag = KitBagSlots.SetSlot(kitBag, 0, equipment.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 18, primary.ToCompactString());
        var request = new ForgeTransactionRequest(
            ForgeSlotSelection.Capture(kitBag, 0),
            ForgeSlotSelection.Capture(kitBag, 18),
            null);

        Check.True(
            ForgePersistencePlanner.TryCreate(
                kitBag,
                silver: 100,
                request,
                roll: 99,
                out var plan,
                out _,
                out var rejectionReason),
            $"valid deterministic failed forge produces a persistence plan: {rejectionReason}");
        Check.True(!plan!.Succeeded, "zero-percent forge roll is a legitimate failure");
        Check.Equal(0, plan.Calculation.SuccessProbability, "negative native odds clamp to zero percent");
        Check.Equal(97, plan.UpdatedSilver, "failed roll still deducts the round silver cost");
        Check.Equal(
            equipment,
            KitBagSlots.GetItem(plan.UpdatedKitBag, 0),
            "failed roll preserves every equipment field");
        Check.Equal(
            primary with { Stack = 1 },
            KitBagSlots.GetItem(plan.UpdatedKitBag, 18),
            "failed roll still consumes the primary material");
    }

    private static void CheckMythicalBoundarySilverTransactions()
    {
        var equipment = Item(1001, quality: 12);
        var primary = Item(4213, stack: 2);
        var kitBag = GameDefaults.EmptyKitBag;
        kitBag = KitBagSlots.SetSlot(kitBag, 0, equipment.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 18, primary.ToCompactString());
        var request = new ForgeTransactionRequest(
            ForgeSlotSelection.Capture(kitBag, 0),
            ForgeSlotSelection.Capture(kitBag, 18),
            null);

        Check.True(
            ForgePersistencePlanner.TryCreate(
                kitBag,
                silver: 600,
                request,
                roll: 0,
                out var failedPlan,
                out _,
                out var failedReason),
            $"Q12 level-20 forge creates a failed-roll plan at exact silver ({failedReason})");
        Check.True(!failedPlan!.Succeeded, "zero-percent Q12 attempt remains a committed failed roll");
        Check.Equal(600, failedPlan.Calculation.SilverCost, "Q12 attempt uses the authoritative level-20 cost");
        Check.Equal(0, failedPlan.UpdatedSilver, "failed Q12 attempt deducts the exact authoritative cost");
        Check.Equal(
            equipment,
            KitBagSlots.GetItem(failedPlan.UpdatedKitBag, 0),
            "failed Q12 attempt preserves the equipment at Q12");
        Check.Equal(
            primary with { Stack = 1 },
            KitBagSlots.GetItem(failedPlan.UpdatedKitBag, 18),
            "failed Q12 attempt consumes its authoritative primary material");

        AssertRejected(
            kitBag,
            silver: 599,
            request,
            ForgeTransactionStatus.InsufficientSilver,
            "Q12 attempt one silver below the authoritative level-20 cost");

        var odds = Item(4233, stack: 25);
        var starterEquipment = Item(1000, quality: 12);
        var successKitBag = GameDefaults.EmptyKitBag;
        successKitBag = KitBagSlots.SetSlot(successKitBag, 0, starterEquipment.ToCompactString());
        successKitBag = KitBagSlots.SetSlot(successKitBag, 18, primary.ToCompactString());
        successKitBag = KitBagSlots.SetSlot(successKitBag, 19, odds.ToCompactString());
        var successRequest = new ForgeTransactionRequest(
            ForgeSlotSelection.Capture(successKitBag, 0),
            ForgeSlotSelection.Capture(successKitBag, 18),
            ForgeSlotSelection.Capture(successKitBag, 19, 25));

        Check.True(
            ForgePersistencePlanner.TryCreate(
                successKitBag,
                silver: 30,
                successRequest,
                roll: 0,
                out var successPlan,
                out _,
                out var successReason),
            $"Q12 starter forge creates a success plan at exact silver ({successReason})");
        Check.True(successPlan!.Succeeded, "Q12 starter forge can reach Mythical with authoritative odds");
        Check.Equal(30, successPlan.Calculation.SilverCost, "Q12 starter attempt uses its exact economy cost");
        Check.Equal(0, successPlan.UpdatedSilver, "successful Q12 attempt deducts the exact authoritative cost");
        Check.Equal(
            starterEquipment with { Quality = 13 },
            KitBagSlots.GetItem(successPlan.UpdatedKitBag, 0),
            "successful Q12 attempt reaches Q13 without skipping to the global ceiling");

        var boundlessEquipment = starterEquipment with
        {
            Quality = EquipmentForgeCalculator.MaximumQuality
        };
        var cappedKitBag = KitBagSlots.SetSlot(
            successKitBag,
            0,
            boundlessEquipment.ToCompactString());
        AssertRejected(
            cappedKitBag,
            silver: 1_000_000,
            new ForgeTransactionRequest(
                ForgeSlotSelection.Capture(cappedKitBag, 0),
                ForgeSlotSelection.Capture(cappedKitBag, 18),
                null),
            ForgeTransactionStatus.InvalidForge,
            "attempt above the authoritative Boundless Q20 quality ceiling");
    }

    private static void CheckBackendRejections()
    {
        var equipment = CompactItemEntry.Parse("[1000,,,,,,1,1,1,1,0,0]");
        var primary = CompactItemEntry.Parse("[4212,,,,,,1,1,1,1,0,0]");
        var odds = CompactItemEntry.Parse("[4232,,,,,,1,1,1,1,0,0]");
        var kitBag = GameDefaults.EmptyKitBag;
        kitBag = KitBagSlots.SetSlot(kitBag, 0, equipment.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 18, primary.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 19, odds.ToCompactString());

        AssertRejected(
            kitBag,
            silver: 100,
            new ForgeTransactionRequest(
                new ForgeSlotSelection(0, equipment with { Quality = 2 }, 1),
                new ForgeSlotSelection(18, primary, 1),
                null),
            ForgeTransactionStatus.StaleSelection,
            "client-spoofed equipment snapshot");

        AssertRejected(
            kitBag,
            silver: 0,
            new ForgeTransactionRequest(
                new ForgeSlotSelection(0, equipment, 1),
                new ForgeSlotSelection(18, primary, 1),
                null),
            ForgeTransactionStatus.InsufficientSilver,
            "insufficient authoritative silver");

        AssertRejected(
            kitBag,
            silver: 100,
            new ForgeTransactionRequest(
                new ForgeSlotSelection(0, equipment, 1),
                new ForgeSlotSelection(0, equipment, 1),
                null),
            ForgeTransactionStatus.InvalidSelection,
            "duplicate equipment/material slot");

        AssertRejected(
            kitBag,
            silver: 100,
            new ForgeTransactionRequest(
                new ForgeSlotSelection(0, equipment, 1),
                new ForgeSlotSelection(18, primary, 0),
                null),
            ForgeTransactionStatus.InvalidSelection,
            "zero primary-material quantity");

        AssertRejected(
            kitBag,
            silver: 100,
            new ForgeTransactionRequest(
                new ForgeSlotSelection(0, equipment, 1),
                new ForgeSlotSelection(18, primary, 1),
                new ForgeSlotSelection(19, odds, 2)),
            ForgeTransactionStatus.InsufficientMaterials,
            "odds quantity larger than authoritative stack");

        var stackedEquipment = equipment with { Stack = 2 };
        var stackedKitBag = KitBagSlots.SetSlot(
            kitBag,
            0,
            stackedEquipment.ToCompactString());
        AssertRejected(
            stackedKitBag,
            silver: 100,
            new ForgeTransactionRequest(
                new ForgeSlotSelection(0, stackedEquipment, 1),
                new ForgeSlotSelection(18, primary, 1),
                null),
            ForgeTransactionStatus.InvalidForge,
            "malformed stacked equipment");

        Check.True(
            ForgePersistencePlanner.TryCreate(
                kitBag,
                silver: 100,
                new ForgeTransactionRequest(
                    new ForgeSlotSelection(0, equipment, 1),
                    new ForgeSlotSelection(18, primary, 1),
                    null),
                roll: 0,
                out var exactStackPlan,
                out _,
                out var exactStackReason),
            $"exact-stack forge creates a valid backend plan ({exactStackReason})");
        Check.True(
            KitBagSlots.GetItem(exactStackPlan!.UpdatedKitBag, 18).IsEmpty,
            "consuming an exact primary stack clears its authoritative slot");

        var multiStackKitBag = KitBagSlots.SetSlot(
            kitBag,
            19,
            (odds with { Stack = 6 }).ToCompactString());
        multiStackKitBag = KitBagSlots.SetSlot(
            multiStackKitBag,
            20,
            (odds with { Stack = 7 }).ToCompactString());
        Check.True(
            ForgePersistencePlanner.TryCreate(
                multiStackKitBag,
                silver: 100,
                new ForgeTransactionRequest(
                    new ForgeSlotSelection(0, equipment, 1),
                    new ForgeSlotSelection(18, primary, 1),
                    new ForgeSlotSelection(19, odds with { Stack = 6 }, 6),
                    [new ForgeSlotSelection(20, odds with { Stack = 7 }, 7)]),
                roll: 0,
                out var multiStackPlan,
                out _,
                out var multiStackReason),
            $"same-ID odds crystals can span authoritative bag stacks ({multiStackReason})");
        Check.True(
            KitBagSlots.GetItem(multiStackPlan!.UpdatedKitBag, 19).IsEmpty &&
            KitBagSlots.GetItem(multiStackPlan.UpdatedKitBag, 20).IsEmpty,
            "multi-stack forge consumes each reserved crystal source exactly");
    }

    private static void AssertRejected(
        string kitBag,
        int silver,
        ForgeTransactionRequest request,
        ForgeTransactionStatus expectedStatus,
        string scenario)
    {
        Check.True(
            !ForgePersistencePlanner.TryCreate(
                kitBag,
                silver,
                request,
                roll: 0,
                out var plan,
                out var status,
                out _),
            $"{scenario} is rejected by the backend planner");
        Check.True(plan is null, $"{scenario} produces no mutation plan");
        Check.Equal((int)expectedStatus, (int)status, $"{scenario} rejection status");
    }

    private static CompactItemEntry Item(
        uint itemId,
        short quality = 1,
        short grade = 1,
        short stack = 1)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = quality,
            Grade = grade,
            Stack = stack
        };
    }
}
