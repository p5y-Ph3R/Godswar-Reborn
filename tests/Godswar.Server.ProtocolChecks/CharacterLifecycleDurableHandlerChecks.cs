using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterLifecycleDurableHandlerChecks
{
    public const string CheckName =
        "Durable character lifecycle handler and replay";

    public static async Task RunAsync()
    {
        await CheckSecureSuccessAsync(
            CommandFamily.CharacterCreate,
            duplicate: false);
        await CheckSecureSuccessAsync(
            CommandFamily.CharacterCreate,
            duplicate: true);
        await CheckSecureSuccessAsync(
            CommandFamily.CharacterDelete,
            duplicate: false);
        await CheckSecureSuccessAsync(
            CommandFamily.CharacterDelete,
            duplicate: true);
        await CheckTerminalFailureAsync(
            CommandFamily.CharacterCreate,
            requestConflict: true);
        await CheckTerminalFailureAsync(
            CommandFamily.CharacterCreate,
            requestConflict: false);
        await CheckTerminalFailureAsync(
            CommandFamily.CharacterDelete,
            requestConflict: true);
        await CheckTerminalFailureAsync(
            CommandFamily.CharacterDelete,
            requestConflict: false);
        await CheckHistoricalSuccessSettlesAgainstCurrentProjectionAsync();
        await CheckCrossRealmReceiptFailsClosedAsync();
        await CheckSecureMissingIdentityFailsClosedAsync();
        await CheckInWorldLifecycleFailsClosedAsync();
        await CheckMixedRawPostgresProfileFailsClosedAsync();
        await CheckRawLegacyCompatibilityAsync();
    }

    private static async Task CheckSecureSuccessAsync(
        CommandFamily family,
        bool duplicate)
    {
        var create = family == CommandFamily.CharacterCreate;
        var initial = create ? EmptySnapshot() : ActiveSnapshot();
        var projected = create ? ActiveSnapshot() : EmptySnapshot();
        var receipt = SuccessReceipt(family);
        var terminal = duplicate
            ? CharacterLifecycleExecutionResult.Duplicate(receipt)
            : CharacterLifecycleExecutionResult.Committed(receipt);
        await using var fixture = CreateSecureFixture(
            initial,
            projected,
            terminal);
        var operationId =
            create ? CreateOperationId : DeleteOperationId;

        await InvokeAsync(
            fixture.Handler,
            create
                ? CreateRolePacket(operationId)
                : DeleteRolePacket(
                    operationId,
                    "forged-client-account"));

        Check.Equal(
            1,
            create
                ? fixture.Executor.CreateCount
                : fixture.Executor.DeleteCount,
            $"{family} reaches its aggregate executor once");
        Check.Equal(
            0,
            fixture.Store.CreateCount + fixture.Store.DeleteCount,
            $"{family} secure path bypasses broad legacy store mutation");
        Check.Equal(
            1,
            fixture.SnapshotReader.ReadCount,
            $"{family} success reloads its authoritative projection");
        AssertCapturedIntent(fixture, family, operationId);

        var packets = fixture.Transport.ReadClearPackets();
        var nativeSuccess = create
            ? PacketBuilder.CreateRoleSuccess()
            : PacketBuilder.DeleteRoleSuccess();
        Check.True(
            packets.Any(packet =>
                packet.AsSpan().SequenceEqual(nativeSuccess)),
            $"{family} sends the stock native success");
        var preview = create
            ? PacketBuilder.CharacterPreview(
                CharacterLoadSnapshotHydrator
                    .Hydrate(projected)!.Character)
            : PacketBuilder.BlankUser();
        Check.True(
            packets.Any(packet =>
                packet.AsSpan().SequenceEqual(preview)),
            $"{family} sends the refreshed character preview");

        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition ==
                (duplicate
                    ? SecureLegacyCommandDisposition.Replayed
                    : SecureLegacyCommandDisposition.Applied),
            $"{family} reports the durable disposition");
        Check.Equal(
            (ushort)family,
            result.CommandFamily,
            $"{family} reports its secure family");
        Check.Equal(
            checked((uint)receipt.Status),
            result.ResultCode,
            $"{family} reports its durable status");
        Check.Equal(
            checked((ulong)receipt.LifecycleVersion),
            result.AuthoritativeRevision,
            $"{family} reports its lifecycle revision");
        Check.Equal(
            operationId,
            result.OperationId,
            $"{family} settles the supplied operation UUID");
        Check.Equal(
            "command-result",
            fixture.Transport.Events.Last(),
            $"{family} settles after stock-client projection");
    }

    private static async Task CheckTerminalFailureAsync(
        CommandFamily family,
        bool requestConflict)
    {
        var create = family == CommandFamily.CharacterCreate;
        var initial = create ? EmptySnapshot() : ActiveSnapshot();
        var receipt = RejectionReceipt(family);
        var rejection =
            CharacterLifecycleExecutionResult.TerminalRejected(receipt);
        await using var fixture = CreateSecureFixture(
            initial,
            initial,
            requestConflict
                ? CharacterLifecycleExecutionResult
                    .RequestHashConflict()
                : rejection);
        var operationId =
            create ? CreateOperationId : DeleteOperationId;

        await InvokeAsync(
            fixture.Handler,
            create
                ? CreateRolePacket(operationId)
                : DeleteRolePacket(
                    operationId,
                    "untrusted-username"));

        var packets = fixture.Transport.ReadClearPackets();
        var forbiddenSuccess = create
            ? PacketBuilder.CreateRoleSuccess()
            : PacketBuilder.DeleteRoleSuccess();
        Check.True(
            packets.All(packet =>
                !packet.AsSpan().SequenceEqual(forbiddenSuccess)),
            $"{family} rejection sends no false native success");
        Check.Equal(
            0,
            fixture.Store.CreateCount + fixture.Store.DeleteCount,
            $"{family} rejection cannot downgrade to legacy mutation");
        Check.Equal(
            1,
            create
                ? fixture.Executor.CreateCount
                : fixture.Executor.DeleteCount,
            $"{family} executes exactly once");

        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition ==
                (requestConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected),
            $"{family} rejection reports its secure disposition");
        Check.Equal(
            (ushort)family,
            result.CommandFamily,
            $"{family} rejection reports its family");
        Check.Equal(
            requestConflict
                ? 0U
                : checked((uint)receipt.Status),
            result.ResultCode,
            $"{family} rejection reports its status");
        Check.Equal(
            operationId,
            result.OperationId,
            $"{family} rejection settles the supplied UUID");
    }

    private static async Task
        CheckSecureMissingIdentityFailsClosedAsync()
    {
        foreach (var family in new[]
                 {
                     CommandFamily.CharacterCreate,
                     CommandFamily.CharacterDelete
                 })
        {
            var create = family ==
                CommandFamily.CharacterCreate;
            var initial = create
                ? EmptySnapshot()
                : ActiveSnapshot();
            await using var fixture = CreateSecureFixture(
                initial,
                initial,
                CharacterLifecycleExecutionResult.InvalidIntent());

            await InvokeAsync(
                fixture.Handler,
                create
                    ? CreateRolePacket(operationId: null)
                    : DeleteRolePacket(
                        operationId: null,
                        "untrusted-username"));

            var packets = fixture.Transport.ReadClearPackets();
            var forbiddenSuccess = create
                ? PacketBuilder.CreateRoleSuccess()
                : PacketBuilder.DeleteRoleSuccess();
            Check.True(
                packets.All(packet =>
                    !packet.AsSpan().SequenceEqual(forbiddenSuccess)),
                $"{family} without UUID sends no native success");
            Check.Equal(
                0,
                fixture.Executor.CreateCount +
                    fixture.Executor.DeleteCount,
                $"{family} without UUID cannot reach durable executor");
            Check.Equal(
                0,
                fixture.Store.CreateCount +
                    fixture.Store.DeleteCount,
                $"{family} without UUID cannot downgrade");
            Check.Equal(
                0,
                fixture.Transport.CommandResults.Count,
                $"{family} without UUID cannot settle another operation");
        }
    }

    private static async Task CheckRawLegacyCompatibilityAsync()
    {
        var active = ActiveSnapshot();
        await using var fixture = CreateRawFixture(
            EmptySnapshot(),
            active,
            EmptySnapshot());

        await InvokeAsync(
            fixture.Handler,
            CreateRolePacket(operationId: null));
        await InvokeAsync(
            fixture.Handler,
            DeleteRolePacket(
                operationId: null,
                "legacy-untrusted-account"));

        Check.Equal(
            1,
            fixture.Store.CreateCount,
            "raw legacy CreateRole retains compatibility mutation");
        Check.Equal(
            1,
            fixture.Store.DeleteCount,
            "raw legacy DeleteRole retains compatibility mutation");
        Check.Equal(
            0,
            fixture.Executor.CreateCount +
                fixture.Executor.DeleteCount,
            "raw legacy lifecycle does not invent operation identity");
        var packets = fixture.Transport.ReadClearPackets();
        Check.True(
            packets.Any(packet =>
                packet.AsSpan().SequenceEqual(
                    PacketBuilder.CreateRoleSuccess())),
            "raw legacy CreateRole receives native success");
        Check.True(
            packets.Any(packet =>
                packet.AsSpan().SequenceEqual(
                    PacketBuilder.DeleteRoleSuccess())),
            "raw legacy DeleteRole receives native success");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "raw legacy lifecycle emits no secure settlement");
    }

    private static void AssertCapturedIntent(
        LifecycleFixture fixture,
        CommandFamily family,
        Guid operationId)
    {
        if (family == CommandFamily.CharacterCreate)
        {
            var envelope = fixture.Executor.CreateEnvelope ??
                throw new InvalidOperationException(
                    "CreateRole envelope was not captured.");
            Check.Equal(
                operationId,
                envelope.Command.ClientOperationId,
                "CreateRole preserves operation UUID");
            Check.Equal(
                AccountId,
                envelope.Subject.AccountId,
                "CreateRole uses authenticated account");
            Check.Equal(
                CharacterName,
                envelope.Command.Name,
                "CreateRole preserves character name");
            return;
        }

        var deleteEnvelope = fixture.Executor.DeleteEnvelope ??
            throw new InvalidOperationException(
                "DeleteRole envelope was not captured.");
        var identity = ActiveSnapshot().Character?.Identity ??
            throw new InvalidOperationException(
                "DeleteRole intent fixture has no identity.");
        Check.Equal(
            operationId,
            deleteEnvelope.Command.ClientOperationId,
            "DeleteRole preserves operation UUID");
        Check.Equal(
            AccountId,
            deleteEnvelope.Subject.AccountId,
            "DeleteRole uses authenticated account");
        Check.Equal(
            CharacterName,
            deleteEnvelope.Command.Name,
            "DeleteRole ignores client account-name field");
        Check.True(
            deleteEnvelope.Command.ExpectedActiveCharacterId ==
                identity.CharacterId,
            "DeleteRole captures active character precondition");
        Check.True(
            deleteEnvelope.Command.ExpectedLifecycleVersion ==
                identity.LifecycleVersion,
            "DeleteRole captures lifecycle-version precondition");
    }
}
