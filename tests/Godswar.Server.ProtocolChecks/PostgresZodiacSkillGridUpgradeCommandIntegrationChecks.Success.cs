using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridUpgradeCommandIntegrationChecks
{
    private static async Task AssertSuccessAndOwnershipAsync(
        string connectionString)
    {
        await AssertSuccessfulUpgradeAsync(connectionString);
        await AssertWrongOwnerAsync(connectionString);
    }

    private static async Task AssertSuccessfulUpgradeAsync(
        string connectionString)
    {
        const int gridIndex = 0;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "success",
            energy: 10,
            energyRemainderX100: 37,
            talentPoints: 20,
            zodiacLevel: 1);
        var envelope = CreateEnvelope(
            fixture,
            gridIndex,
            Guid.NewGuid());
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(dataSource).ExecuteAsync(
            envelope);
        var receipt = RequireReceipt(
            result,
            ZodiacSkillGridUpgradeExecutionDisposition.Committed,
            "successful Zodiac upgrade");
        Check.True(
            receipt.Status ==
                ZodiacSkillGridUpgradeReceiptStatus.Succeeded &&
            receipt.CharacterId == fixture.CharacterId &&
            receipt.GridIndex == gridIndex &&
            receipt.PreviousLevel == 1 &&
            receipt.CurrentLevel == 2 &&
            receipt.CurrentZodiacLevel == 1 &&
            receipt.RequiredZodiacLevel == 1 &&
            receipt.EnergyCost == 5 &&
            receipt.EnergyBefore == 10 &&
            receipt.EnergyRemainderBeforeX100 == 37 &&
            receipt.EnergyAfter == 5 &&
            receipt.EnergyRemainderAfterX100 == 37 &&
            receipt.TalentPointCost == 7 &&
            receipt.TalentPointsBefore == 20 &&
            receipt.TalentPointsAfter == 13 &&
            receipt.SelectedSkillId == -1 &&
            receipt.AggregateRevision == 2 &&
            receipt.OutboxEventId is { } eventId &&
            eventId != Guid.Empty &&
            long.TryParse(receipt.AuditReference, out var auditId) &&
            auditId > 0,
            "successful upgrade returns exact immutable resource evidence");
        Check.True(
            result.HasAuthoritativeProjection &&
            result.CurrentEnergy == 5 &&
            result.CurrentEnergyRemainderX100 == 37 &&
            result.CurrentTalentPoints == 13 &&
            result.CurrentLevel == 2 &&
            result.SelectedSkillId == -1,
            "successful upgrade returns the committed projection");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex);
        AssertCommitted(
            state,
            expectedEnergy: 5,
            expectedRemainder: 37,
            expectedTalentPoints: 13,
            expectedLevel: 2,
            "successful upgrade");
    }

    private static async Task AssertWrongOwnerAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "owner",
            energy: 10,
            energyRemainderX100: 0,
            talentPoints: 20);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(dataSource).ExecuteAsync(
            CreateEnvelope(
                fixture,
                gridIndex: 0,
                Guid.NewGuid(),
                new CommandSubject(
                    fixture.AccountId + 1,
                    fixture.CharacterId)));
        Check.True(
            result.Disposition ==
                ZodiacSkillGridUpgradeExecutionDisposition
                    .PreconditionFailed &&
            result.Receipt is null &&
            !result.HasAuthoritativeProjection,
            "wrong-owner Zodiac upgrade fails without leaking state");
        AssertUntouched(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex: 0),
            expectedEnergy: 10,
            expectedRemainder: 0,
            expectedTalentPoints: 20,
            expectedLevel: 1,
            "wrong-owner upgrade");
    }

    private static void AssertCommitted(
        ZodiacUpgradeDurableState state,
        int expectedEnergy,
        int expectedRemainder,
        int expectedTalentPoints,
        int expectedLevel,
        string description)
    {
        Check.True(
            state.Energy == expectedEnergy &&
            state.EnergyRemainderX100 == expectedRemainder &&
            state.TalentPoints == expectedTalentPoints &&
            state.Level == expectedLevel &&
            state.SelectedSkillId == -1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.OutboxCount == 1 &&
            state.HasLatestWinsEvidence &&
            state.HasAuditResourceEvidence &&
            state.TerminalRejectedCount == 0 &&
            state.CurrencyLedgerCount == 0,
            $"{description} atomically commits latest-wins evidence");
    }

    private static void AssertUntouched(
        ZodiacUpgradeDurableState state,
        int expectedEnergy,
        int expectedRemainder,
        int expectedTalentPoints,
        int expectedLevel,
        string description)
    {
        Check.True(
            state.Energy == expectedEnergy &&
            state.EnergyRemainderX100 == expectedRemainder &&
            state.TalentPoints == expectedTalentPoints &&
            state.Level == expectedLevel &&
            state.SelectedSkillId == -1 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.ConflictCount == 0 &&
            state.OutboxCount == 0 &&
            !state.HasLatestWinsEvidence &&
            !state.HasAuditResourceEvidence &&
            state.TerminalRejectedCount == 0 &&
            state.CurrencyLedgerCount == 0,
            $"{description} leaves no durable side effect");
    }
}
