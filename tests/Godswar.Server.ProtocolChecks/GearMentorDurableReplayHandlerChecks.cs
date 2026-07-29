using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDurableReplayHandlerChecks
{
    private const uint UnroutedNpcId = 900_001;
    private static readonly Guid ReplayOperationId =
        Guid.Parse("68942fbd-27db-447a-b854-69796d02e5ba");
    private static readonly MethodInfo HandlePacketMethod =
        FindHandlerMethod("HandlePacketAsync");

    public static async Task RunAsync()
    {
        await CheckDurableReplayWinsBeforeRouteRejectionAsync();
        await CheckReplayMissContinuesRouteRejectionAsync();
        await CheckUnavailableExecutorLeavesOperationPendingAsync();
        await CheckUnavailableMakeStoneExecutorLeavesOperationPendingAsync();
        await CheckUnavailableDecomposeExecutorLeavesOperationPendingAsync();
        await CheckDecomposeReplayWinsBeforeRouteRejectionAsync();
        await CheckUnavailableGearEnhancementLeavesOperationPendingAsync();
        await CheckGearEnhancementReplayMissLeavesOperationPendingAsync();
        await CheckOriginGearEnhancementCommitOrderingAsync();
        await CheckOriginReconnectReplayPrecedesCurrentSnapshotsAsync();
        await CheckPhysicalGearMentorIgnoresInlineScratchTripletAsync();
        await CheckGearEnhancementReplayUsesStoredEndpointAsync();
    }

    private static async Task
        CheckDurableReplayWinsBeforeRouteRejectionAsync()
    {
        var receipt = CreateSuccessfulTransformReceipt();
        var replay = GearMentorMaterialConversionExecutionResult
            .Duplicate(receipt);
        await using var fixture = CreateFixture(replay);

        await InvokePacketAsync(
            fixture.Handler,
            CreateFunctionActionPacket(
                UnroutedNpcId,
                GearEnhancerProtocol.TransformCrystalSubId,
                ReplayOperationId));

        Check.Equal(
            1,
            fixture.Executor.TransformReplayCount,
            "unrouted secure retry checks the durable inbox once");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "unrouted secure retry never executes a new mutation");
        Check.Equal(
            fixture.PersistedKitBag,
            fixture.LiveCharacter.KitBag,
            "durable replay reloads the authoritative bag projection");

        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(
            packets.Count >= 2,
            "durable replay sends the stock result and a full bag refresh");
        AssertNpcResult(
            packets[0],
            UnroutedNpcId,
            receipt.NativeResultSubId,
            "durable replay stock response");
        Check.True(
            packets.Skip(1).Any(
                packet => ReadOpcode(packet) == 0x2731),
            "durable replay sends kit-bag detail after the stock response");

        var secureResult = fixture.Transport.CommandResults.Single();
        AssertSecureResult(
            secureResult,
            SecureLegacyCommandDisposition.Replayed,
            CommandFamily.GearMentorTransformCrystal,
            receipt.NativeResultSubId,
            receipt.InventoryRevision,
            ReplayOperationId,
            "durable replay");
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            "0x0102 is emitted after every stock bag-refresh packet");
        Check.Equal(
            packets.Count,
            fixture.Transport.Events.Count(
                static value => value == "legacy"),
            "every stock packet precedes the terminal secure result");
    }

    private static async Task
        CheckReplayMissContinuesRouteRejectionAsync()
    {
        await using var fixture = CreateFixture(
            GearMentorMaterialConversionExecutionResult
                .ReplayNotFound());

        await InvokePacketAsync(
            fixture.Handler,
            CreateFunctionActionPacket(
                UnroutedNpcId,
                GearEnhancerProtocol.TransformCrystalSubId,
                ReplayOperationId));

        Check.Equal(
            1,
            fixture.Executor.TransformReplayCount,
            "route rejection follows one durable replay lookup");
        Check.Equal(
            0,
            fixture.SnapshotReader.ReadCount,
            "a replay miss does not reload an unchanged inventory");

        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.Equal(
            1,
            packets.Count,
            "replay miss continues to the single stock rejection");
        AssertNpcResult(
            packets[0],
            UnroutedNpcId,
            GearMentorMaterialConversionNativeResults
                .TransformInvalidCrystalSubId,
            "replay-miss stock rejection");

        var secureResult = fixture.Transport.CommandResults.Single();
        AssertSecureResult(
            secureResult,
            SecureLegacyCommandDisposition.Rejected,
            CommandFamily.GearMentorTransformCrystal,
            GearMentorMaterialConversionNativeResults
                .TransformInvalidCrystalSubId,
            inventoryRevision: 0,
            ReplayOperationId,
            "replay-miss route rejection");
        Check.True(
            fixture.Transport.Events.SequenceEqual(
                ["legacy", "command-result"]),
            "replay miss reaches normal stock-then-0x0102 rejection");
    }

    private static void AssertNpcResult(
        byte[] packet,
        uint expectedNpcId,
        int expectedResultSubId,
        string description,
        int expectedDialogIndex = GearEnhancerProtocol.DialogIndex)
    {
        Check.Equal(16, packet.Length, $"{description} length");
        Check.Equal(
            Opcodes.NpcFunctionActionResponse,
            ReadOpcode(packet),
            $"{description} opcode");
        Check.Equal(
            expectedNpcId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(4, 4)),
            $"{description} NPC");
        Check.Equal(
            expectedDialogIndex,
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.AsSpan(8, 4)),
            $"{description} dialog");
        Check.Equal(
            expectedResultSubId,
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.AsSpan(12, 4)),
            $"{description} result");
    }

    private static void AssertSecureResult(
        SecureLegacyCommandResult actual,
        SecureLegacyCommandDisposition expectedDisposition,
        CommandFamily expectedFamily,
        int expectedResultCode,
        long inventoryRevision,
        Guid operationId,
        string description)
    {
        Check.True(
            actual.Disposition == expectedDisposition,
            $"{description} disposition");
        Check.Equal(
            (ushort)expectedFamily,
            actual.CommandFamily,
            $"{description} family");
        Check.Equal(
            checked((uint)expectedResultCode),
            actual.ResultCode,
            $"{description} result code");
        Check.Equal(
            checked((ulong)inventoryRevision),
            actual.InventoryRevision,
            $"{description} inventory revision");
        Check.Equal(
            operationId,
            actual.OperationId,
            $"{description} operation ID");
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, 2));

    private static async Task InvokePacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "Game packet handler did not return a task.");
        await invocation;
    }

    private static MethodInfo FindHandlerMethod(string name) =>
        typeof(GameClientHandler).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");
}
