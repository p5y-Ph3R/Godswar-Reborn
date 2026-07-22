using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class GearEnhancementTransactionChecks
{
    private const int GearSlot = 10;
    private const int StoneSlot = 11;
    private const int FlameSparkSlot = 12;
    private const int QuartzPlateSlot = 13;
    private const int WaterGrainSlot = 14;

    public static async Task RunAsync()
    {
        await CheckJsonAddEnhanceDeletePersistenceAsync();
        await CheckJsonConcurrentDuplicateSubmissionAsync();
    }

    private static async Task CheckJsonAddEnhanceDeletePersistenceAsync()
    {
        var dataPath = CreateDataPath("sequence");
        try
        {
            var gear = Item(1000) with
            {
                Quality = 20,
                Grade = 25,
                Exp = 321,
                HolySuitCode = 205,
                SocketCount = 1,
                Socket1EffectId = 7,
                Socket1Level = 3
            };
            var stone = Item(9930, stack: 4);
            var flameSpark = Item(9990, stack: 2);
            var quartzPlate = Item(9960, stack: 2);
            var waterGrain = Item(9991, stack: 2);
            var initialKitBag = Stage(
                gear,
                stone,
                flameSpark,
                quartzPlate,
                waterGrain);
            await SeedJsonStoreAsync(dataPath, initialKitBag);

            await using (var store = new JsonGameStore(dataPath))
            {
                var addRequest = Request(
                    initialKitBag,
                    GearEnhancementOperation.Add,
                    FlameSparkSlot);

                var wrongOwner = await store.EnhanceGearAsync(
                    accountId: 2,
                    characterId: 1,
                    addRequest);
                Check.True(
                    !wrongOwner.CharacterFound && wrongOwner.Enhancement is null,
                    "JSON gear enhancement rejects a character owned by another account");
                var afterOwnershipRejection = (await store.GetCharactersAsync(1)).Single();
                Check.Equal(
                    initialKitBag,
                    afterOwnershipRejection.KitBag,
                    "ownership rejection does not mutate the authoritative JSON bag");

                var added = await store.EnhanceGearAsync(1, 1, addRequest);
                AssertCommitted(added, GearEnhancementOperation.Add, "JSON Add");
                var addedCharacter = RequireCharacter(added, "JSON Add");
                var gearAfterAdd = gear with { Attribute1 = 0, AttributeLevel1 = 1 };
                Check.Equal(
                    gearAfterAdd,
                    KitBagSlots.GetItem(addedCharacter.KitBag, GearSlot),
                    "JSON Add persists the new attribute and preserves unrelated gear metadata");
                Check.Equal(
                    stone with { Stack = 3 },
                    KitBagSlots.GetItem(addedCharacter.KitBag, StoneSlot),
                    "JSON Add consumes exactly one Attribute Stone");
                Check.Equal(
                    flameSpark with { Stack = 1 },
                    KitBagSlots.GetItem(addedCharacter.KitBag, FlameSparkSlot),
                    "JSON Add consumes exactly one Flame Spark");
                Check.Equal(
                    quartzPlate,
                    KitBagSlots.GetItem(addedCharacter.KitBag, QuartzPlateSlot),
                    "JSON Add does not consume the Quartz Plate");
                Check.Equal(
                    waterGrain,
                    KitBagSlots.GetItem(addedCharacter.KitBag, WaterGrainSlot),
                    "JSON Add does not consume the Water Grain");

                var staleReplay = await store.EnhanceGearAsync(1, 1, addRequest);
                Check.True(!staleReplay.Committed, "stale JSON Add replay is not committed");
                Check.Equal(
                    (int)GearEnhancementStatus.StaleSelection,
                    (int)(staleReplay.Enhancement?.Status
                          ?? throw new InvalidOperationException("Stale Add replay omitted its result.")),
                    "stale JSON Add replay is rejected by snapshot revalidation");
                Check.Equal(
                    0,
                    staleReplay.Enhancement!.Mutations.Count,
                    "stale JSON Add replay emits no slot mutations");
                Check.Equal(
                    addedCharacter.KitBag,
                    RequireCharacter(staleReplay, "stale JSON Add replay").KitBag,
                    "stale JSON Add replay leaves every authoritative slot unchanged");

                var enhanceRequest = Request(
                    addedCharacter.KitBag,
                    GearEnhancementOperation.Enhance,
                    QuartzPlateSlot);
                var enhanced = await store.EnhanceGearAsync(1, 1, enhanceRequest);
                AssertCommitted(enhanced, GearEnhancementOperation.Enhance, "JSON Enhance");
                var enhancedCharacter = RequireCharacter(enhanced, "JSON Enhance");
                var gearAfterEnhance = gear with { Attribute1 = 1, AttributeLevel1 = 2 };
                Check.Equal(
                    gearAfterEnhance,
                    KitBagSlots.GetItem(enhancedCharacter.KitBag, GearSlot),
                    "JSON Enhance persists synchronized attribute template and level fields");
                Check.Equal(
                    stone with { Stack = 2 },
                    KitBagSlots.GetItem(enhancedCharacter.KitBag, StoneSlot),
                    "JSON Enhance consumes exactly one Attribute Stone");
                Check.Equal(
                    quartzPlate with { Stack = 1 },
                    KitBagSlots.GetItem(enhancedCharacter.KitBag, QuartzPlateSlot),
                    "JSON Enhance consumes exactly one Quartz Plate");
                Check.Equal(
                    flameSpark with { Stack = 1 },
                    KitBagSlots.GetItem(enhancedCharacter.KitBag, FlameSparkSlot),
                    "JSON Enhance preserves the remaining Flame Spark");

                var deleteRequest = Request(
                    enhancedCharacter.KitBag,
                    GearEnhancementOperation.Delete,
                    WaterGrainSlot);
                var deleted = await store.EnhanceGearAsync(1, 1, deleteRequest);
                AssertCommitted(deleted, GearEnhancementOperation.Delete, "JSON Delete");
                var deletedCharacter = RequireCharacter(deleted, "JSON Delete");
                Check.Equal(
                    gear,
                    KitBagSlots.GetItem(deletedCharacter.KitBag, GearSlot),
                    "JSON Delete removes the selected attribute and preserves unrelated gear metadata");
                Check.Equal(
                    stone with { Stack = 1 },
                    KitBagSlots.GetItem(deletedCharacter.KitBag, StoneSlot),
                    "JSON Delete consumes exactly one Attribute Stone");
                Check.Equal(
                    waterGrain with { Stack = 1 },
                    KitBagSlots.GetItem(deletedCharacter.KitBag, WaterGrainSlot),
                    "JSON Delete consumes exactly one Water Grain");
                Check.Equal(
                    flameSpark with { Stack = 1 },
                    KitBagSlots.GetItem(deletedCharacter.KitBag, FlameSparkSlot),
                    "JSON Delete preserves the remaining Flame Spark");
                Check.Equal(
                    quartzPlate with { Stack = 1 },
                    KitBagSlots.GetItem(deletedCharacter.KitBag, QuartzPlateSlot),
                    "JSON Delete preserves the remaining Quartz Plate");
            }

            await using var reopenedStore = new JsonGameStore(dataPath);
            var persisted = (await reopenedStore.GetCharactersAsync(1)).Single();
            Check.Equal(
                gear,
                KitBagSlots.GetItem(persisted.KitBag, GearSlot),
                "JSON Add/Enhance/Delete result survives a store reopen");
            Check.Equal(
                stone with { Stack = 1 },
                KitBagSlots.GetItem(persisted.KitBag, StoneSlot),
                "JSON Attribute Stone consumption survives a store reopen");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task CheckJsonConcurrentDuplicateSubmissionAsync()
    {
        var dataPath = CreateDataPath("race");
        try
        {
            var gear = Item(1000);
            var stone = Item(9930, stack: 2);
            var flameSpark = Item(9990, stack: 2);
            var initialKitBag = Stage(
                gear,
                stone,
                flameSpark,
                Item(9960),
                Item(9991));
            await SeedJsonStoreAsync(dataPath, initialKitBag);
            var request = Request(
                initialKitBag,
                GearEnhancementOperation.Add,
                FlameSparkSlot);

            await using (var storeA = new JsonGameStore(dataPath))
            await using (var storeB = new JsonGameStore(dataPath))
            {
                var results = await Task.WhenAll(
                    storeA.EnhanceGearAsync(1, 1, request),
                    storeB.EnhanceGearAsync(1, 1, request));
                Check.Equal(
                    1,
                    results.Count(static result => result.Committed),
                    "concurrent duplicate JSON gear enhancements commit exactly once");
                Check.Equal(
                    1,
                    results.Count(static result => !result.Committed),
                    "the duplicate JSON gear-enhancement submission is rejected");
                Check.Equal(
                    (int)GearEnhancementStatus.StaleSelection,
                    (int)(results.Single(static result => !result.Committed).Enhancement?.Status
                          ?? throw new InvalidOperationException("Concurrent rejection omitted its result.")),
                    "concurrent duplicate JSON submission fails authoritative snapshot revalidation");

                var persisted = (await storeA.GetCharactersAsync(1)).Single();
                Check.Equal(
                    gear with { Attribute1 = 0, AttributeLevel1 = 1 },
                    KitBagSlots.GetItem(persisted.KitBag, GearSlot),
                    "JSON race applies the gear mutation only once");
                Check.Equal(
                    stone with { Stack = 1 },
                    KitBagSlots.GetItem(persisted.KitBag, StoneSlot),
                    "JSON race consumes only one Attribute Stone");
                Check.Equal(
                    flameSpark with { Stack = 1 },
                    KitBagSlots.GetItem(persisted.KitBag, FlameSparkSlot),
                    "JSON race consumes only one Flame Spark");
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static void AssertCommitted(
        GearEnhancementTransactionResult result,
        GearEnhancementOperation operation,
        string scenario)
    {
        Check.True(result.CharacterFound, $"{scenario} returns the refreshed character");
        Check.True(result.Committed, $"{scenario} commits");
        Check.Equal(
            (int)GearEnhancementStatus.Succeeded,
            (int)(result.Enhancement?.Status
                  ?? throw new InvalidOperationException($"{scenario} omitted its result.")),
            $"{scenario} status");
        Check.Equal(
            (int)operation,
            (int)(result.Enhancement.Operation
                  ?? throw new InvalidOperationException($"{scenario} omitted its operation.")),
            $"{scenario} operation");
        Check.Equal(3, result.Enhancement.Mutations.Count, $"{scenario} persists three slot mutations");
    }

    private static GameCharacter RequireCharacter(
        GearEnhancementTransactionResult result,
        string scenario)
    {
        return result.Character
               ?? throw new InvalidOperationException($"{scenario} omitted the refreshed character.");
    }

    private static string CreateDataPath(string scenario)
    {
        var dataPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"godswar-gear-enhancement-{scenario}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(dataPath);
        return dataPath;
    }

    private static async Task SeedJsonStoreAsync(string dataPath, string kitBag)
    {
        var database = new GameDatabase
        {
            NextAccountId = 3,
            NextCharacterId = 2,
            Accounts =
            [
                new GameAccount
                {
                    Id = 1,
                    Username = "gear-enhancement-owner",
                    Password = string.Empty,
                    CreatedUtc = DateTime.UtcNow
                },
                new GameAccount
                {
                    Id = 2,
                    Username = "gear-enhancement-other",
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
                    Name = "EnhanceHero",
                    Profession = 0,
                    KitBag = kitBag,
                    Equipment = GameDefaults.DefaultEquipment(0),
                    CreatedUtc = DateTime.UtcNow
                }
            ]
        };
        await File.WriteAllTextAsync(
            Path.Combine(dataPath, "state.json"),
            JsonSerializer.Serialize(database, JsonDefaults.Indented));
    }

    private static string Stage(
        CompactItemEntry gear,
        CompactItemEntry stone,
        CompactItemEntry flameSpark,
        CompactItemEntry quartzPlate,
        CompactItemEntry waterGrain)
    {
        var kitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            GearSlot,
            gear.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, StoneSlot, stone.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, FlameSparkSlot, flameSpark.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, QuartzPlateSlot, quartzPlate.ToCompactString());
        return KitBagSlots.SetSlot(kitBag, WaterGrainSlot, waterGrain.ToCompactString());
    }

    private static GearEnhancementRequest Request(
        string kitBag,
        GearEnhancementOperation operation,
        int catalystSlot)
    {
        return new GearEnhancementRequest(
            operation,
            GearEnhancementSlotSelection.Capture(kitBag, GearSlot),
            GearEnhancementSlotSelection.Capture(kitBag, StoneSlot),
            GearEnhancementSlotSelection.Capture(kitBag, catalystSlot));
    }

    private static CompactItemEntry Item(uint itemId, short stack = 1)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Stack = stack
        };
    }
}
