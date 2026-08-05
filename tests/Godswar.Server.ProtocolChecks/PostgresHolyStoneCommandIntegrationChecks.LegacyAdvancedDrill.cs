using Godswar.Server.Application.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task
        AssertLegacyAdvancedDrillStackConsumptionAsync(
            string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "lgadv3",
            target: AdvancedGear(
                sockets: 2,
                quality: 20,
                grade: 25,
                holySuitCode: 710,
                id: 1035),
            targetSlot: 50,
            stone: SimpleItem(4272, stack: 99),
            stoneSlot: 27,
            gold: 3_000);
        await using var store = new PostgresGameStore(
            connectionString);
        await store.EnsureSeedDataAsync();

        var result = await store.ApplyWeaponHolyStoneAsync(
            fixture.AccountId,
            fixture.CharacterId,
            HolyStoneOperation.AdvancedDrillSocket,
            HolyStoneTargetMode.KitBag,
            fixture.TargetSlot,
            socketIndex: -1,
            stoneKitBagSlot: fixture.StoneSlot,
            destinationKitBagSlot: -1);
        Check.True(
            result is not null,
            "legacy PostgreSQL Advanced Drill accepts a stacked Spell III");

        var targetAfter = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value;
        var spellAfter = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.StoneSlot))!.Value;
        Check.True(
            targetAfter.Item.SocketCount == 3 &&
            spellAfter.Id == fixture.StoneItemId &&
            spellAfter.Item.Id == 4272 &&
            spellAfter.Item.Stack == 98,
            "legacy PostgreSQL Advanced Drill atomically opens socket III " +
            "and decrements exactly one spell from a stack of 99");
    }
}
