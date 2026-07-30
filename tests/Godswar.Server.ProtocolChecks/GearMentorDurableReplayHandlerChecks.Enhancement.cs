using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDurableReplayHandlerChecks
{
    private const long EnhancementInventoryRevision = 81;
    private static readonly MethodInfo
        HandleDurableGearEnhancementMethod =
        FindHandlerMethod("HandleDurableGearEnhancementAsync");
    private static readonly MethodInfo HandleGearEnhancementMethod =
        FindHandlerMethod("HandleGearEnhancerOperationAsync");
    private static readonly MethodInfo HandleGearMentorTransactionMethod =
        FindHandlerMethod("HandleGearMentorTransactionAsync");

    private static async Task
        CheckUnavailableGearEnhancementLeavesOperationPendingAsync()
    {
        await using var fixture = CreateEnhancementFixture(
            executor: null);
        await InvokeDurableEnhancementAsync(
            fixture.Handler,
            selections: null);

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "Gear Enhancement provider outage emits no terminal result");
    }

    private static async Task
        CheckGearEnhancementReplayMissLeavesOperationPendingAsync()
    {
        var executor = new EnhancementExecutor(
            GearEnhancementExecutionResult.ReplayNotFound());
        await using var fixture = CreateEnhancementFixture(executor);
        await InvokeDurableEnhancementAsync(
            fixture.Handler,
            selections: null);

        Check.Equal(
            1,
            executor.ReplayCount,
            "selectionless Gear Enhancement retry checks its inbox");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "a valid-route replay miss leaves the UUID pending");
    }

    private static async Task
        CheckSecureTokenlessGearEnhancementFailsClosedAsync()
    {
        var executor = new EnhancementExecutor(
            GearEnhancementExecutionResult.ReplayNotFound());
        await using var fixture = CreateEnhancementFixture(executor);
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
                (Guid?)null,
                CancellationToken.None
            ]) as Task
            ?? throw new InvalidOperationException(
                "Gear Enhancement handler did not return a task.");
        await invocation;

        Check.Equal(
            0,
            executor.ReplayCount,
            "secure tokenless enhancement does not query durable replay");
        Check.Equal(
            0,
            executor.ExecuteCount,
            "secure tokenless enhancement cannot mutate");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "secure tokenless enhancement has no UUID to settle");
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.Equal(
            1,
            packets.Count,
            "secure tokenless enhancement sends one stock rejection");
        AssertNpcResult(
            packets[0],
            GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
            GearEnhancerProtocol.InvalidSelectionResultSubId,
            "secure tokenless enhancement",
            GearEnhancerProtocol.OriginDialogIndex);
    }

    private static async Task
        CheckSecureTokenlessGearMentorTransactionFailsClosedAsync()
    {
        await using var fixture = CreateFixture(
            GearMentorMaterialConversionExecutionResult
                .ReplayNotFound());
        SetField(
            fixture.Handler,
            "_gearEnhancerSelectionContext",
            new GearEnhancerSelectionContext(
                7,
                fixture.LiveCharacter.Id,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                operation: null,
                DateTimeOffset.UtcNow.AddMinutes(1)));

        var args = Enumerable.Repeat(
                -1,
                GearEnhancerProtocol.FunctionActionArgumentCount)
            .ToArray();
        args[GearEnhancerProtocol.GearArgumentIndex] = 100;
        var invocation = HandleGearMentorTransactionMethod.Invoke(
            fixture.Handler,
            [
                (uint)GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.TransformCrystalSubId,
                args,
                (Guid?)null,
                CancellationToken.None
            ]) as Task
            ?? throw new InvalidOperationException(
                "Gear Mentor transaction handler did not return a task.");
        await invocation;

        Check.Equal(
            0,
            fixture.Executor.TransformReplayCount,
            "secure tokenless Mentor transaction skips durable replay");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "secure tokenless Mentor transaction cannot mutate");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "secure tokenless Mentor transaction has no UUID to settle");
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.Equal(
            1,
            packets.Count,
            "secure tokenless Mentor transaction sends one stock rejection");
        AssertNpcResult(
            packets[0],
            GearEnhancerProtocol.SpartaEnhancerNpcId,
            GearEnhancerProtocol.SelectedItemMissingResultSubId,
            "secure tokenless Mentor transaction");
    }

    private static async Task CheckOriginGearEnhancementCommitOrderingAsync()
    {
        var receipt = CreateSuccessfulAddReceipt();
        var executor = new EnhancementExecutor(
            GearEnhancementExecutionResult.Committed(receipt),
            GearEnhancementExecutionResult.ReplayNotFound());
        await using var fixture = CreateEnhancementFixture(executor);
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
                "Gear Enhancement handler did not return a task.");
        await invocation;

        Check.Equal(
            1,
            executor.ReplayCount,
            "Origin Enhancer secure commit checks durable replay first");
        Check.Equal(
            1,
            executor.ExecuteCount,
            "Origin Enhancer secure commit executes once");
        var command = executor.ExecutedCommand ??
            throw new InvalidOperationException(
                "Origin Enhancer executor did not capture its command.");
        Check.Equal(
            (int)GearEnhancementCommandOperation.Add,
            (int)command.Operation,
            "Origin Enhancer preserves Add operation");
        Check.Equal(
            checked((int)GearEnhancerProtocol.SpartaOriginEnhancerNpcId),
            command.NpcId,
            "Origin Enhancer preserves NPC identity");
        Check.Equal(
            GearEnhancerProtocol.OriginDialogIndex,
            command.DialogIndex,
            "Origin Enhancer preserves dialog identity");
        Check.True(
            GearEnhancementCommandEnvelope.OrderedSelections(command)
                .Select(static selection => selection.Role)
                .SequenceEqual(
                [
                    GearEnhancementCommandItemRole.Gear,
                    GearEnhancementCommandItemRole.Catalyst,
                    GearEnhancementCommandItemRole.AttributeStone
                ]),
            "Origin Enhancer command keeps gear/catalyst/stone role order");

        AssertEnhancementResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Applied,
            "Origin Enhancer commit");
    }

    private static async Task
        CheckGearEnhancementReplayUsesStoredEndpointAsync()
    {
        var receipt = CreateSuccessfulAddReceipt();
        var executor = new EnhancementExecutor(
            GearEnhancementExecutionResult.Duplicate(receipt));
        await using var fixture = CreateEnhancementFixture(executor);

        await InvokePacketAsync(
            fixture.Handler,
            CreateFunctionActionPacket(
                UnroutedNpcId,
                GearEnhancerProtocol.AddAttributeSubId,
                ReplayOperationId));

        Check.Equal(
            1,
            executor.ReplayCount,
            "unrouted Gear Enhancement retry checks its durable inbox");
        Check.Equal(
            0,
            executor.ExecuteCount,
            "unrouted Gear Enhancement retry never starts a mutation");
        AssertEnhancementResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Replayed,
            "unrouted Origin Enhancer replay");
    }

    private static void AssertEnhancementResponse(
        EnhancementFixture fixture,
        GearEnhancementExecutionReceipt receipt,
        SecureLegacyCommandDisposition disposition,
        string description)
    {
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(
            packets.Count >= 2,
            $"{description} sends stock result and bag refresh");
        AssertNpcResult(
            packets[0],
            checked((uint)receipt.NpcId),
            receipt.NativeResultSubId,
            description,
            receipt.DialogIndex);
        AssertSecureResult(
            fixture.Transport.CommandResults.Single(),
            disposition,
            receipt.Family,
            receipt.NativeResultSubId,
            receipt.InventoryRevision,
            ReplayOperationId,
            description);
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            $"{description} sends 0x0102 last");
    }

    private static async Task InvokeDurableEnhancementAsync(
        GameClientHandler handler,
        GearEnhancerSelectionTriplet? selections)
    {
        var invocation = HandleDurableGearEnhancementMethod.Invoke(
            handler,
            [
                (uint)GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Add,
                ReplayOperationId,
                selections,
                GameDefaults.EmptyKitBag,
                "none",
                CancellationToken.None
            ]) as Task
            ?? throw new InvalidOperationException(
                "Durable Gear Enhancement handler did not return a task.");
        await invocation;
    }

    private static EnhancementFixture CreateEnhancementFixture(
        EnhancementExecutor? executor)
    {
        var original = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var before = CreateBeforeEnhancementBag();
        var after = CreateAfterEnhancementBag();
        var persisted = original with
        {
            Character = original.Character! with
            {
                Loadout = original.Character.Loadout with
                {
                    KitBag = after
                }
            }
        };
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(persisted)
            ?? throw new InvalidOperationException(
                "Gear Enhancement fixture character did not hydrate.");
        hydrated.Character.KitBag = before;

        var transport = new ReplayCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            persisted.AccountId,
            hydrated.Character);
        var handler = new GameClientHandler(
            session,
            new ReplayGameStore(),
            registry,
            new ReplaySnapshotReader(persisted),
            WorldContentReaderTestFixtures.Empty,
            gearEnhancementCommands: executor);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = persisted.AccountId,
                Username = "gear-enhancement-durable-check"
            });
        SetField(handler, "_character", hydrated.Character);
        return new EnhancementFixture(
            persisted.AccountId,
            session,
            transport,
            handler,
            hydrated.Character);
    }

    private static GearEnhancementExecutionReceipt
        CreateSuccessfulAddReceipt()
    {
        var beforeGear = CreateGear();
        var afterGear = beforeGear with
        {
            Attribute1 = 0,
            AttributeLevel1 = 1,
            Bound = 1
        };
        var catalyst = CreateCatalyst();
        var stone = CreateStone();
        return new GearEnhancementExecutionReceipt(
            characterId: 19,
            GearEnhancementCommandOperation.Add,
            checked((int)GearEnhancerProtocol.SpartaOriginEnhancerNpcId),
            GearEnhancerProtocol.OriginDialogIndex,
            GearEnhancementCommandResultStatus.Succeeded,
            GearEnhancementNativeResults.AddSucceededSubId,
            mutations:
            [
                new GearEnhancementReceiptMutation(
                    GearEnhancementCommandItemRole.Gear,
                    0,
                    beforeGear.Id,
                    beforeGear.ToCompactString(),
                    afterGear.ToCompactString()),
                new GearEnhancementReceiptMutation(
                    GearEnhancementCommandItemRole.Catalyst,
                    1,
                    catalyst.Id,
                    catalyst.ToCompactString(),
                    CompactItemEntry.Empty.ToCompactString()),
                new GearEnhancementReceiptMutation(
                    GearEnhancementCommandItemRole.AttributeStone,
                    2,
                    stone.Id,
                    stone.ToCompactString(),
                    CompactItemEntry.Empty.ToCompactString())
            ],
            EnhancementInventoryRevision,
            "audit:gear-enhancement:handler-check",
            Guid.Parse("ac2f6166-524e-4f77-bb43-568aac14a355"));
    }

    private static string CreateBeforeEnhancementBag()
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            CreateGear().ToCompactString());
        bag = KitBagSlots.SetSlot(
            bag,
            1,
            CreateCatalyst().ToCompactString());
        return KitBagSlots.SetSlot(
            bag,
            2,
            CreateStone().ToCompactString());
    }

    private static string CreateAfterEnhancementBag() =>
        KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            (CreateGear() with
            {
                Attribute1 = 0,
                AttributeLevel1 = 1,
                Bound = 1
            }).ToCompactString());

    private static CompactItemEntry CreateGear() =>
        CompactItemEntry.Empty with
        {
            Id = 10_001,
            Quality = 1,
            Grade = 1,
            Stack = 1
        };

    private static CompactItemEntry CreateCatalyst() =>
        CompactItemEntry.Empty with
        {
            Id = GearEnhancementMaterialCatalog.FlameSparkItemId,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 1
        };

    private static CompactItemEntry CreateStone() =>
        CompactItemEntry.Empty with
        {
            Id = 9_930,
            Quality = 1,
            Grade = 1,
            Stack = 1
        };

    private sealed record EnhancementFixture(
        int AccountId,
        ClientSession Session,
        ReplayCaptureTransport Transport,
        GameClientHandler Handler,
        GameCharacter LiveCharacter) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class EnhancementExecutor(
        GearEnhancementExecutionResult executeResult,
        GearEnhancementExecutionResult? replayResult = null) :
        IGearEnhancementCommandExecutor
    {
        public int ExecuteCount { get; private set; }

        public int ReplayCount { get; private set; }

        public GearEnhancementCommand? ExecutedCommand { get; private set; }

        public Task<GearEnhancementExecutionResult> ExecuteAsync(
            CommandEnvelope<GearEnhancementCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            ExecutedCommand = envelope.Command;
            return Task.FromResult(executeResult);
        }

        public Task<GearEnhancementExecutionResult> TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            GearEnhancementCommandOperation operation,
            Guid clientOperationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            Check.Equal(
                (int)GearEnhancementCommandOperation.Add,
                (int)operation,
                "Gear Enhancement replay operation");
            Check.Equal(
                ReplayOperationId,
                clientOperationId,
                "Gear Enhancement replay UUID");
            return Task.FromResult(replayResult ?? executeResult);
        }
    }
}
