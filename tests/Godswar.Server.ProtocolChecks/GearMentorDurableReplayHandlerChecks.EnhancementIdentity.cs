using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDurableReplayHandlerChecks
{
    private static async Task
        CheckOriginReconnectReplayPrecedesCurrentSnapshotsAsync()
    {
        var receipt = CreateSuccessfulAddReceipt();
        var executor = new EnhancementExecutor(
            GearEnhancementExecutionResult.InvalidIntent(),
            GearEnhancementExecutionResult.Duplicate(receipt));
        await using var fixture = CreateEnhancementFixture(executor);
        fixture.LiveCharacter.KitBag = CreateAfterEnhancementBag();
        SetField(
            fixture.Handler,
            "_gearEnhancerSelectionContext",
            new GearEnhancerSelectionContext(
                fixture.AccountId,
                fixture.LiveCharacter.Id,
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Add,
                DateTimeOffset.UtcNow.AddMinutes(1)));

        var args = Enumerable.Repeat(
                -1,
                GearEnhancerProtocol.FunctionActionArgumentCount)
            .ToArray();
        args[GearEnhancerProtocol.GearArgumentIndex] = 100;
        args[GearEnhancerProtocol.CatalystArgumentIndex] = 101;
        args[GearEnhancerProtocol.AttributeStoneArgumentIndex] = 102;
        var invocation = HandleGearEnhancementMethod.Invoke(
            fixture.Handler,
            [
                (uint)GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancerProtocol.AddAttributeSubId,
                args,
                (Guid?)ReplayOperationId,
                CancellationToken.None
            ]) as Task
            ?? throw new InvalidOperationException(
                "Origin replay handler did not return a task.");
        await invocation;

        Check.Equal(
            1,
            executor.ReplayCount,
            "Origin reconnect resolves the permanent inbox first");
        Check.Equal(
            0,
            executor.ExecuteCount,
            "Origin reconnect does not hash already-mutated snapshots");
        AssertEnhancementResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Replayed,
            "Origin Enhancer reconnect replay");
    }

    private static async Task
        CheckPhysicalGearMentorIgnoresInlineScratchTripletAsync()
    {
        var executor = new EnhancementExecutor(
            GearEnhancementExecutionResult.InvalidIntent(),
            GearEnhancementExecutionResult.ReplayNotFound());
        await using var fixture = CreateEnhancementFixture(executor);
        var context = new GearEnhancerSelectionContext(
            fixture.AccountId,
            fixture.LiveCharacter.Id,
            checked((uint)GearEnhancementCommandEnvelope
                .SpartaGearMentorNpcId),
            GearEnhancerProtocol.DialogIndex,
            operation: null,
            DateTimeOffset.UtcNow.AddMinutes(1));
        for (var slot = 0; slot < 3; slot++)
        {
            context.Apply(
                new GearEnhancerItemSelectionPacket(
                    BagPage: 0,
                    PageSlot: slot,
                    Selected: true),
                fixture.LiveCharacter.KitBag);
        }
        for (var slot = 0; slot < 3; slot++)
        {
            context.Apply(
                new GearEnhancerItemSelectionPacket(
                    BagPage: 0,
                    PageSlot: slot,
                    Selected: false),
                fixture.LiveCharacter.KitBag);
        }
        SetField(
            fixture.Handler,
            "_gearEnhancerSelectionContext",
            context);

        var args = Enumerable.Repeat(
                -1,
                GearEnhancerProtocol.FunctionActionArgumentCount)
            .ToArray();
        args[GearEnhancerProtocol.GearArgumentIndex] = 103;
        args[GearEnhancerProtocol.CatalystArgumentIndex] = 104;
        args[GearEnhancerProtocol.AttributeStoneArgumentIndex] = 105;
        var invocation = HandleGearEnhancementMethod.Invoke(
            fixture.Handler,
            [
                checked((uint)GearEnhancementCommandEnvelope
                    .SpartaGearMentorNpcId),
                GearEnhancerProtocol.DialogIndex,
                GearEnhancerProtocol.AddAttributeSubId,
                args,
                (Guid?)ReplayOperationId,
                CancellationToken.None
            ]) as Task
            ?? throw new InvalidOperationException(
                "Physical Gear Mentor handler did not return a task.");
        await invocation;

        Check.Equal(
            1,
            executor.ReplayCount,
            "physical Gear Mentor checks durable replay before execution");
        Check.Equal(
            1,
            executor.ExecuteCount,
            "physical Gear Mentor staged triplet executes once");
        var command = executor.ExecutedCommand ??
            throw new InvalidOperationException(
                "physical Gear Mentor executor captured no command.");
        Check.True(
            GearEnhancementCommandEnvelope.OrderedSelections(command)
                .Select(static selection => selection.KitBagSlot)
                .SequenceEqual([0, 1, 2]),
            "physical Gear Mentor inline scratch cannot override staged " +
            "Gear, Catalyst, and Attribute Stone slots");
        Check.Equal(
            GearEnhancerProtocol.DialogIndex,
            command.DialogIndex,
            "physical Gear Mentor preserves dialog identity");
    }
}
