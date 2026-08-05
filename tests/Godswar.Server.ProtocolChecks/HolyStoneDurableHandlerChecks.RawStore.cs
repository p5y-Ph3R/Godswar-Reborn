using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task
        CheckRawStoreMaterialAndSocketRulesAsync()
    {
        var validSpirit = CreateFireSpirit(stack: 1);
        await AssertMountRejectedWithoutMutationAsync(
            CompactItemEntry.Empty,
            WeaponBefore,
            "empty material");
        await AssertMountRejectedWithoutMutationAsync(
            CreateSimpleBagItem(9030),
            WeaponBefore,
            "non-spirit 9030 material");
        await AssertMountRejectedWithoutMutationAsync(
            CreateSimpleBagItem(9999),
            WeaponBefore,
            "unknown material");
        await AssertMountRejectedWithoutMutationAsync(
            validSpirit,
            WeaponBefore with
            {
                SocketCount = 0
            },
            "undrilled target");
        await AssertMountRejectedWithoutMutationAsync(
            validSpirit,
            WeaponBefore with
            {
                Socket1EffectId = 1,
                Socket1Level = 4
            },
            "duplicate spirit");

        await CheckStackedSpiritConsumesOneAsync();
        await CheckRemoveDoesNotFallbackAsync();
        await CheckFullBagRemoveIsNonMutatingAsync();
        await CheckRemovePreservesStoneLevelAsync();
    }

    private static async Task
        AssertMountRejectedWithoutMutationAsync(
            CompactItemEntry material,
            CompactItemEntry weapon,
            string description)
    {
        var bag = GameDefaults.EmptyKitBag;
        if (!material.IsEmpty)
        {
            bag = KitBagSlots.SetSlot(
                bag,
                7,
                material.ToCompactString());
        }
        var outcome = await ApplyExactPacketToJsonStoreAsync(
            weapon,
            bag,
            CreateRawCanonicalMountPacket(
                HolyStoneProtocol.EncodeKitBagReference(WeaponSlot),
                HolyStoneProtocol.EncodeKitBagReference(7)));

        Check.True(
            outcome.Result is null,
            $"{description} Mount is rejected");
        Check.Equal(
            weapon,
            ReadBaggedWeapon(outcome.Persisted),
            $"{description} cannot mutate the target");
        Check.Equal(
            material,
            KitBagSlots.GetItem(
                outcome.Persisted.KitBag,
                7),
            $"{description} cannot consume material");
    }

    private static async Task CheckStackedSpiritConsumesOneAsync()
    {
        var material = CreateFireSpirit(stack: 3);
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            7,
            material.ToCompactString());
        var outcome = await ApplyExactPacketToJsonStoreAsync(
            WeaponBefore,
            bag,
            CreateRawCanonicalMountPacket(
                HolyStoneProtocol.EncodeKitBagReference(WeaponSlot),
                HolyStoneProtocol.EncodeKitBagReference(7)));

        Check.True(
            outcome.Result is not null,
            "allowlisted stacked Fire Spirit mounts");
        var weapon = ReadBaggedWeapon(outcome.Persisted);
        Check.True(
            weapon.Socket1EffectId == 1,
            "allowlisted Fire Spirit supplies its exact effect");
        Check.Equal(
            (short)2,
            KitBagSlots.GetItem(
                outcome.Persisted.KitBag,
                7).Stack,
            "Mount consumes exactly one stacked Fire Spirit");
    }

    private static async Task
        CheckRemoveDoesNotFallbackAsync()
    {
        var weapon = WeaponBefore with
        {
            Socket1EffectId = null,
            Socket1Level = null,
            Socket2EffectId = 2,
            Socket2Level = 7
        };
        var outcome = await ApplyExactPacketToJsonStoreAsync(
            weapon,
            GameDefaults.EmptyKitBag,
            CreateExactRemovePacket(socketOrdinal: 1));

        Check.True(
            outcome.Result is null,
            "Remove rejects an empty exact socket");
        Check.Equal(
            weapon,
            ReadBaggedWeapon(outcome.Persisted),
            "Remove cannot fall back to another occupied socket");
    }

    private static async Task
        CheckFullBagRemoveIsNonMutatingAsync()
    {
        var fullBag = GameDefaults.EmptyKitBag;
        var filler = CreateSimpleBagItem(2200);
        for (var slot = 0; slot < 96; slot++)
        {
            fullBag = KitBagSlots.SetSlot(
                fullBag,
                slot,
                filler.ToCompactString());
        }
        var weapon = WeaponBefore with
        {
            Socket1EffectId = 2,
            Socket1Level = 7
        };
        fullBag = KitBagSlots.SetSlot(
            fullBag,
            WeaponSlot,
            weapon.ToCompactString());
        var outcome = await ApplyExactPacketToJsonStoreAsync(
            weapon,
            fullBag,
            CreateExactRemovePacket(socketOrdinal: 1));

        Check.True(
            outcome.Result is null,
            "full-bag Remove is rejected");
        Check.Equal(
            weapon,
            ReadBaggedWeapon(outcome.Persisted),
            "full-bag Remove preserves the occupied socket");
        Check.True(
            string.Equals(
                fullBag,
                outcome.Persisted.KitBag,
                StringComparison.Ordinal),
            "full-bag Remove preserves every bag slot");
    }

    private static async Task CheckRemovePreservesStoneLevelAsync()
    {
        var weapon = WeaponBefore with
        {
            Socket1EffectId = 2,
            Socket1Level = 7
        };
        var outcome = await ApplyExactPacketToJsonStoreAsync(
            weapon,
            GameDefaults.EmptyKitBag,
            CreateExactRemovePacket(socketOrdinal: 1));

        Check.True(
            outcome.Result is not null,
            "occupied exact socket can be removed");
        Check.True(
            ReadBaggedWeapon(outcome.Persisted)
                .Socket1EffectId is null,
            "successful Remove clears the requested socket");
        var output = KitBagSlots.GetItem(
            outcome.Persisted.KitBag,
            0);
        Check.Equal(
            (uint)HolyStoneItemMutator.HeatedHolyStoneItemId,
            output.Id,
            "Remove produces a heated Holy Stone");
        Check.Equal(
            (short)7,
            output.Grade,
            "Remove preserves the mounted stone level in Grade");
    }

    private static GamePacket CreateExactRemovePacket(
        int socketOrdinal) =>
        HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.RemoveSubId,
            args =>
            {
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(WeaponSlot);
                args[HolyStoneProtocol.RemoveOrdinalArgumentIndex] =
                    socketOrdinal;
            });

    private static async Task<RawJsonStoreOutcome>
        ApplyExactPacketToJsonStoreAsync(
            CompactItemEntry weapon,
            string kitBag,
            GamePacket packet)
    {
        Check.True(
            HolyStoneProtocol.TryReadMutation(
                packet,
                out _,
                out _,
                out var intent),
            "crafted raw store packet is canonical");
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-raw-holy-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync(
                "raw-holy-store",
                string.Empty);
            kitBag = KitBagSlots.SetSlot(
                kitBag,
                WeaponSlot,
                weapon.ToCompactString());
            var created = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "RawHolyStore",
                    Profession = 0,
                    Gold = 10_000,
                    KitBag = kitBag
                });
            var operation = intent.Operation switch
            {
                HolyStoneCommandOperation.Mount =>
                    HolyStoneOperation.MountStone,
                HolyStoneCommandOperation.Remove =>
                    HolyStoneOperation.RemoveStone,
                HolyStoneCommandOperation.Drill =>
                    HolyStoneOperation.DrillSocket,
                HolyStoneCommandOperation.AdvancedDrill =>
                    HolyStoneOperation.AdvancedDrillSocket,
                _ => throw new InvalidDataException(
                    "Unknown crafted Holy Stone operation.")
            };
            Check.Equal(
                (int)HolyStoneTargetLocation.KitBag,
                (int)intent.TargetLocation,
                "crafted raw store packet targets the kitbag");
            var result = await store.ApplyWeaponHolyStoneAsync(
                account.Id,
                created.Id,
                operation,
                HolyStoneTargetMode.KitBag,
                intent.TargetSlot,
                intent.SocketIndex,
                intent.StoneKitBagSlot,
                destinationKitBagSlot: -1);
            var persisted = await store.GetFirstCharacterAsync(
                account.Id)
                ?? throw new InvalidDataException(
                    "Raw Holy Stone character disappeared.");
            return new RawJsonStoreOutcome(result, persisted);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    private static CompactItemEntry ReadBaggedWeapon(
        GameCharacter character) =>
        KitBagSlots.GetItem(character.KitBag, WeaponSlot);

    private static CompactItemEntry CreateFireSpirit(
        short stack) =>
        CreateSimpleBagItem(9060) with
        {
            Grade = 4,
            Stack = stack
        };

    private static CompactItemEntry CreateSimpleBagItem(
        uint itemId) =>
        CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 1
        };

    private sealed record RawJsonStoreOutcome(
        GameCharacter? Result,
        GameCharacter Persisted);
}
