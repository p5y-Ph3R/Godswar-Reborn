using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    public static async Task RunAsync()
    {
        await CheckCommittedMountProjectionAndOrderingAsync();
        await CheckReplayPrecedesStateCaptureAsync();
        await CheckReplayPrecedesRouteRejectionAsync();
        await CheckMalformedUuidCannotDowngradeAsync();
        await CheckSecureMutationRequiresUuidAsync();
        await CheckSecureAliasesCannotDowngradeAsync();
        await CheckNavigationNeedsNoUuidAsync();
        await CheckCrossCityReplayUsesCurrentEndpointAsync();
        await CheckWrongCharacterReceiptStaysPendingAsync();
        await CheckProviderUncertaintyStaysPendingAsync();
        await CheckProjectionUncertaintyStaysPendingAsync();
        await CheckSettlementEvictionsAsync();
        await CheckRawExactWireSemanticsAsync();
        await CheckAdvancedDrillStaysFailClosedAsync();
    }

    private static async Task
        CheckCommittedMountProjectionAndOrderingAsync()
    {
        var receipt = CreateMountReceipt();
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound(),
            HolyStoneExecutionResult.Committed(receipt));

        await InvokeMountAsync(fixture, OperationId);

        Check.Equal(1, fixture.Executor!.ReplayCount, "Mount checks inbox first");
        Check.Equal(1, fixture.Executor.ExecuteCount, "new Mount executes once");
        var command = fixture.Executor.ExecutedCommand ??
            throw new InvalidOperationException(
                "Mount executor did not capture its command.");
        Check.Equal(
            WeaponBefore.ToCompactString(),
            command.ExpectedTargetCompactItemState,
            "Mount captures full weapon state");
        Check.Equal(
            StoneBefore.ToCompactString(),
            command.ExpectedStoneCompactItemState,
            "Mount captures full stone state");
        Check.Equal(
            WeaponAfter,
            EquipmentSlots.GetItem(
                fixture.LiveCharacter.Equipment,
                fixture.LiveCharacter.Profession,
                EquipmentSlots.Weapon),
            "committed Mount reloads authoritative weapon");
        Check.True(
            KitBagSlots.GetItem(
                fixture.LiveCharacter.KitBag,
                StoneSlot).IsEmpty,
            "committed Mount reloads consumed stone");
        Check.Equal(
            777,
            fixture.LiveCharacter.CurrentHp,
            "Mount projection preserves live HP");
        Check.Equal(
            9_000_000,
            fixture.LiveCharacter.Gold,
            "Mount projection refreshes authoritative Gold");

        AssertDurableResponse(
            fixture,
            SecureLegacyCommandDisposition.Applied,
            expectedDeletionAcknowledgements: 1,
            "committed Mount");
    }

    private static async Task CheckReplayPrecedesStateCaptureAsync()
    {
        var receipt = CreateMountReceipt();
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.Duplicate(receipt),
            liveAfterMutation: true);

        await InvokeMountAsync(fixture, OperationId);

        Check.Equal(1, fixture.Executor!.ReplayCount, "Mount retry checks inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "Mount retry does not capture post-mutation state and execute");
        Check.Equal(
            9_000_000,
            fixture.LiveCharacter.Gold,
            "Mount replay refreshes authoritative Gold");
        AssertDurableResponse(
            fixture,
            SecureLegacyCommandDisposition.Replayed,
            expectedDeletionAcknowledgements: 0,
            "replayed Mount");
    }

    private static async Task
        CheckReplayPrecedesRouteRejectionAsync()
    {
        var receipt = CreateMountReceipt();
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.Duplicate(receipt),
            liveAfterMutation: true,
            installNpcRoute: false);

        await InvokeMountAsync(fixture, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "unrouted retry checks permanent inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "unrouted durable replay never mutates");
        AssertDurableResponse(
            fixture,
            SecureLegacyCommandDisposition.Replayed,
            expectedDeletionAcknowledgements: 0,
            "pre-route Mount replay");
    }

    private static async Task
        CheckMalformedUuidCannotDowngradeAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound());
        var malformed = CreateMountPacket(OperationId).Buffer[..^4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            malformed,
            checked((ushort)malformed.Length));

        await InvokeAsync(
            fixture.Handler,
            new GamePacket(malformed, OperationId));

        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "malformed UUID Mount never reaches executor");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "malformed UUID Mount cannot downgrade to legacy store");
        AssertRejectedSecureResult(fixture, "malformed UUID Mount");
    }

    private static async Task CheckSecureMutationRequiresUuidAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound());

        await InvokeMountAsync(fixture, operationId: null);

        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "unidentified secure Mount never reaches durable executor");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "unidentified secure Mount cannot downgrade to legacy store");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "unidentified command cannot fabricate a UUID result");
        var response = fixture.Transport.ReadClearLegacyPackets().Single();
        AssertNpcResult(
            response,
            HolyStoneNativeResults.WrongSelectionSubId,
            "unidentified secure Mount");
    }

    private static async Task CheckSecureAliasesCannotDowngradeAsync()
    {
        await using var unidentified = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound());
        foreach (var alias in new[] { 106, 206, 306, 406 })
        {
            await InvokeAsync(
                unidentified.Handler,
                HolyStoneCommandContractChecks.CreatePacket(
                    HolyStoneProtocol.SpartaNpcId,
                    alias,
                    static _ => { }));
        }

        Check.Equal(
            0,
            unidentified.Store.HolyStoneCount,
            "secure aliases without UUID never reach the legacy store");
        Check.Equal(
            4,
            unidentified.Transport.ReadClearLegacyPackets().Count,
            "secure aliases without UUID receive controlled rejections");

        await using var identified = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound());
        await InvokeAsync(
            identified.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.MountAliasOneSubId,
                static _ => { },
                OperationId));
        Check.Equal(
            0,
            identified.Store.HolyStoneCount,
            "secure alias with UUID cannot downgrade after shape failure");
        AssertRejectedSecureResult(
            identified,
            "secure UUID alias");
    }

    private static async Task CheckNavigationNeedsNoUuidAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound());
        var navigation = HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.MountSubId,
            static _ => { });

        await InvokeAsync(fixture.Handler, navigation);

        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "navigation does not enter the durable command executor");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "navigation does not mutate through the legacy store");
        var response = fixture.Transport.ReadClearLegacyPackets().Single();
        AssertNpcResult(response, 106, "Mount navigation");
    }

    private static async Task
        CheckCrossCityReplayUsesCurrentEndpointAsync()
    {
        var receipt = CreateMountReceipt(
            npcId: HolyStoneCommandEnvelope.SpartaNpcId);
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.Duplicate(receipt),
            liveAfterMutation: true,
            requestNpcId: HolyStoneProtocol.AthensNpcId);

        await InvokeMountAsync(
            fixture,
            OperationId,
            HolyStoneProtocol.AthensNpcId);

        AssertDurableResponse(
            fixture,
            SecureLegacyCommandDisposition.Replayed,
            expectedDeletionAcknowledgements: 0,
            "cross-city Mount replay");
        var response = fixture.Transport
            .ReadClearLegacyPackets()[0];
        Check.Equal(
            HolyStoneProtocol.AthensNpcId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                response.AsSpan(4, sizeof(uint))),
            "cross-city replay projects through the current endpoint");
    }

    private static async Task
        CheckWrongCharacterReceiptStaysPendingAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.Duplicate(
                CreateMountReceipt(characterId: 20)),
            liveAfterMutation: true);

        await InvokeMountAsync(fixture, OperationId);

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "receipt bound to another character emits no response");
        Check.Equal(
            0,
            fixture.SnapshotReader.ReadCount,
            "wrong-character receipt is rejected before projection");
    }

    private static async Task
        CheckProviderUncertaintyStaysPendingAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound(),
            providerUnavailable: true);

        await InvokeMountAsync(fixture, OperationId);

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "missing provider emits no false terminal response");
    }

    private static async Task
        CheckProjectionUncertaintyStaysPendingAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound(),
            HolyStoneExecutionResult.Committed(
                CreateMountReceipt()),
            projectionFails: true);

        await InvokeMountAsync(fixture, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            "projection failure follows durable commit");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "projection uncertainty emits no false terminal response");
    }

    private static void AssertDurableResponse(
        HolyStoneFixture fixture,
        SecureLegacyCommandDisposition disposition,
        int expectedDeletionAcknowledgements,
        string description)
    {
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(packets.Count >= 2, $"{description} refreshes projection");
        AssertNpcResult(
            packets[0],
            HolyStoneNativeResults.MountedSubId,
            description);
        Check.Equal(
            expectedDeletionAcknowledgements,
            packets.Count(IsKitBagDeletionAcknowledgement),
            $"{description} non-idempotent deletion ACK count");
        var status = packets.Single(packet =>
            packet.Length >= 128 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2, sizeof(ushort))) == 0x27B6);
        Check.Equal(
            fixture.LiveCharacter.Gold,
            BinaryPrimitives.ReadInt32LittleEndian(
                status.AsSpan(124, sizeof(int))),
            $"{description} status projects authoritative Gold");
        var result = fixture.Transport.CommandResults.Single();
        Check.Equal(
            (int)disposition,
            (int)result.Disposition,
            $"{description} disposition");
        Check.Equal(
            (ushort)CommandFamily.HolyStoneMount,
            result.CommandFamily,
            $"{description} family");
        Check.Equal(
            OperationId,
            result.OperationId,
            $"{description} operation UUID");
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            $"{description} sends secure result last");
    }

    private static void AssertRejectedSecureResult(
        HolyStoneFixture fixture,
        string description)
    {
        var result = fixture.Transport.CommandResults.Single();
        Check.Equal(
            (int)SecureLegacyCommandDisposition.Rejected,
            (int)result.Disposition,
            $"{description} disposition");
        Check.Equal(
            (ushort)CommandFamily.HolyStoneMount,
            result.CommandFamily,
            $"{description} family");
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            $"{description} sends secure result last");
    }

    private static void AssertNpcResult(
        byte[] packet,
        int expectedSubId,
        string description)
    {
        Check.Equal(
            Opcodes.NpcFunctionActionResponse,
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2, sizeof(ushort))),
            $"{description} stock response opcode");
        Check.Equal(
            expectedSubId,
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.AsSpan(12, sizeof(int))),
            $"{description} stock result");
    }

    private static bool IsKitBagDeletionAcknowledgement(
        byte[] packet) =>
        packet.Length == 16 &&
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort))) == 0x2744 &&
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(12, sizeof(ushort))) == ushort.MaxValue &&
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(14, sizeof(ushort))) == ushort.MaxValue;
}
