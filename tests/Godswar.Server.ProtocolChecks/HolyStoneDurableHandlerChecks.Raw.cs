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
        await CheckRawStoreMaterialAndSocketRulesAsync();
        await CheckRawSettlementOrderingAsync();
    }

    private static async Task CheckRawMountAsync(
        int targetReference,
        HolyStoneTargetMode expectedMode,
        int targetSlot,
        int stoneSlot,
        string description)
    {
        await using var fixture = await CreateRawFixtureAsync();
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

        var call = fixture.Store.LastCall ??
            throw new InvalidOperationException(
                $"{description} did not reach the raw store.");
        Check.Equal(
            (int)HolyStoneOperation.MountStone,
            (int)call.Operation,
            $"{description} operation");
        Check.Equal(
            (int)expectedMode,
            (int)call.TargetMode,
            $"{description} target mode");
        Check.Equal(targetSlot, call.TargetSlot, $"{description} target");
        Check.Equal(stoneSlot, call.StoneSlot, $"{description} stone");
        Check.Equal(-1, call.SocketIndex, $"{description} socket");
    }

    private static async Task CheckRawRemoveAsync()
    {
        await using var fixture = await CreateRawFixtureAsync();
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

        var call = fixture.Store.LastCall!.Value;
        Check.Equal(
            (int)HolyStoneTargetMode.KitBag,
            (int)call.TargetMode,
            "raw Remove uses exact bag target");
        Check.Equal(23, call.TargetSlot, "raw Remove target slot");
        Check.Equal(3, call.SocketIndex, "raw Remove selected socket");
        Check.Equal(-1, call.StoneSlot, "raw Remove has no material");
    }

    private static async Task CheckRawDrillAsync()
    {
        await using var fixture = await CreateRawFixtureAsync();
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
        var kitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            WeaponSlot,
            (WeaponBefore with
            {
                SocketCount = 0
            }).ToCompactString());
        Check.True(
            HolyStoneItemMutator.TryGetDrillGoldCost(
                GameDefaults.DefaultEquipment(profession: 0),
                kitBag,
                profession: 0,
                HolyStoneTargetMode.KitBag,
                WeaponSlot,
                out var firstCost),
            "raw first bagged-weapon Drill resolves a Gold cost");
        Check.Equal(
            HolyStoneDrillCostPolicy.FirstSocketGoldCost,
            firstCost,
            "raw first Drill Gold cost");

        kitBag = KitBagSlots.SetSlot(
            kitBag,
            WeaponSlot,
            (WeaponBefore with
            {
                SocketCount = 1
            }).ToCompactString());
        Check.True(
            HolyStoneItemMutator.TryGetDrillGoldCost(
                GameDefaults.DefaultEquipment(profession: 0),
                kitBag,
                profession: 0,
                HolyStoneTargetMode.KitBag,
                WeaponSlot,
                out var secondCost),
            "raw second bagged-weapon Drill resolves a Gold cost");
        Check.Equal(
            HolyStoneDrillCostPolicy.SecondSocketGoldCost,
            secondCost,
            "raw second Drill Gold cost");
    }

    private static async Task CheckJsonRawDrillGoldDebitAsync()
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
                "raw JSON first Drill debits Gold atomically");
            Check.Equal(
                (short)1,
                KitBagSlots.GetItem(
                    first.KitBag,
                    WeaponSlot).SocketCount,
                "raw JSON first Drill mutates the selected bagged weapon");

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
                "raw JSON second Drill debits Gold atomically");
            Check.Equal(
                (short)2,
                KitBagSlots.GetItem(
                    second.KitBag,
                    WeaponSlot).SocketCount,
                "raw JSON second Drill mutates the selected bagged weapon");

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
                "raw JSON Drill cannot create a third basic socket");
            var persisted = await store.GetFirstCharacterAsync(account.Id)
                ?? throw new InvalidDataException(
                    "raw JSON Drill character disappeared");
            Check.Equal(
                second.Gold,
                persisted.Gold,
                "rejected third raw Drill cannot spend Gold");
            Check.Equal(
                (short)2,
                KitBagSlots.GetItem(
                    persisted.KitBag,
                    WeaponSlot).SocketCount,
                "rejected third raw Drill cannot mutate the bagged weapon");
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }
}
