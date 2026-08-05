using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertHolySpiritTransactionsAsync(
        string connectionString)
    {
        await AssertImplementationPersistsFinalRollAsync(connectionString);
        await AssertMountDetachRemountPreservesRollAsync(connectionString);
        await AssertLegacyNullStoneMountsWithFallbackAsync(connectionString);
    }

    private static async Task AssertImplementationPersistsFinalRollAsync(
        string connectionString)
    {
        var holyStone = SimpleItem(9030, grade: 10);
        var spirit = SimpleItem(9060);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "spirit",
            target: holyStone,
            stone: spirit);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(
            dataSource,
            holySpiritRandomSource:
                new MaximumHolySpiritRandomSource());

        var result = await ExecuteSelectedItemsAsync(
            executor,
            fixture,
            HolyStoneCommandOperation.ImplementSpirit,
            fixture.TargetSlot,
            holyStone,
            fixture.StoneSlot,
            spirit);
        var receipt = RequireReceipt(
            result,
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.SpiritImplemented,
            "Holy Spirit implementation");
        var implemented = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value.Item;
        Check.True(
            implemented.SocketCount == 1 &&
            implemented.Socket1EffectId == 1 &&
            implemented.Socket1Level == 10 &&
            implemented.Socket1Value == 800,
            "implementation persists the exact Grade-10 maximum roll");
        Check.True(
            !(await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.StoneSlot)).HasValue,
            "implementation consumes exactly one selected Spirit");
        Check.Equal(
            800_010_903,
            receipt.NativeResultSubId,
            "implementation returns the stock dynamic success result");
    }

    private static async Task AssertMountDetachRemountPreservesRollAsync(
        string connectionString)
    {
        var gear = Weapon(1);
        var implementedStone = SimpleItem(9030, grade: 10) with
        {
            SocketCount = 1,
            Socket1EffectId = 1,
            Socket1Level = 10,
            Socket1Value = 797
        };
        var fixture = await CreateFixtureAsync(
            connectionString,
            "cycle",
            target: gear,
            stone: implementedStone);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        RequireReceipt(
            await ExecuteSelectedItemsAsync(
                executor,
                fixture,
                HolyStoneCommandOperation.Mount,
                fixture.TargetSlot,
                gear,
                fixture.StoneSlot,
                implementedStone),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Mounted,
            "implemented stone Mount");
        var mountedGear = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value.Item;
        Check.Equal(
            797,
            mountedGear.Socket1Value!.Value,
            "Mount copies the exact persisted roll to gear");

        var removeReceipt = RequireReceipt(
            await ExecuteSelectedItemsAsync(
                executor,
                fixture,
                HolyStoneCommandOperation.Remove,
                fixture.TargetSlot,
                mountedGear,
                -1,
                CompactItemEntry.Empty,
                socketIndex: 0),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Removed,
            "implemented stone detach");
        var detachedStone = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            checked((short)removeReceipt.OutputKitBagSlot)))!.Value.Item;
        Check.True(
            detachedStone.Id == 9030 &&
            detachedStone.Grade == 10 &&
            detachedStone.Socket1EffectId == 1 &&
            detachedStone.Socket1Level == 10 &&
            detachedStone.Socket1Value == 797,
            "detach restores type, grade, effect, and exact roll");

        var emptyGear = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value.Item;
        RequireReceipt(
            await ExecuteSelectedItemsAsync(
                executor,
                fixture,
                HolyStoneCommandOperation.Mount,
                fixture.TargetSlot,
                emptyGear,
                checked((short)removeReceipt.OutputKitBagSlot),
                detachedStone),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Mounted,
            "detached stone remount");
        var remounted = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value.Item;
        Check.Equal(
            797,
            remounted.Socket1Value!.Value,
            "detach/remount never rerolls effectiveness");
    }

    private static async Task AssertLegacyNullStoneMountsWithFallbackAsync(
        string connectionString)
    {
        var gear = Weapon(1);
        var legacyStone = SimpleItem(9030, grade: 10) with
        {
            SocketCount = 1,
            Socket1EffectId = 1,
            Socket1Level = 10
        };
        var fixture = await CreateFixtureAsync(
            connectionString,
            "legacy",
            target: gear,
            stone: legacyStone);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);

        RequireReceipt(
            await ExecuteSelectedItemsAsync(
                CreateExecutor(dataSource),
                fixture,
                HolyStoneCommandOperation.Mount,
                fixture.TargetSlot,
                gear,
                fixture.StoneSlot,
                legacyStone),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Mounted,
            "legacy-null implemented stone Mount");
        var mounted = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value.Item;
        Check.Equal(
            1400,
            mounted.Socket1Value!.Value,
            "legacy-null Mount persists the deterministic former value");
    }

    private static async Task<HolyStoneExecutionResult>
        ExecuteSelectedItemsAsync(
            PostgresHolyStoneCommandExecutor executor,
            HolyFixture fixture,
            HolyStoneCommandOperation operation,
            short targetSlot,
            CompactItemEntry target,
            short materialSlot,
            CompactItemEntry material,
            int socketIndex = -1)
    {
        var hasMaterial = materialSlot >= 0;
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                operation,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                targetSlot,
                target.ToCompactString(),
                socketIndex,
                hasMaterial
                    ? materialSlot
                    : HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                hasMaterial ? material.ToCompactString() : "[]",
                out var command),
            $"{operation} integration command is valid");
        return await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                HolyStoneCommandEnvelope.Create(
                    fixture.Subject,
                    new CommandConnectionCorrelation(
                        Guid.NewGuid(),
                        CommandTransportKind.SecureTlsLegacy),
                    DateTimeOffset.UtcNow,
                    command)));
    }

    private sealed class MaximumHolySpiritRandomSource :
        IHolySpiritEffectivenessRandomSource
    {
        public int NextInclusive(
            int minimumInclusive,
            int maximumInclusive) => maximumInclusive;
    }
}
