using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetBasicSavvyResetHandlerChecks
{
    private static async Task CheckStaleReplaySuppressionAsync()
    {
        var operationId = Guid.Parse(
            "36399fb8-928e-440e-bfff-65fbb9f5a561");
        var initial = CharacterWithFairyFeather(stack: 2);
        var persisted = CharacterWithFairyFeather(stack: 1);
        var committedPet = ApplyPreviewValues(CreatePet());
        var currentPet = ApplyValues(committedPet, LaterValues);
        var executor = new PetBasicSavvyPreviewTestExecutor
        {
            ResetBasicSavvy = envelope =>
                PetDurableExecutionResult.Duplicate(
                    Receipt(
                        envelope,
                        PetDurableReceiptStatus.PetBasicSavvyAccepted,
                        committedPet))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            persisted,
            [currentPet],
            executor);

        await InvokeAsync(fixture.Handler, CreateResetPacket(operationId));

        var packets = fixture.Transport.ReadLegacyPackets();
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            executor.ResetBasicSavvyCount == 1 &&
            executor.CurrentChecks == 0 &&
            result.Disposition == SecureLegacyCommandDisposition.Replayed &&
            result.CommandFamily ==
                (ushort)CommandFamily.PetBasicSavvyReset &&
            result.OperationId == operationId,
            "delayed Fairy replay settles its duplicate secure identity");
        Check.True(
            packets.Count(packet => packet.SequenceEqual(
                ResultPage(LaterValues))) == 1 &&
            packets.All(packet => !packet.SequenceEqual(PreviewPage())) &&
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.PetLevelUpgrade &&
                packet.Length == 68) == 1 &&
            HasNoPresenceProjection(packets) &&
            executor.TransitionCount == 0,
            "delayed Fairy replay displays current DB Savvy, never its stale receipt roll");
    }

    private static async Task CheckLegacyPreviewReplayIsHarmlessAsync()
    {
        var operationId = Guid.Parse(
            "a1c54cf4-53bb-4e34-adfb-2cfa9f5a136a");
        var character = CharacterWithFairyFeather(stack: 1);
        var pet = ApplyPreviewValues(CreatePet());
        var executor = new PetBasicSavvyPreviewTestExecutor
        {
            ResetBasicSavvy = envelope =>
                PetDurableExecutionResult.Duplicate(
                    Receipt(
                        envelope,
                        PetDurableReceiptStatus.PetBasicSavvyPreviewed,
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
            packets.Any(packet => packet.SequenceEqual(TerminalPage(
                PetManagerProtocol
                    .BasicSavvyResetPreviewUnavailableResultSubId))) &&
            packets.All(packet => !packet.SequenceEqual(PreviewPage())) &&
            HasNoPresenceProjection(packets),
            "retired paid Preview replay cannot present an uncommitted roll");
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == SecureLegacyCommandDisposition.Replayed &&
            result.ResultCode == PetManagerProtocol
                .BasicSavvyResetPreviewUnavailableResultSubId,
            "retired paid Preview receipt maps to harmless terminal 129");
    }

    private static async Task CheckReplayAfterPetSwitchSettlesAsync()
    {
        var operationId = Guid.Parse(
            "5c240107-4040-4b24-bd74-95f3cdb8f0cb");
        var committedPet = ApplyPreviewValues(CreatePet());
        var switchedPet = CreatePet(revision: committedPet.Revision + 1) with
        {
            PetId = committedPet.PetId + 1,
            IsCarried = true,
            IsSummoned = true
        };
        var character = CharacterWithFairyFeather(stack: 1);
        var executor = new PetBasicSavvyPreviewTestExecutor
        {
            ResetBasicSavvy = envelope =>
                PetDurableExecutionResult.Duplicate(
                    Receipt(
                        envelope,
                        PetDurableReceiptStatus.PetBasicSavvyAccepted,
                        committedPet))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [switchedPet],
            executor);

        await InvokeAsync(fixture.Handler, CreateResetPacket(operationId));

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(
            packets.Any(packet => packet.SequenceEqual(TerminalPage(
                PetManagerProtocol
                    .BasicSavvyResetPreviewUnavailableResultSubId))) &&
            packets.All(packet =>
                ReadOpcode(packet) != Opcodes.PetLevelUpgrade) &&
            HasNoPresenceProjection(packets),
            "delayed Reset after a pet switch settles without stale projection");
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == SecureLegacyCommandDisposition.Replayed &&
            result.OperationId == operationId,
            "pet-switch replay always settles the secure operation identity");
    }

    private static async Task CheckNativeFailuresAsync()
    {
        (PetDurableReceiptStatus Status, int ResultSubId, bool Accept)[] cases =
        [
            (
                PetDurableReceiptStatus.FairyFeatherNotFound,
                PetManagerProtocol.BasicSavvyResetMissingFeatherResultSubId,
                false),
            (
                PetDurableReceiptStatus.PetNotTaken,
                PetManagerProtocol.BasicSavvyResetNoPetResultSubId,
                false),
            (
                PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable,
                PetManagerProtocol
                    .BasicSavvyResetPreviewUnavailableResultSubId,
                true)
        ];

        foreach (var (status, resultSubId, accept) in cases)
        {
            var operationId = Guid.NewGuid();
            var character = CharacterWithFairyFeather(stack: 1);
            var pet = CreatePet();
            var executor = new PetBasicSavvyPreviewTestExecutor
            {
                ResetBasicSavvy = envelope =>
                    PetDurableExecutionResult.Rejected(
                        Receipt(envelope, status, pet))
            };
            await using var fixture = PetDurableHandlerFixture.Create(
                character,
                character,
                [pet],
                executor);

            await InvokeAsync(fixture.Handler, CreateResetPacket(
                operationId,
                accept));

            var packets = fixture.Transport.ReadLegacyPackets();
            var result = fixture.Transport.CommandResults.Single();
            if (status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable)
            {
                Check.True(
                    executor.ResetBasicSavvyEnvelope is { } envelope &&
                    envelope.Command.Operation ==
                        PetBasicSavvyResetOperation.Accept &&
                    envelope.Command.PreviewOperationId == Guid.Empty,
                    "OK without an active Fairy preview reaches the executor with an empty preview binding");
            }
            Check.True(
                executor.ResetBasicSavvyCount == 1 &&
                packets is [var terminal] &&
                terminal.SequenceEqual(TerminalPage(resultSubId)),
                $"Fairy {status} emits only native terminal {resultSubId}");
            Check.True(
                result.Disposition ==
                    SecureLegacyCommandDisposition.Rejected &&
                result.CommandFamily ==
                    (ushort)CommandFamily.PetBasicSavvyReset &&
                result.ResultCode == (uint)resultSubId &&
                result.OperationId == operationId,
                $"Fairy {status} settles the exact rejected operation");
        }
    }

    private static async Task CheckMalformedFrameAsync()
    {
        var character = CharacterWithFairyFeather(stack: 1);
        var pet = CreatePet();
        var executor = new PetBasicSavvyPreviewTestExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [pet],
            executor);

        await InvokeAsync(fixture.Handler, CreateResetPacket(
            Guid.Parse("7b42d5f3-aa04-46d4-b00b-d95004207a9c"),
            corruptPaddingIndex: 2));

        Check.Equal(0, executor.ResetBasicSavvyCount,
            "malformed Fairy frame never reaches its executor");
        Check.Equal(0, fixture.Transport.CommandResults.Count,
            "malformed Fairy frame settles no unrelated secure UUID");
        Check.Equal(0, fixture.Transport.ReadLegacyPackets().Count,
            "malformed Fairy frame emits no misleading native result");
    }

    private static async Task CheckDuplicateIdentityAsync()
    {
        var operationId = Guid.Parse(
            "3ec79fe1-dfc7-459f-9113-e7772913614f");
        var initial = CharacterWithFairyFeather(stack: 2);
        var persisted = CharacterWithFairyFeather(stack: 1);
        var pet = ApplyPreviewValues(CreatePet());
        var observed = new List<
            CommandEnvelope<PetBasicSavvyResetCommand>>();
        var executor = new PetBasicSavvyPreviewTestExecutor
        {
            ResetBasicSavvy = envelope =>
            {
                observed.Add(envelope);
                var receipt = Receipt(
                    envelope,
                    PetDurableReceiptStatus.PetBasicSavvyAccepted,
                    pet);
                return observed.Count == 1
                    ? PetDurableExecutionResult.Committed(receipt)
                    : PetDurableExecutionResult.Duplicate(receipt);
            }
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            persisted,
            [pet],
            executor);

        await InvokeAsync(fixture.Handler, CreateResetPacket(operationId));
        await InvokeAsync(fixture.Handler, CreateResetPacket(operationId));

        var results = fixture.Transport.CommandResults;
        Check.True(
            observed.Count == 2 &&
            observed.All(envelope =>
                envelope.Command.Identity.OperationId == operationId) &&
            results.Count == 2 &&
            results[0].Disposition == SecureLegacyCommandDisposition.Applied &&
            results[1].Disposition ==
                SecureLegacyCommandDisposition.Replayed &&
            results.All(result =>
                result.OperationId == operationId &&
                result.CommandFamily ==
                    (ushort)CommandFamily.PetBasicSavvyReset),
            "duplicate Fairy request preserves one durable operation identity");
        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(
            packets.Count(packet => packet.SequenceEqual(PreviewPage())) == 2 &&
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.PetLevelUpgrade &&
                packet.Length == 68) == 2 &&
            HasNoPresenceProjection(packets),
            "duplicate Fairy identity replays current page 120 without a second durable mutation");
    }
}
