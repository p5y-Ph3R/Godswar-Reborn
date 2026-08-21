using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterLifecycleDurableHandlerChecks
{
    private static async Task CheckCrossRealmReceiptFailsClosedAsync()
    {
        await using var fixture = CreateSecureFixture(
            EmptySnapshot(),
            ActiveSnapshot(),
            CharacterLifecycleExecutionResult.Committed(
                SuccessReceipt(
                    CommandFamily.CharacterCreate,
                    Godswar.Server.Domain.World.Instances.RealmId.Dwargon)));

        await InvokeAsync(
            fixture.Handler,
            CreateRolePacket(CreateOperationId));

        Check.Equal(
            0,
            fixture.SnapshotReader.ReadCount,
            "cross-realm lifecycle receipt cannot refresh projection");
        Check.True(
            fixture.Transport.ReadClearPackets().All(packet =>
                !packet.AsSpan().SequenceEqual(
                    PacketBuilder.CreateRoleSuccess())),
            "cross-realm lifecycle receipt emits no native success");
    }

    private static async Task
        CheckHistoricalSuccessSettlesAgainstCurrentProjectionAsync()
    {
        await using (var createReplay = CreateSecureFixture(
            EmptySnapshot(),
            EmptySnapshot(),
            CharacterLifecycleExecutionResult.Duplicate(
                SuccessReceipt(CommandFamily.CharacterCreate))))
        {
            await InvokeAsync(
                createReplay.Handler,
                CreateRolePacket(CreateOperationId));

            var packets = createReplay.Transport.ReadClearPackets();
            Check.True(
                packets.All(packet =>
                    !packet.AsSpan().SequenceEqual(
                        PacketBuilder.CreateRoleSuccess())),
                "historical create replay sends no stale native success");
            Check.True(
                packets.Any(packet =>
                    packet.AsSpan().SequenceEqual(
                        PacketBuilder.BlankUser())),
                "historical create replay sends current empty projection");
            var result =
                createReplay.Transport.CommandResults.Single();
            Check.True(
                result.Disposition ==
                    SecureLegacyCommandDisposition.Replayed,
                "historical create replay settles its UUID");
            Check.Equal(
                CreateOperationId,
                result.OperationId,
                "historical create replay settles the original UUID");
        }

        var active = ActiveSnapshot();
        var activeCharacter = active.Character ??
            throw new InvalidOperationException(
                "replacement fixture requires a character");
        var replacementId =
            activeCharacter.Identity.CharacterId + 100;
        var replacement = active with
        {
            ProviderSnapshotToken =
                "character-lifecycle-handler-replacement",
            Character = activeCharacter with
            {
                Identity = activeCharacter.Identity with
                {
                    CharacterId = replacementId,
                    LifecycleVersion =
                        activeCharacter.Identity.LifecycleVersion + 2
                },
                CalculatedStats =
                    activeCharacter.CalculatedStats with
                    {
                        CharacterId = replacementId
                    },
                Pets = []
            }
        };
        await using var deleteReplay = CreateSecureFixture(
            replacement,
            replacement,
            CharacterLifecycleExecutionResult.Duplicate(
                SuccessReceipt(CommandFamily.CharacterDelete)));
        await InvokeAsync(
            deleteReplay.Handler,
            DeleteRolePacket(
                DeleteOperationId,
                "untrusted-replacement-account"));

        var deletePackets =
            deleteReplay.Transport.ReadClearPackets();
        Check.True(
            deletePackets.All(packet =>
                !packet.AsSpan().SequenceEqual(
                    PacketBuilder.DeleteRoleSuccess())),
            "historical delete replay sends no stale native success");
        var replacementPreview = PacketBuilder.CharacterPreview(
            CharacterLoadSnapshotHydrator.Hydrate(
                replacement)!.Character);
        Check.True(
            deletePackets.Any(packet =>
                packet.AsSpan().SequenceEqual(
                    replacementPreview)),
            "historical delete replay sends current replacement");
        var deleteResult =
            deleteReplay.Transport.CommandResults.Single();
        Check.True(
            deleteResult.Disposition ==
                SecureLegacyCommandDisposition.Replayed,
            "historical delete replay settles its UUID");
        Check.Equal(
            DeleteOperationId,
            deleteResult.OperationId,
            "historical delete replay settles the original UUID");
    }

    private static async Task CheckInWorldLifecycleFailsClosedAsync()
    {
        foreach (var family in new[]
                 {
                     CommandFamily.CharacterCreate,
                     CommandFamily.CharacterDelete
                 })
        {
            var create =
                family == CommandFamily.CharacterCreate;
            var initial = create
                ? EmptySnapshot()
                : ActiveSnapshot();
            var operationId = create
                ? CreateOperationId
                : DeleteOperationId;
            await using var secure = CreateSecureFixture(
                initial,
                initial,
                CharacterLifecycleExecutionResult.Committed(
                    SuccessReceipt(family)));
            SetField(
                secure.Handler,
                create
                    ? "_clientReadyReceived"
                    : "_registered",
                true);
            await InvokeAsync(
                secure.Handler,
                create
                    ? CreateRolePacket(operationId)
                    : DeleteRolePacket(
                        operationId,
                        "malicious-in-world-account"));

            Check.Equal(
                0,
                secure.Executor.CreateCount +
                    secure.Executor.DeleteCount +
                    secure.Store.CreateCount +
                    secure.Store.DeleteCount,
                $"secure in-world {family} cannot mutate lifecycle");
            Check.Equal(
                0,
                secure.SnapshotReader.ReadCount,
                $"secure in-world {family} cannot refresh state");
            var forbiddenSuccess = create
                ? PacketBuilder.CreateRoleSuccess()
                : PacketBuilder.DeleteRoleSuccess();
            Check.True(
                secure.Transport.ReadClearPackets().All(packet =>
                    !packet.AsSpan().SequenceEqual(
                        forbiddenSuccess)),
                $"secure in-world {family} sends no native success");
            var result = secure.Transport.CommandResults.Single();
            Check.True(
                result.Disposition ==
                    SecureLegacyCommandDisposition.Rejected,
                $"secure in-world {family} settles as rejected");
            Check.Equal(
                (uint)CharacterLifecycleReceiptStatus
                    .InvalidLifecycleState,
                result.ResultCode,
                $"secure in-world {family} reports invalid state");

            await using var legacy = CreateRawFixture(
                initial,
                initial);
            SetField(
                legacy.Handler,
                create
                    ? "_enterUiReadyReceived"
                    : "_registered",
                true);
            await InvokeAsync(
                legacy.Handler,
                create
                    ? CreateRolePacket(operationId: null)
                    : DeleteRolePacket(
                        operationId: null,
                        "malicious-in-world-account"));
            Check.Equal(
                0,
                legacy.Store.CreateCount +
                    legacy.Store.DeleteCount,
                $"legacy in-world {family} cannot mutate lifecycle");
            Check.Equal(
                0,
                legacy.Transport.CommandResults.Count,
                $"legacy in-world {family} invents no settlement");
            Check.True(
                legacy.Transport.ReadClearPackets().All(packet =>
                    !packet.AsSpan().SequenceEqual(
                        forbiddenSuccess)),
                $"legacy in-world {family} sends no native success");
        }
    }

    private static async Task
        CheckMixedRawPostgresProfileFailsClosedAsync()
    {
        foreach (var family in new[]
                 {
                     CommandFamily.CharacterCreate,
                     CommandFamily.CharacterDelete
                 })
        {
            var create =
                family == CommandFamily.CharacterCreate;
            var initial = create
                ? EmptySnapshot()
                : ActiveSnapshot();
            await using var fixture = CreateMixedRawFixture(
                initial,
                initial);

            await InvokeAsync(
                fixture.Handler,
                create
                    ? CreateRolePacket(operationId: null)
                    : DeleteRolePacket(
                        operationId: null,
                        "raw-postgres-account"));

            Check.Equal(
                0,
                fixture.Store.CreateCount +
                    fixture.Store.DeleteCount,
                $"{family} raw/PostgreSQL profile cannot mutate broad store");
            Check.Equal(
                0,
                fixture.Executor.CreateCount +
                    fixture.Executor.DeleteCount,
                $"{family} raw/PostgreSQL profile cannot invent identity");
            var forbiddenSuccess = create
                ? PacketBuilder.CreateRoleSuccess()
                : PacketBuilder.DeleteRoleSuccess();
            Check.True(
                fixture.Transport.ReadClearPackets().All(packet =>
                    !packet.AsSpan().SequenceEqual(
                        forbiddenSuccess)),
                $"{family} raw/PostgreSQL profile sends no false success");
        }
    }
}
