using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertHighLevelSignetRejectsBeforeRollAsync(
        string connectionString)
    {
        const short catalystSlot = 15;
        var catalyst = SimpleItem(9054);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "uplimit",
            target: SimpleItem(9030, grade: 7),
            stone: SimpleItem(9042),
            additionalBagItems: [(catalystSlot, catalyst)]);
        var random = new FixedUpgradeRandomSource(0);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteUpgradeAsync(
                CreateExecutor(
                    dataSource,
                    upgradeRandomSource: random),
                fixture,
                Guid.NewGuid(),
                catalystSlot,
                catalyst.ToCompactString()),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.SignetProtectionUnavailable,
            "high-level signet protection limit");
        Check.Equal(
            HolyStoneNativeResults.SignetProtectionUnavailableSubId,
            receipt.NativeResultSubId,
            "level-seven rejection has a dedicated native dialog result");
        Check.Equal(
            0,
            random.CallCount,
            "high-level signet rejection consumes no RNG");
        Check.Equal(
            fixture.TargetState,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.TargetSlot))!.Value.Item.ToCompactString(),
            "high-level signet rejection leaves target unchanged");
        Check.Equal(
            fixture.StoneState,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.StoneSlot))!.Value.Item.ToCompactString(),
            "high-level signet rejection leaves Eclipse unchanged");
        Check.Equal(
            catalyst.ToCompactString(),
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                catalystSlot))!.Value.Item.ToCompactString(),
            "high-level signet rejection leaves signet unchanged");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Upgrade),
            "high-level signet protection limit");
    }
}
