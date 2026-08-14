using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetBasicSavvyResetHandlerChecks
{
    public static async Task RunAsync()
    {
        await CheckSecureCommitAsync();
        await CheckLegacyOkIsHarmlessAsync();
        await CheckLegacyAcceptedReplayIsHarmlessAsync();
        await CheckLegacyPreviewReplayIsHarmlessAsync();
        await CheckStaleReplaySuppressionAsync();
        await CheckReplayAfterPetSwitchSettlesAsync();
        await CheckNativeFailuresAsync();
        await CheckMalformedFrameAsync();
        await CheckDuplicateIdentityAsync();
    }

    private static async Task CheckSecureCommitAsync()
    {
        var operationId = Guid.Parse(
            "d0ee1f10-884b-43ae-b4be-768349bf05a1");
        var initial = CharacterWithFairyFeather(stack: 2);
        var persisted = CharacterWithFairyFeather(stack: 1);
        var pet = ApplyPreviewValues(CreatePet());
        var executor = new PetBasicSavvyPreviewTestExecutor
        {
            ResetBasicSavvy = envelope =>
                PetDurableExecutionResult.Committed(
                    Receipt(
                        envelope,
                        PetDurableReceiptStatus.PetBasicSavvyAccepted,
                        pet))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            persisted,
            [pet],
            executor);

        await InvokeAsync(fixture.Handler, CreateResetPacket(operationId));

        var packets = fixture.Transport.ReadLegacyPackets();
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            executor.ResetBasicSavvyCount == 1 &&
            executor.ResetBasicSavvyEnvelope is { } envelope &&
            envelope.Family == CommandFamily.PetBasicSavvyReset &&
            envelope.Command.Identity.IsSecureClient &&
            envelope.Command.Identity.OperationId == operationId &&
            envelope.Command.Operation ==
                PetBasicSavvyResetOperation.Preview &&
            envelope.Command.PreviewOperationId == Guid.Empty,
            "exact secure Fairy reset reaches family 51 once");
        Check.Equal(
            1,
            packets.Count(packet => packet.SequenceEqual(PreviewPage())),
            "secure Fairy reset renders exactly one native page 120");
        var bagDetails = PacketBuilder.KitBagDetailPages(persisted);
        Check.True(
            bagDetails.All(expected =>
                packets.Any(packet => packet.SequenceEqual(expected))),
            "Fairy receipt projects the authoritative consumed stack");
        Check.True(
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.PetLevelUpgrade &&
                packet.Length == 68) == 1 &&
            HasNoPresenceProjection(packets) &&
            executor.TransitionCount == 0,
            "Fairy reset emits one 68-byte 10286 and no pet presence");
        Check.True(
            executor.CurrentChecks == 0 &&
            result.Disposition == SecureLegacyCommandDisposition.Applied &&
            result.CommandFamily ==
                (ushort)CommandFamily.PetBasicSavvyReset &&
            result.ResultCode ==
                PetManagerProtocol.BasicSavvyResetSucceededResultSubId &&
            result.OperationId == operationId,
            "one-phase Fairy reset settles its secure UUID without preview state");
    }

    private static async Task CheckLegacyOkIsHarmlessAsync()
    {
        var acceptOperationId = Guid.Parse(
            "98bdad39-cafb-4b7e-a11d-0d2e6e86aa6a");
        var character = CharacterWithFairyFeather(stack: 2);
        var pet = CreatePet();
        var executor = new PetBasicSavvyPreviewTestExecutor
        {
            ResetBasicSavvy = envelope =>
                PetDurableExecutionResult.Rejected(
                    Receipt(
                        envelope,
                        PetDurableReceiptStatus
                            .PetBasicSavvyPreviewUnavailable,
                        pet))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [pet],
            executor);

        await InvokeAsync(fixture.Handler, CreateResetPacket(
            acceptOperationId,
            accept: true));

        Check.True(
            executor.ResetBasicSavvyEnvelope is { } accept &&
            accept.Command.Operation ==
                PetBasicSavvyResetOperation.Accept &&
            accept.Command.PreviewOperationId == Guid.Empty,
            "retired Fairy OK reaches only a non-mutating compatibility path");
        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            1,
            packets.Count,
            "retired Fairy OK emits exactly one terminal page");
        Check.True(
            packets.Single().SequenceEqual(TerminalPage(
                PetManagerProtocol
                    .BasicSavvyResetPreviewUnavailableResultSubId)) &&
            HasNoPresenceProjection(packets) &&
            executor.TransitionCount == 0,
            "retired Fairy OK returns page 129 without pet mutation");
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == SecureLegacyCommandDisposition.Rejected &&
            result.ResultCode == PetManagerProtocol
                .BasicSavvyResetPreviewUnavailableResultSubId &&
            result.OperationId == acceptOperationId,
            "retired Fairy OK settles without disconnecting the client");
    }

    private static async Task CheckLegacyAcceptedReplayIsHarmlessAsync()
    {
        var operationId = Guid.Parse(
            "84c0ce59-15b0-446c-92dd-05ded3fda3e3");
        var pet = ApplyPreviewValues(CreatePet());
        var character = CharacterWithFairyFeather(stack: 1);
        var executor = new PetBasicSavvyPreviewTestExecutor
        {
            ResetBasicSavvy = envelope =>
                PetDurableExecutionResult.Duplicate(
                    Receipt(
                        envelope with
                        {
                            Command = envelope.Command with
                            {
                                Operation =
                                    PetBasicSavvyResetOperation.Accept
                            }
                        },
                        PetDurableReceiptStatus.PetBasicSavvyAccepted,
                        pet))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [pet],
            executor);

        await InvokeAsync(fixture.Handler, CreateResetPacket(operationId));

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(
            packets.Any(packet => packet.Length == 68 &&
                ReadOpcode(packet) == Opcodes.PetLevelUpgrade) &&
            packets.Any(packet => packet.SequenceEqual(TerminalPage(
                PetManagerProtocol
                    .BasicSavvyResetPreviewUnavailableResultSubId))),
            "legacy Accepted replay refreshes authoritative stats then returns page 129");
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == SecureLegacyCommandDisposition.Replayed &&
            result.ResultCode == PetManagerProtocol
                .BasicSavvyResetPreviewUnavailableResultSubId,
            "legacy Accepted v2 receipt cannot throw or reopen stale values");
    }
}
