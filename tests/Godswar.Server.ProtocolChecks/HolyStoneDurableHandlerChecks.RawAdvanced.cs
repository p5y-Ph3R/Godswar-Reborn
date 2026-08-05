using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private const int AdvancedSpellSlot = 5;
    private const int FourthSocketSpellSlot = 6;

    private static async Task CheckRawAdvancedDrillFallbackAsync()
    {
        CheckRawBasicDrillLevelPolicy();
        await CheckRawAdvancedDrillRoutingAsync();
        await CheckStockRawAdvancedDrillSelectionRoutingAsync();
        await CheckRawHolyStonePersistenceFailureResponseAsync();
        await CheckRawDrillRejectionResponsesAsync();
        await CheckJsonRawAdvancedDrillAtomicityAsync();
    }

    private static void CheckRawBasicDrillLevelPolicy()
    {
        AssertRawDrillEligibility(
            itemId: 1008,
            socketCount: 0,
            HolyStoneDrillEligibilityFailure.ItemLevel,
            expectedGoldCost: 0,
            "level 90 gear cannot receive socket 1");
        AssertRawDrillEligibility(
            itemId: 1009,
            socketCount: 0,
            HolyStoneDrillEligibilityFailure.None,
            expectedGoldCost: 230,
            "level 100 gear can receive socket 1");
        AssertRawDrillEligibility(
            itemId: 1009,
            socketCount: 1,
            HolyStoneDrillEligibilityFailure.ItemLevel,
            expectedGoldCost: 0,
            "level 100 gear cannot receive socket 2");
        AssertRawDrillEligibility(
            itemId: 1013,
            socketCount: 1,
            HolyStoneDrillEligibilityFailure.None,
            expectedGoldCost: 2_300,
            "level 120 gear can receive socket 2");
    }

    private static void AssertRawDrillEligibility(
        uint itemId,
        short socketCount,
        HolyStoneDrillEligibilityFailure expectedFailure,
        int expectedGoldCost,
        string description)
    {
        Check.True(
            HolyStoneItemMutator.TryEvaluateDrill(
                TestItemContent.Catalog,
                GameDefaults.DefaultEquipment(profession: 0),
                DrillTargetBag(itemId, socketCount),
                profession: 0,
                HolyStoneOperation.DrillSocket,
                HolyStoneTargetMode.KitBag,
                WeaponSlot,
                stoneKitBagSlot: -1,
                out var failure,
                out var goldCost),
            $"{description} resolves an authoritative target");
        Check.Equal(
            (int)expectedFailure,
            (int)failure,
            description);
        Check.Equal(
            expectedGoldCost,
            goldCost,
            $"{description} Gold cost");
    }

    private static async Task CheckRawAdvancedDrillRoutingAsync()
    {
        var target = CreateAdvancedDrillTarget(socketCount: 2);
        var spell = CreateSocketSpell(
            HolyStoneDrillEligibilityPolicy.SocketSpellThreeItemId,
            stack: 2);
        var bag = CreateAdvancedDrillBag(
            target,
            AdvancedSpellSlot,
            spell);
        await using var fixture = await CreateRawFixtureAsync(
            initialKitBag: bag,
            storeMutation: character =>
            {
                character.KitBag = KitBagSlots.SetSlot(
                    character.KitBag,
                    WeaponSlot,
                    (target with { SocketCount = 3 })
                        .ToCompactString());
                character.KitBag = KitBagSlots.SetSlot(
                    character.KitBag,
                    AdvancedSpellSlot,
                    (spell with { Stack = 1 }).ToCompactString());
                return character;
            });

        await InvokeAsync(
            fixture.Handler,
            CreateRawAdvancedDrillPacket(AdvancedSpellSlot));

        var call = fixture.Store.LastCall ??
            throw new InvalidOperationException(
                "raw Advanced Drill did not reach the store");
        Check.Equal(
            (int)HolyStoneOperation.AdvancedDrillSocket,
            (int)call.Operation,
            "raw action 701 maps to distinct Advanced Drill operation");
        Check.Equal(
            WeaponSlot,
            call.TargetSlot,
            "raw Advanced Drill preserves target slot");
        Check.Equal(
            AdvancedSpellSlot,
            call.StoneSlot,
            "raw Advanced Drill preserves Socket Spell slot");
        AssertNpcResult(
            fixture.Transport.ReadLegacyPackets()[0],
            HolyStoneNativeResults.DrilledSubId,
            "raw Advanced Drill success");
    }

    private static async Task CheckRawDrillRejectionResponsesAsync()
    {
        await AssertRawAdvancedRejectedAsync(
            CreateAdvancedDrillTarget(socketCount: 2),
            CreateSocketSpell(
                HolyStoneDrillEligibilityPolicy.SocketSpellFourItemId,
                stack: 1),
            HolyStoneNativeResults.AdvancedSpellRequiredSubId,
            "wrong Socket Spell");
        await AssertRawAdvancedRejectedAsync(
            CreateAdvancedDrillTarget(socketCount: 4),
            CreateSocketSpell(
                HolyStoneDrillEligibilityPolicy.SocketSpellFourItemId,
                stack: 1),
            HolyStoneNativeResults.AdvancedMaximumSocketsSubId,
            "maximum Advanced Drill sockets");
        await AssertRawAdvancedRejectedAsync(
            CreateAdvancedDrillTarget(socketCount: 1),
            CreateSocketSpell(
                HolyStoneDrillEligibilityPolicy.SocketSpellThreeItemId,
                stack: 1),
            HolyStoneNativeResults.DrillPrerequisiteSubId,
            "missing first two sockets");
        await AssertRawAdvancedRejectedAsync(
            CreateAdvancedDrillTarget(
                socketCount: 3) with
            {
                Quality = 14
            },
            CreateSocketSpell(
                HolyStoneDrillEligibilityPolicy.SocketSpellFourItemId,
                stack: 1),
            HolyStoneNativeResults.DrillPrerequisiteSubId,
            "fourth socket equipment prerequisite");

        await AssertRawBasicRejectedAsync(
            DrillTargetBag(itemId: 1008, socketCount: 0),
            initialGold: 10_000,
            HolyStoneNativeResults.DrillPrerequisiteSubId,
            "basic level prerequisite");
        await AssertRawBasicRejectedAsync(
            DrillTargetBag(itemId: 1035, socketCount: 0),
            initialGold: 229,
            HolyStoneNativeResults.InsufficientFundsSubId,
            "real basic Drill insufficient funds");
    }

    private static async Task AssertRawAdvancedRejectedAsync(
        CompactItemEntry target,
        CompactItemEntry spell,
        int expectedSubId,
        string description)
    {
        var bag = CreateAdvancedDrillBag(
            target,
            AdvancedSpellSlot,
            spell);
        await using var fixture = await CreateRawFixtureAsync(
            initialKitBag: bag);

        await InvokeAsync(
            fixture.Handler,
            CreateRawAdvancedDrillPacket(AdvancedSpellSlot));

        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            $"{description} cannot reach raw persistence");
        AssertNpcResult(
            fixture.Transport.ReadLegacyPackets().Single(),
            expectedSubId,
            description);
    }

    private static async Task AssertRawBasicRejectedAsync(
        string bag,
        int initialGold,
        int expectedSubId,
        string description)
    {
        await using var fixture = await CreateRawFixtureAsync(
            initialKitBag: bag,
            initialGold: initialGold);
        await InvokeAsync(
            fixture.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.DrillSubId,
                args => args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(WeaponSlot)));

        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            $"{description} cannot reach raw persistence");
        AssertNpcResult(
            fixture.Transport.ReadLegacyPackets().Single(),
            expectedSubId,
            description);
    }

    private static async Task CheckJsonRawAdvancedDrillAtomicityAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-raw-advanced-drill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync(
                "raw-advanced-drill",
                string.Empty);
            var target = CreateAdvancedDrillTarget(socketCount: 2);
            var spellThree = CreateSocketSpell(
                HolyStoneDrillEligibilityPolicy.SocketSpellThreeItemId,
                stack: 2);
            var spellFour = CreateSocketSpell(
                HolyStoneDrillEligibilityPolicy.SocketSpellFourItemId,
                stack: 1);
            var bag = CreateAdvancedDrillBag(
                target,
                AdvancedSpellSlot,
                spellThree);
            bag = KitBagSlots.SetSlot(
                bag,
                FourthSocketSpellSlot,
                spellFour.ToCompactString());
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "RawAdvanced",
                    Profession = 0,
                    Gold = 97,
                    KitBag = bag
                });

            var third = await store.ApplyWeaponHolyStoneAsync(
                account.Id,
                character.Id,
                HolyStoneOperation.AdvancedDrillSocket,
                HolyStoneTargetMode.KitBag,
                WeaponSlot,
                socketIndex: -1,
                stoneKitBagSlot: AdvancedSpellSlot,
                destinationKitBagSlot: -1);
            Check.True(third is not null, "raw JSON third socket commits");
            Check.Equal(97, third!.Gold, "Advanced Drill spends no Gold");
            Check.Equal(
                (short)3,
                KitBagSlots.GetItem(
                    third.KitBag,
                    WeaponSlot).SocketCount,
                "raw JSON third socket updates target atomically");
            Check.Equal(
                (short)1,
                KitBagSlots.GetItem(
                    third.KitBag,
                    AdvancedSpellSlot).Stack,
                "raw JSON third socket consumes exactly one Spell III");

            var fourth = await store.ApplyWeaponHolyStoneAsync(
                account.Id,
                character.Id,
                HolyStoneOperation.AdvancedDrillSocket,
                HolyStoneTargetMode.KitBag,
                WeaponSlot,
                socketIndex: -1,
                stoneKitBagSlot: FourthSocketSpellSlot,
                destinationKitBagSlot: -1);
            Check.True(fourth is not null, "raw JSON fourth socket commits");
            Check.Equal(97, fourth!.Gold, "fourth socket spends no Gold");
            Check.Equal(
                (short)4,
                KitBagSlots.GetItem(
                    fourth.KitBag,
                    WeaponSlot).SocketCount,
                "raw JSON fourth socket updates target atomically");
            Check.True(
                KitBagSlots.GetItem(
                    fourth.KitBag,
                    FourthSocketSpellSlot).IsEmpty,
                "raw JSON fourth socket consumes the last Spell IV");

            var beforeRejected = fourth.KitBag;
            var rejected = await store.ApplyWeaponHolyStoneAsync(
                account.Id,
                character.Id,
                HolyStoneOperation.AdvancedDrillSocket,
                HolyStoneTargetMode.KitBag,
                WeaponSlot,
                socketIndex: -1,
                stoneKitBagSlot: AdvancedSpellSlot,
                destinationKitBagSlot: -1);
            Check.True(rejected is null, "raw JSON fifth socket is rejected");
            var persisted = await store.GetFirstCharacterAsync(account.Id)
                ?? throw new InvalidDataException(
                    "raw Advanced Drill character disappeared");
            Check.True(
                string.Equals(
                    beforeRejected,
                    persisted.KitBag,
                    StringComparison.Ordinal),
                "rejected raw Advanced Drill changes neither gear nor material");
            Check.Equal(
                97,
                persisted.Gold,
                "rejected raw Advanced Drill changes no currency");

            await AssertJsonAdvancedRejectedWithoutMutationAsync(
                store,
                "RawWrongSpell",
                CreateAdvancedDrillTarget(socketCount: 2),
                CreateSocketSpell(
                    HolyStoneDrillEligibilityPolicy
                        .SocketSpellFourItemId,
                    stack: 2),
                "wrong Socket Spell");
            await AssertJsonAdvancedRejectedWithoutMutationAsync(
                store,
                "RawFourthPrereq",
                CreateAdvancedDrillTarget(socketCount: 3) with
                {
                    Grade = 19
                },
                CreateSocketSpell(
                    HolyStoneDrillEligibilityPolicy
                        .SocketSpellFourItemId,
                    stack: 2),
                "fourth socket prerequisite");
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    private static async Task
        AssertJsonAdvancedRejectedWithoutMutationAsync(
            JsonGameStore store,
            string characterName,
            CompactItemEntry target,
            CompactItemEntry spell,
            string description)
    {
        var account = await store.LoginOrCreateAccountAsync(
            $"raw-{characterName}",
            string.Empty);
        var bag = CreateAdvancedDrillBag(
            target,
            AdvancedSpellSlot,
            spell);
        var character = await store.CreateCharacterAsync(
            account.Id,
            new GameCharacter
            {
                Name = characterName,
                Profession = 0,
                Gold = 7_777,
                KitBag = bag
            });

        var result = await store.ApplyWeaponHolyStoneAsync(
            account.Id,
            character.Id,
            HolyStoneOperation.AdvancedDrillSocket,
            HolyStoneTargetMode.KitBag,
            WeaponSlot,
            socketIndex: -1,
            stoneKitBagSlot: AdvancedSpellSlot,
            destinationKitBagSlot: -1);
        Check.True(
            result is null,
            $"raw JSON {description} is rejected");
        var persisted = (await store.GetCharactersAsync(account.Id))
            .Single(value => value.Id == character.Id);
        Check.True(
            string.Equals(
                bag,
                persisted.KitBag,
                StringComparison.Ordinal),
            $"raw JSON {description} changes neither gear nor material");
        Check.Equal(
            7_777,
            persisted.Gold,
            $"raw JSON {description} changes no currency");
    }

    private static GamePacket CreateRawAdvancedDrillPacket(
        int spellSlot) =>
        HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.AdvancedDrillSubId,
            args =>
            {
                args[HolyStoneProtocol.AdvancedDrillScratchArgumentIndex] =
                    0;
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(WeaponSlot);
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(spellSlot);
            });

    private static string CreateAdvancedDrillBag(
        CompactItemEntry target,
        int spellSlot,
        CompactItemEntry spell)
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            WeaponSlot,
            target.ToCompactString());
        return KitBagSlots.SetSlot(
            bag,
            spellSlot,
            spell.ToCompactString());
    }

    private static CompactItemEntry CreateAdvancedDrillTarget(
        short socketCount) =>
        WeaponBefore with
        {
            Id = 1035,
            Quality = HolyStoneDrillEligibilityPolicy.ArcaneQuality,
            Grade = HolyStoneDrillEligibilityPolicy
                .FourthSocketMinimumGrade,
            HolySuitCode = 601,
            SocketCount = socketCount
        };

    private static CompactItemEntry CreateSocketSpell(
        uint itemId,
        short stack) =>
        CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = stack
        };
}
