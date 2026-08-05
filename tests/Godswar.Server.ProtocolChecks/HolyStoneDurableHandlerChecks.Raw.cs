using System.Buffers.Binary;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Domain.Inventory;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckRawExactWireSemanticsAsync()
    {
        await CheckRawInitialMenuNavigationAsync();
        await CheckRawBoundaryRejectsDowngradesAsync();
        await CheckRawMountAsync(
            HolyStoneProtocol.EncodeKitBagReference(WeaponSlot),
            HolyStoneTargetMode.KitBag,
            WeaponSlot,
            stoneSlot: 7,
            "page-zero bag Mount");
        await CheckRawMountAsync(
            HolyStoneProtocol.EncodeKitBagReference(40),
            HolyStoneTargetMode.KitBag,
            targetSlot: 40,
            stoneSlot: 7,
            "page-one bag Mount");
        await CheckRawRemoveAsync();
        await CheckRawDrillAsync();
        await CheckRawNavigationAsync();
        CheckExactDrillCostPolicy();
        await CheckJsonRawDrillGoldDebitAsync();
        await CheckRawAdvancedDrillFallbackAsync();
        await CheckRawStoreMaterialAndSocketRulesAsync();
        await CheckRawSettlementOrderingAsync();
        await CheckRawUpgradeBridgeAsync();
        await CheckRawCombinationBridgeAsync();
    }

    private static async Task CheckRawMountAsync(
        int targetReference,
        HolyStoneTargetMode expectedMode,
        int targetSlot,
        int stoneSlot,
        string description)
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            targetSlot,
            WeaponBefore.ToCompactString());
        bag = KitBagSlots.SetSlot(
            bag,
            stoneSlot,
            StoneBefore.ToCompactString());
        await using var fixture = await CreateRawFixtureAsync(
            initialKitBag: bag,
            durableExecutionResult:
                HolyStoneExecutionResult.InvalidIntent(),
            durableOperation: HolyStoneCommandOperation.Mount,
            requiresDurablePlayerCommands: true,
            hasLocalLegacyAuthenticationAccess: true);
        var packet = HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.MountSubId,
            args =>
            {
                args[HolyStoneProtocol.MountScratchArgumentIndex] = 0;
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    targetReference;
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(stoneSlot);
            });

        await InvokeAsync(fixture.Handler, packet);

        var command = fixture.Executor?.ExecutedCommand ??
            throw new InvalidOperationException(
                $"{description} did not reach the durable executor.");
        Check.Equal(
            (int)HolyStoneCommandOperation.Mount,
            (int)command.Operation,
            $"{description} operation");
        Check.Equal(
            (int)(expectedMode == HolyStoneTargetMode.KitBag
                ? HolyStoneTargetLocation.KitBag
                : HolyStoneTargetLocation.Equipment),
            (int)command.TargetLocation,
            $"{description} target mode");
        Check.Equal(targetSlot, command.TargetSlot, $"{description} target");
        Check.Equal(stoneSlot, command.StoneKitBagSlot, $"{description} stone");
        Check.Equal(-1, command.SocketIndex, $"{description} socket");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            $"{description} never reaches retry-ambiguous legacy storage");
    }

    private static async Task CheckRawRemoveAsync()
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            23,
            WeaponBefore.ToCompactString());
        await using var fixture = await CreateRawFixtureAsync(
            initialKitBag: bag,
            durableExecutionResult:
                HolyStoneExecutionResult.InvalidIntent(),
            durableOperation: HolyStoneCommandOperation.Remove,
            requiresDurablePlayerCommands: true,
            hasLocalLegacyAuthenticationAccess: true);
        var packet = HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.RemoveSubId,
            args =>
            {
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(23);
                args[HolyStoneProtocol.RemoveOrdinalArgumentIndex] = 4;
            });

        await InvokeAsync(fixture.Handler, packet);

        var command = fixture.Executor?.ExecutedCommand ??
            throw new InvalidOperationException(
                "raw Remove did not reach the durable executor");
        Check.Equal(
            (int)HolyStoneTargetLocation.KitBag,
            (int)command.TargetLocation,
            "raw Remove uses exact bag target");
        Check.Equal(23, command.TargetSlot, "raw Remove target slot");
        Check.Equal(3, command.SocketIndex, "raw Remove selected socket");
        Check.Equal(
            HolyStoneCommandEnvelope.NoStoneKitBagSlot,
            command.StoneKitBagSlot,
            "raw Remove has no material");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "raw Remove never reaches retry-ambiguous legacy storage");
    }

    private static async Task CheckRawDrillAsync()
    {
        await using var fixture = await CreateRawFixtureAsync(
            initialKitBag: DrillTargetBag(
                itemId: 1035,
                socketCount: 0));
        var packet = HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.DrillSubId,
            args => args[HolyStoneProtocol.TargetArgumentIndex] =
                16);

        await InvokeAsync(fixture.Handler, packet);

        var call = fixture.Store.LastCall!.Value;
        Check.Equal(
            (int)HolyStoneOperation.DrillSocket,
            (int)call.Operation,
            "raw Drill operation");
        Check.Equal(
            (int)HolyStoneTargetMode.KitBag,
            (int)call.TargetMode,
            "raw Drill uses the live-captured bag target");
        Check.Equal(
            WeaponSlot,
            call.TargetSlot,
            "raw Drill target slot");
    }

    private static async Task CheckRawNavigationAsync()
    {
        await CheckRawNavigationPageAsync(
            HolyStoneProtocol.MountSubId,
            [106, 206, 306],
            "Mount");
        await CheckRawNavigationPageAsync(
            HolyStoneProtocol.UpgradeSubId,
            [406, 506, 606],
            "Upgrade");
        await CheckRawNavigationPageAsync(
            HolyStoneProtocol.ImplementSpiritSubId,
            [706, 806, 906],
            "Implement Spirit");
        await CheckRawNavigationPageAsync(
            HolyStoneProtocol.CombineSubId,
            [907],
            "Combination");
        await CheckRawNavigationPageAsync(
            HolyStoneProtocol.AdvancedDrillSubId,
            [107, 207, 307],
            "Advanced Drill");
    }

    private static async Task CheckRawNavigationPageAsync(
        int requestSubId,
        IReadOnlyList<int> expectedResponseSubIds,
        string description)
    {
        await using var fixture = await CreateRawFixtureAsync();
        await InvokeAsync(
            fixture.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                requestSubId,
                static _ => { }));

        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            $"raw {description} navigation does not mutate");
        var response = fixture.Transport.ReadLegacyPackets().Single();
        Check.Equal(
            12 + (expectedResponseSubIds.Count * sizeof(int)),
            response.Length,
            $"raw {description} page response length");
        for (var index = 0;
             index < expectedResponseSubIds.Count;
             index++)
        {
            Check.Equal(
                expectedResponseSubIds[index],
                BinaryPrimitives.ReadInt32LittleEndian(
                    response.AsSpan(12 + (index * sizeof(int)))),
                $"raw {description} page sub-ID {index}");
        }
    }

    private static void CheckExactDrillCostPolicy()
    {
        foreach (var (itemId, description) in new (uint, string)[]
                 {
                     (1035, "weapon"),
                     (2113, "armor"),
                     (2834, "gloves")
                 })
        {
            var firstBag = DrillTargetBag(itemId, socketCount: 0);
            Check.True(
                HolyStoneItemMutator.TryGetDrillGoldCost(
                    TestItemContent.Catalog,
                    GameDefaults.DefaultEquipment(profession: 0),
                    firstBag,
                    profession: 0,
                    HolyStoneTargetMode.KitBag,
                    WeaponSlot,
                    out var firstCost),
                $"raw first bagged-{description} Drill resolves a Gold cost");
            Check.Equal(
                HolyStoneDrillCostPolicy.FirstSocketGoldCost,
                firstCost,
                $"raw first {description} Drill Gold cost");

            var secondBag = DrillTargetBag(itemId, socketCount: 1);
            Check.True(
                HolyStoneItemMutator.TryGetDrillGoldCost(
                    TestItemContent.Catalog,
                    GameDefaults.DefaultEquipment(profession: 0),
                    secondBag,
                    profession: 0,
                    HolyStoneTargetMode.KitBag,
                    WeaponSlot,
                    out var secondCost),
                $"raw second bagged-{description} Drill resolves a Gold cost");
            Check.Equal(
                HolyStoneDrillCostPolicy.SecondSocketGoldCost,
                secondCost,
                $"raw second {description} Drill Gold cost");
        }

        foreach (var (itemId, description) in new (uint, string)[]
                 {
                     (9030, "non-equipment"),
                     (8000, "stylish"),
                     (6000, "mount"),
                     (14500, "mount gear")
                 })
        {
            Check.True(
                !HolyStoneItemMutator.TryGetDrillGoldCost(
                    TestItemContent.Catalog,
                    GameDefaults.DefaultEquipment(profession: 0),
                    DrillTargetBag(itemId, socketCount: 0),
                    profession: 0,
                    HolyStoneTargetMode.KitBag,
                    WeaponSlot,
                    out _),
                $"raw Drill rejects {description}");
        }
    }

    private static async Task CheckJsonRawDrillGoldDebitAsync()
    {
        foreach (var (itemId, description) in new (uint, string)[]
                 {
                     (1035, "weapon"),
                     (2113, "armor"),
                     (2834, "gloves")
                 })
        {
            await CheckJsonRawDrillGoldDebitAsync(
                itemId,
                description);
        }
    }

    private static async Task CheckJsonRawDrillGoldDebitAsync(
        uint itemId,
        string description)
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-raw-holy-stone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync(
                "raw-holy-stone-gold",
                string.Empty);
            var kitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                WeaponSlot,
                (WeaponBefore with
                {
                    Id = itemId,
                    SocketCount = 0
                }).ToCompactString());
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "RawHolyGold",
                    Profession = 0,
                    Gold = 5_000,
                    KitBag = kitBag
                });

            var first = await store.ApplyWeaponHolyStoneAsync(
                account.Id,
                character.Id,
                HolyStoneOperation.DrillSocket,
                HolyStoneTargetMode.KitBag,
                WeaponSlot,
                socketIndex: -1,
                stoneKitBagSlot: -1,
                destinationKitBagSlot: -1);
            Check.Equal(
                5_000 -
                    HolyStoneDrillCostPolicy.FirstSocketGoldCost,
                first!.Gold,
                $"raw JSON first {description} Drill debits Gold atomically");
            Check.Equal(
                (short)1,
                KitBagSlots.GetItem(
                    first.KitBag,
                    WeaponSlot).SocketCount,
                $"raw JSON first Drill mutates the selected bagged {description}");

            var second = await store.ApplyWeaponHolyStoneAsync(
                account.Id,
                character.Id,
                HolyStoneOperation.DrillSocket,
                HolyStoneTargetMode.KitBag,
                WeaponSlot,
                socketIndex: -1,
                stoneKitBagSlot: -1,
                destinationKitBagSlot: -1);
            Check.Equal(
                5_000 -
                    HolyStoneDrillCostPolicy.FirstSocketGoldCost -
                    HolyStoneDrillCostPolicy.SecondSocketGoldCost,
                second!.Gold,
                $"raw JSON second {description} Drill debits Gold atomically");
            Check.Equal(
                (short)2,
                KitBagSlots.GetItem(
                    second.KitBag,
                    WeaponSlot).SocketCount,
                $"raw JSON second Drill mutates the selected bagged {description}");

            var third = await store.ApplyWeaponHolyStoneAsync(
                account.Id,
                character.Id,
                HolyStoneOperation.DrillSocket,
                HolyStoneTargetMode.KitBag,
                WeaponSlot,
                socketIndex: -1,
                stoneKitBagSlot: -1,
                destinationKitBagSlot: -1);
            Check.True(
                third is null,
                $"raw JSON {description} Drill cannot create a third basic socket");
            var persisted = await store.GetFirstCharacterAsync(account.Id)
                ?? throw new InvalidDataException(
                    "raw JSON Drill character disappeared");
            Check.Equal(
                second.Gold,
                persisted.Gold,
                $"rejected third raw {description} Drill cannot spend Gold");
            Check.Equal(
                (short)2,
                KitBagSlots.GetItem(
                    persisted.KitBag,
                    WeaponSlot).SocketCount,
                $"rejected third raw Drill cannot mutate the bagged {description}");
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    private static string DrillTargetBag(
        uint itemId,
        short socketCount) =>
        KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            WeaponSlot,
            (WeaponBefore with
            {
                Id = itemId,
                SocketCount = socketCount
            }).ToCompactString());
}
