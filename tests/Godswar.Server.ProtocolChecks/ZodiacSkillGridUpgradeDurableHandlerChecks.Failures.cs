using Godswar.Server.Application.Zodiac;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    ZodiacSkillGridUpgradeDurableHandlerChecks
{
    private static async Task
        CheckUncertainFailuresLeaveMarkerPendingAsync()
    {
        var failingExecutor = new CapturingExecutor(
            (_, _) => Task.FromException<
                ZodiacSkillGridUpgradeExecutionResult>(
                    new IOException("injected Zodiac provider failure")));
        await using (var fixture = CreateFixture(
            execution: null,
            executorOverride: failingExecutor))
        {
            await InvokeAsync(
                fixture.Handler,
                CreateUpgradePacket(OperationId));
            Check.Equal(
                1,
                failingExecutor.Count,
                "failed Zodiac provider was invoked once");
            Check.Equal(
                0,
                fixture.Transport.Events.Count,
                "provider failure emits no terminal response");
        }

        var cancellationExecutor = new CapturingExecutor(
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(
                    ZodiacSkillGridUpgradeExecutionResult
                        .PreconditionFailed());
            });
        await using (var fixture = CreateFixture(
            execution: null,
            executorOverride: cancellationExecutor))
        {
            using var source = new CancellationTokenSource();
            source.Cancel();
            var cancelled = false;
            try
            {
                await InvokeAsync(
                    fixture.Handler,
                    CreateUpgradePacket(OperationId),
                    source.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Check.True(
                cancelled,
                "cancelled Zodiac operation propagates cancellation");
            Check.Equal(
                0,
                fixture.Transport.Events.Count,
                "cancelled Zodiac operation emits no terminal response");
        }

        var mismatchedReceipt =
            new ZodiacSkillGridUpgradeExecutionReceipt(
                characterId: CharacterId + 1,
                ZodiacSkillGridUpgradeReceiptStatus.Succeeded,
                GridIndex,
                previousLevel: 1,
                currentLevel: 2,
                currentZodiacLevel: 9,
                requiredZodiacLevel: 1,
                energyCost: 5,
                energyBefore: 1_000,
                energyRemainderBeforeX100: 50,
                energyAfter: 995,
                energyRemainderAfterX100: 50,
                talentPointCost: 7,
                talentPointsBefore: 890,
                talentPointsAfter: 883,
                selectedSkillId: 10_050,
                auditReference: "audit:zodiac-mismatched-owner",
                outboxEventId:
                    Guid.Parse(
                        "eb563263-84c8-45ca-a330-300797394388"));
        await using (var fixture = CreateFixture(
            ZodiacSkillGridUpgradeExecutionResult.Committed(
                mismatchedReceipt)))
        {
            await InvokeAsync(
                fixture.Handler,
                CreateUpgradePacket(OperationId));
            Check.Equal(
                0,
                fixture.Transport.Events.Count,
                "mismatched receipt emits no projection or terminal result");
            Check.Equal(
                1_000,
                fixture.Character.ZodiacEnergy,
                "mismatched receipt cannot change handler mirror");
            Check.Equal(
                1_000,
                fixture.RegistryMirror.ZodiacEnergy,
                "mismatched receipt cannot change registry mirror");
        }
    }
}
