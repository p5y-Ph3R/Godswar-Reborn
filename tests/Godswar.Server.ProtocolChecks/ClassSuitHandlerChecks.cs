using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ClassSuitHandlerChecks
{
    public const string CheckName =
        "Class Suit database-authored multi-route handler";

    public static async Task RunAsync()
    {
        await CheckOpenAdvertisesBothRoutesInOrderAsync();
        await CheckMenuNavigationAndDetailsAsync();
        await CheckSecureMutationExecutesAndSettlesAsync();
        await CheckChangedSnapshotRetryReplaysAsync();
        await CheckPreRouteIntentConflictSettlesAsync();
        await CheckMalformedPreRoutePacketCannotReplayAsync();
        await CheckMissingSecureIdentityFailsClosedAsync();
        await CheckMalformedSecureMutationFailsClosedAsync();
    }

    private static async Task
        CheckOpenAdvertisesBothRoutesInOrderAsync()
    {
        await using var fixture = await CreateFixtureAsync();

        await InvokeAsync(
            fixture.Handler,
            CreateDialogOpenPacket());

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            2,
            packets.Count,
            "one physical Gear Mentor advertises both authored routes");
        AssertDialogOpen(
            packets[0],
            GearEnhancerProtocol.DialogIndex,
            "Gear Mentor primary route");
        AssertDialogOpen(
            packets[1],
            ClassSuitProtocol.DialogIndex,
            "Class Suit secondary route");
    }

    private static async Task CheckMenuNavigationAndDetailsAsync()
    {
        await using var fixture = await CreateFixtureAsync();

        await InvokeAsync(
            fixture.Handler,
            CreateClassSuitActionPacket(
                ClassSuitProtocol.InitialMenuRequestSubId));
        await InvokeAsync(
            fixture.Handler,
            CreateClassSuitActionPacket(
                (int)ClassSuitWireOperation.ExchangeTierOne));
        await InvokeAsync(
            fixture.Handler,
            CreateClassSuitActionPacket(119));

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            3,
            packets.Count,
            "Class Suit menu, operation page, and detail each reply once");
        AssertFunctionResponse(
            packets[0],
            ClassSuitProtocol.InitialMenuSubIds,
            "database-authored Class Suit initial menu");
        AssertFunctionResponse(
            packets[1],
            [110, 111, 119],
            "Tier I exchange page");
        AssertFunctionResponse(
            packets[2],
            [1119],
            "Tier I exchange detail");
        Check.Equal(
            0,
            fixture.Executor.ReplayCount,
            "navigation never enters the durable mutation executor");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "navigation never mutates Class Suit state");
    }

    private static async Task
        CheckSecureMutationExecutesAndSettlesAsync()
    {
        await using var fixture = await CreateFixtureAsync();

        await InvokeAsync(
            fixture.Handler,
            CreateTierOneMutationPacket(OperationId));

        Check.Equal(
            1,
            fixture.Executor.ReplayCount,
            "secure Class Suit mutation checks its durable inbox once");
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            "replay miss executes one authoritative mutation");
        Check.Equal(
            1,
            fixture.Snapshots.ReadCount,
            "durable result refreshes the character projection");

        var envelope = fixture.Executor.Envelope ??
            throw new InvalidOperationException(
                "Class Suit executor did not capture its envelope.");
        Check.Equal(
            (int)ClassSuitCommandOperation.ExchangeTierI,
            (int)envelope.Command.Operation,
            "handler maps stock operation 100 to Tier I exchange");
        Check.Equal(
            GearSlot,
            envelope.Command.Gear.KitBagSlot,
            "handler captures the selected gear slot");
        Check.Equal(
            CommonWeapon.ToCompactString(),
            envelope.Command.Gear.ExpectedCompactItemState,
            "handler captures authoritative pre-mutation gear state");
        Check.Equal(
            InsigniaSlot,
            envelope.Command.PrimaryMaterial!.Value.KitBagSlot,
            "handler captures the selected insignia slot");
        Check.Equal(
            TierOneInsignia.ToCompactString(),
            envelope.Command.PrimaryMaterial.Value
                .ExpectedCompactItemState,
            "handler captures authoritative insignia state");
        Check.True(
            envelope.Command.SecondaryMaterial is null,
            "Tier I exchange cannot inject a second material");
        Check.True(
            fixture.Executor.ReplayIntent ==
                ClassSuitReplayIntent.FromCommand(envelope.Command),
            "handler replay lookup uses the exact parsed stable intent");

        Check.Equal(
            TierOneWeapon.Id,
            KitBagSlots.GetItemId(
                fixture.Character.KitBag,
                GearSlot),
            "post-commit projection contains the converted weapon");
        Check.Equal(
            0u,
            KitBagSlots.GetItemId(
                fixture.Character.KitBag,
                InsigniaSlot),
            "post-commit projection removes the consumed insignia");

        var packets = fixture.Transport.ReadLegacyPackets();
        var result = packets.FirstOrDefault(packet =>
            ReadOpcode(packet) == Opcodes.NpcFunctionActionResponse);
        Check.True(
            result is not null,
            "committed Class Suit mutation sends a stock result");
        AssertFunctionResponse(
            result!,
            [120],
            "Tier I exchange success");
        Check.True(
            packets.Any(packet => ReadOpcode(packet) == 0x2731),
            "committed Class Suit mutation sends a full bag refresh");

        var secure = fixture.Transport.CommandResults.Single();
        Check.True(
            secure.Disposition ==
                SecureLegacyCommandDisposition.Applied,
            "secure Tier I result settles as applied");
        Check.Equal(
            (ushort)CommandFamily.ClassSuitExchangeTierI,
            secure.CommandFamily,
            "secure Tier I result uses the Class Suit command family");
        Check.Equal(120u, secure.ResultCode, "secure Tier I result code");
        Check.Equal(
            checked((ulong)InventoryRevision),
            secure.InventoryRevision,
            "secure Tier I result carries committed inventory revision");
        Check.Equal(
            OperationId,
            secure.OperationId,
            "secure Tier I result settles the supplied operation UUID");
    }

    private static async Task CheckChangedSnapshotRetryReplaysAsync()
    {
        await using var fixture = await CreateFixtureAsync();
        var receipt = fixture.Executor.SuccessfulReceipt ??
            throw new InvalidOperationException(
                "Class Suit replay fixture has no durable receipt.");
        fixture.Character.KitBag = KitBagSlots.SetSlot(
            fixture.Character.KitBag,
            GearSlot,
            TierOneWeapon.ToCompactString());
        fixture.Character.KitBag = KitBagSlots.SetSlot(
            fixture.Character.KitBag,
            InsigniaSlot,
            CompactItemEntry.Empty.ToCompactString());
        fixture.Executor.ReplayResult =
            ClassSuitExecutionResult.Duplicate(receipt);

        await InvokeAsync(
            fixture.Handler,
            CreateTierOneMutationPacket(OperationId));

        Check.Equal(
            1,
            fixture.Executor.ReplayCount,
            "post-commit retry checks the durable inbox once");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "post-commit item snapshot changes do not re-execute");
        Check.True(
            fixture.Executor.ReplayIntent == receipt.ReplayIntent,
            "post-commit retry matches the stored stable replay intent");
        Check.True(
            fixture.Transport.CommandResults.Single().Disposition ==
                SecureLegacyCommandDisposition.Replayed,
            "post-commit retry settles as replayed");
    }

    private static async Task CheckPreRouteIntentConflictSettlesAsync()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.Executor.ReplayResult =
            ClassSuitExecutionResult.RequestHashConflict();

        await InvokeAsync(
            fixture.Handler,
            CreateTierOneMutationPacket(
                OperationId,
                npcId: ClassSuitProtocol.AthensNpcId));

        Check.Equal(
            1,
            fixture.Executor.ReplayCount,
            "wrong-route valid mutation compares its stable replay intent");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "pre-route replay conflict cannot mutate inventory");
        Check.Equal(
            checked((int)ClassSuitProtocol.AthensNpcId),
            fixture.Executor.ReplayIntent!.Value.NpcId,
            "pre-route lookup keeps the exact parsed NPC endpoint");
        AssertFunctionResponse(
            fixture.Transport.ReadLegacyPackets().Single(),
            [ClassSuitNativeResults.GenericWrongSelection],
            "pre-route stable-intent conflict",
            ClassSuitProtocol.AthensNpcId);
        Check.True(
            fixture.Transport.CommandResults.Single().Disposition ==
                SecureLegacyCommandDisposition.Conflict,
            "pre-route stable-intent mismatch settles as conflict");
    }

    private static async Task CheckMalformedPreRoutePacketCannotReplayAsync()
    {
        await using var fixture = await CreateFixtureAsync();

        await InvokeAsync(
            fixture.Handler,
            CreateTierOneMutationPacket(
                OperationId,
                arguments => arguments[2] = 7_777,
                ClassSuitProtocol.AthensNpcId));

        Check.Equal(
            0,
            fixture.Executor.ReplayCount,
            "malformed pre-route mutation cannot inspect the inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "malformed pre-route mutation cannot execute");
    }

    private static async Task
        CheckMissingSecureIdentityFailsClosedAsync()
    {
        await using var fixture = await CreateFixtureAsync();

        await InvokeAsync(
            fixture.Handler,
            CreateTierOneMutationPacket(operationId: null));

        Check.Equal(
            0,
            fixture.Executor.ReplayCount,
            "tokenless secure mutation cannot inspect the durable inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "tokenless secure mutation cannot execute");
        AssertFunctionResponse(
            fixture.Transport.ReadLegacyPackets().Single(),
            [ClassSuitNativeResults.GenericWrongSelection],
            "tokenless secure mutation rejection");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "tokenless mutation has no operation UUID to settle");
    }

    private static async Task
        CheckMalformedSecureMutationFailsClosedAsync()
    {
        await using var fixture = await CreateFixtureAsync();

        await InvokeAsync(
            fixture.Handler,
            CreateTierOneMutationPacket(
                OperationId,
                arguments => arguments[2] = 7_777));

        Check.Equal(
            0,
            fixture.Executor.ReplayCount,
            "malformed secure mutation cannot inspect the durable inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "malformed secure mutation cannot execute");
        AssertFunctionResponse(
            fixture.Transport.ReadLegacyPackets().Single(),
            [ClassSuitNativeResults.GenericWrongSelection],
            "malformed secure mutation rejection");

        var secure = fixture.Transport.CommandResults.Single();
        Check.True(
            secure.Disposition ==
                SecureLegacyCommandDisposition.Rejected,
            "malformed secure mutation settles as rejected");
        Check.Equal(
            (ushort)CommandFamily.ClassSuitExchangeTierI,
            secure.CommandFamily,
            "malformed mutation settles the exact command family");
        Check.Equal(
            OperationId,
            secure.OperationId,
            "malformed mutation settles the supplied UUID");
    }

    private static void AssertDialogOpen(
        byte[] packet,
        int expectedDialogIndex,
        string description)
    {
        Check.Equal(48, packet.Length, $"{description} length");
        Check.Equal(
            Opcodes.NpcDialogOpen,
            ReadOpcode(packet),
            $"{description} opcode");
        Check.Equal(
            ClassSuitProtocol.SpartaNpcId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(4, sizeof(uint))),
            $"{description} NPC");
        Check.Equal(
            expectedDialogIndex,
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.AsSpan(12, sizeof(int))),
            $"{description} dialog");
    }

    private static void AssertFunctionResponse(
        byte[] packet,
        IReadOnlyList<int> expectedSubIds,
        string description,
        uint expectedNpcId = ClassSuitProtocol.SpartaNpcId)
    {
        Check.Equal(
            12 + (expectedSubIds.Count * sizeof(int)),
            packet.Length,
            $"{description} length");
        Check.Equal(
            Opcodes.NpcFunctionActionResponse,
            ReadOpcode(packet),
            $"{description} opcode");
        Check.Equal(
            expectedNpcId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(4, sizeof(uint))),
            $"{description} NPC");
        Check.Equal(
            ClassSuitProtocol.DialogIndex,
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.AsSpan(8, sizeof(int))),
            $"{description} dialog");
        for (var index = 0; index < expectedSubIds.Count; index++)
        {
            Check.Equal(
                expectedSubIds[index],
                BinaryPrimitives.ReadInt32LittleEndian(
                    packet.AsSpan(
                        12 + (index * sizeof(int)),
                        sizeof(int))),
                $"{description} sub-ID {index}");
        }
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));
}
