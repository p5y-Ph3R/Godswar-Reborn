using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetManagerSkillUnlearnHandlerChecks
{
    private const int PhoenixFeatherSlot = 26;

    private static async Task CheckGrowthResetHandlerOrderingAsync()
    {
        await CheckGrowthResetNavigationBeforeMutationAsync();
        await CheckGrowthResetAcceptAsync();
        await CheckDelayedGrowthResetReplayAfterPetSwitchAsync();
        await CheckStaleGrowthResetComparisonRevisionAsync();
        await CheckGrowthResetNativeFailuresAsync();
        await CheckMalformedGrowthResetFailsClosedAsync();
    }

    private static async Task CheckGrowthResetAcceptAsync()
    {
        var initialCharacter = CharacterWithPhoenixFeather(stack: 2);
        var updatedCharacter = CharacterWithPhoenixFeather(stack: 1);
        var acceptedPet = CreatePet([]) with
        {
            GrowthRevealed = true,
            Revision = 9
        };
        var envelopes = new List<CommandEnvelope<PetGrowthResetCommand>>();
        var executor = new PetGrowthPreviewTestExecutor
        {
            ResetGrowth = envelope =>
            {
                envelopes.Add(envelope);
                return PetDurableExecutionResult.Committed(
                    GrowthResetReceipt(
                        envelope,
                        envelope.Command.Operation ==
                            PetGrowthResetOperation.Preview
                            ? PetDurableReceiptStatus.PetGrowthPreviewed
                            : PetDurableReceiptStatus.PetGrowthAccepted,
                        acceptedPet,
                        succeeded: true));
            }
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initialCharacter,
            updatedCharacter,
            [acceptedPet],
            executor,
            hasLocalDevelopmentCapability: true);

        var resetArguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        resetArguments[0] = PetManagerProtocol.GrowthResetActionSubId;
        await InvokeAsync(
            fixture.Handler,
            DecodeExactRequest(CreateActionPacket(
                PetManagerProtocol.GrowthResetMenuSubId,
                resetArguments,
                dialogIndex: PetManagerProtocol.PointResetDialogIndex)));

        var acceptArguments = (int[])resetArguments.Clone();
        acceptArguments[1] = 0;
        await InvokeAsync(
            fixture.Handler,
            DecodeExactRequest(CreateActionPacket(
                PetManagerProtocol.GrowthResetMenuSubId,
                acceptArguments,
                dialogIndex: PetManagerProtocol.PointResetDialogIndex)));

        Check.True(
            envelopes is [var preview, var accept] &&
            preview.Command.Operation == PetGrowthResetOperation.Preview &&
            accept.Command.Operation == PetGrowthResetOperation.Accept &&
            accept.Command.PreviewOperationId ==
                preview.Command.Identity.OperationId,
            "stock A1 OK accepts exactly the current durable preview");
        var packets = fixture.ReadLegacyPackets();
        Check.Equal(
            1,
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.PetLevelUpgrade),
            "only OK emits the authoritative in-place pet-stat refresh");
        Check.Equal(
            1,
            packets.Count(packet =>
                ReadOpcode(packet) ==
                    Opcodes.NpcFunctionActionResponse),
            "Reset shows page 130 while OK closes without reopening it");
    }

    private static async Task CheckGrowthResetNavigationBeforeMutationAsync()
    {
        var initialCharacter = CharacterWithPhoenixFeather(stack: 2);
        var updatedCharacter = CharacterWithPhoenixFeather(stack: 1);
        var updatedPet = CreatePet([]) with { Revision = 8 };
        updatedPet = updatedPet with
        {
            CompletedRebirths = 5,
            StatValues = updatedPet.StatValues
                .Select(static (stat, index) => stat with
                {
                    GrowthAcceleration = (index + 1) / 10m
                })
                .ToArray()
        };
        var executor = new PetGrowthPreviewTestExecutor
        {
            ResetGrowth = envelope =>
                PetDurableExecutionResult.Committed(
                    GrowthResetReceipt(
                        envelope,
                        PetDurableReceiptStatus.PetGrowthPreviewed,
                        updatedPet,
                        succeeded: true))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initialCharacter,
            updatedCharacter,
            [updatedPet],
            executor,
            hasLocalDevelopmentCapability: true);

        await InvokeAsync(
            fixture.Handler,
            DecodeExactRequest(CreateActionPacket(
                PetManagerProtocol.GrowthResetMenuSubId,
                dialogIndex: PetManagerProtocol.PointResetDialogIndex)));

        var nestedArguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        nestedArguments[0] = PetManagerProtocol.GrowthResetActionSubId;
        await InvokeAsync(
            fixture.Handler,
            DecodeExactRequest(CreateActionPacket(
                PetManagerProtocol.GrowthResetMenuSubId,
                nestedArguments,
                dialogIndex: PetManagerProtocol.PointResetDialogIndex)));

        var packets = fixture.ReadLegacyPackets();
        var navigation = PacketBuilder.NpcFunctionActionResponse(
            PetManagerProtocol.AthensNpcId,
            PetManagerProtocol.PointResetDialogIndex,
            PetManagerProtocol.GrowthResetDescriptionSubId,
            PetManagerProtocol.GrowthResetActionSubId);
        var rates = updatedPet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .Select(static stat =>
                stat.BaseGrowthRate + stat.GrowthAcceleration)
            .ToArray();
        var baseRates = updatedPet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .Select(static stat => stat.BaseGrowthRate)
            .ToArray();
        var rolledRates = RolledGrowthRates(baseRates)
            .Select(static (rate, index) =>
                rate + .50m + (index / 10m))
            .ToArray();
        var success = PacketBuilder.NpcFunctionActionResponse(
            PetManagerProtocol.AthensNpcId,
            PetManagerProtocol.PointResetDialogIndex,
            PetManagerProtocol.BuildGrowthResetSuccessPage(
                rolledRates,
                rates));

        Check.Equal(1, executor.ResetGrowthCount,
            "Growth navigation is read-only and action 117 mutates once");
        Check.True(
            executor.ResetGrowthEnvelope is { } envelope &&
            envelope.Family == CommandFamily.PetGrowthReset &&
            envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.OperationId != Guid.Empty &&
            envelope.Command.Operation == PetGrowthResetOperation.Preview &&
            envelope.Command.PreviewOperationId == Guid.Empty &&
            envelope.Command.Identity.RawLocalConnectionId ==
                envelope.Connection.ConnectionId,
            "Phoenix action receives one durable connection-scoped UUID");
        Check.True(
            packets.Count >= 3 &&
            packets[0].SequenceEqual(navigation) &&
            packets[^1].SequenceEqual(success),
            "Growth page [112,117] precedes durable mutation and native page 130 terminates it");
        Check.Equal(
            0,
            packets.Count(packet =>
                ReadOpcode(packet) == 10_237),
            "successful Phoenix reset never rebuilds live pet selection");
        var progressionRefreshes = packets
            .Where(packet =>
                ReadOpcode(packet) == Opcodes.PetLevelUpgrade)
            .ToArray();
        Check.Equal(0, progressionRefreshes.Length,
            "Phoenix Reset preview does not mutate or refresh pet stats");
        Check.Equal(
            0,
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.StorageItem),
            "remaining Phoenix stack is refreshed without deleting its slot");

        Check.Equal(1, executor.CurrentChecks,
            "page 130 is shown only for the current durable preview");
        Check.Equal(0, executor.TransitionCount,
            "Growth OK/accept never changes durable presence");
    }

    private static async Task
        CheckDelayedGrowthResetReplayAfterPetSwitchAsync()
    {
        var operationId = Guid.Parse(
            "a8c29641-d02c-4cbf-9422-3807f4fe6df4");
        var character = CharacterWithPhoenixFeather(stack: 1);
        var historicalPet = CreatePet([]) with { Revision = 8 };
        var recalledPet = historicalPet with
        {
            IsCarried = false,
            IsSummoned = false,
            Revision = 9
        };
        var replacementPet = CreatePet([]) with
        {
            PetId = historicalPet.PetId + 1,
            Name = "current-companion",
            IsCarried = true,
            IsSummoned = true,
            Revision = 10
        };
        var executor = new PetGrowthPreviewTestExecutor
        {
            PreviewIsCurrent = false,
            ResetGrowth = envelope =>
                PetDurableExecutionResult.Duplicate(
                    GrowthResetReceipt(
                        envelope,
                        PetDurableReceiptStatus.PetGrowthPreviewed,
                        historicalPet,
                        succeeded: true))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [recalledPet, replacementPet],
            executor);
        var request = CreateActionPacket(
            PetManagerProtocol.GrowthResetActionSubId,
            dialogIndex: PetManagerProtocol.PointResetDialogIndex);

        await InvokeAsync(
            fixture.Handler,
            DecodeExactRequest(
                new GamePacket(request.Buffer, operationId)));

        var packets = fixture.Transport.ReadLegacyPackets();
        var secureResult = fixture.Transport.CommandResults.Single();
        Check.True(
            executor.ResetGrowthCount == 1 &&
            executor.ResetGrowthEnvelope?.Command.Identity.OperationId ==
                operationId &&
            secureResult.Disposition ==
                SecureLegacyCommandDisposition.Replayed &&
            secureResult.OperationId == operationId,
            "delayed Growth replay settles its secure operation UUID");
        Check.True(
            packets.All(packet => ReadOpcode(packet) !=
                Opcodes.NpcFunctionActionResponse) &&
            packets.All(packet => ReadOpcode(packet) != 10_237) &&
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.PetLevelUpgrade) == 0 &&
            executor.CurrentChecks == 1,
            "a delayed overwritten preview cannot redisplay stale rates");
        Check.Equal(0, executor.TransitionCount,
            "delayed Growth replay never recalls or reselects either pet");
    }

    private static async Task CheckGrowthResetNativeFailuresAsync()
    {
        (PetDurableReceiptStatus Status, int ResultSubId)[] cases =
        [
            (
                PetDurableReceiptStatus.PhoenixFeatherNotFound,
                PetManagerProtocol.GrowthResetMissingFeatherResultSubId),
            (
                PetDurableReceiptStatus.PetNotTaken,
                PetManagerProtocol.GrowthResetNoPetResultSubId)
        ];
        foreach (var (status, resultSubId) in cases)
        {
            var character = CharacterWithPhoenixFeather(stack: 1);
            var pet = CreatePet([]);
            var executor = new DelegatingPetDurableCommandExecutor
            {
                ResetGrowth = envelope =>
                    PetDurableExecutionResult.Rejected(
                        GrowthResetReceipt(
                            envelope,
                            status,
                            pet,
                            succeeded: false))
            };
            await using var fixture = PetDurableRawHandlerFixture.Create(
                character,
                character,
                [pet],
                executor,
                hasLocalDevelopmentCapability: true);
            await InvokeAsync(
                fixture.Handler,
                DecodeExactRequest(CreateActionPacket(
                    PetManagerProtocol.GrowthResetActionSubId,
                    dialogIndex:
                        PetManagerProtocol.PointResetDialogIndex)));

            var packets = fixture.ReadLegacyPackets();
            Check.True(
                executor.ResetGrowthCount == 1 &&
                packets is [var result] &&
                result.SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                    PetManagerProtocol.AthensNpcId,
                    PetManagerProtocol.PointResetDialogIndex,
                    resultSubId)),
                $"Growth reset {status} emits only native terminal {resultSubId}");
        }
    }

    private static async Task CheckStaleGrowthResetComparisonRevisionAsync()
    {
        var character = CharacterWithPhoenixFeather(stack: 1);
        var previewPet = CreatePet([]) with { Revision = 8 };
        var changedPet = previewPet with { Revision = 9 };
        var executor = new PetGrowthPreviewTestExecutor
        {
            PreviewIsCurrent = true,
            ResetGrowth = envelope =>
                PetDurableExecutionResult.Duplicate(
                    GrowthResetReceipt(
                        envelope,
                        PetDurableReceiptStatus.PetGrowthPreviewed,
                        previewPet,
                        succeeded: true))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [changedPet],
            executor);

        await InvokeAsync(
            fixture.Handler,
            DecodeExactRequest(new GamePacket(
                CreateActionPacket(
                    PetManagerProtocol.GrowthResetActionSubId,
                    dialogIndex: PetManagerProtocol.PointResetDialogIndex)
                    .Buffer,
                Guid.NewGuid())));

        Check.True(
            executor.CurrentChecks == 1 &&
            fixture.Transport.ReadLegacyPackets().All(packet =>
                ReadOpcode(packet) != Opcodes.NpcFunctionActionResponse),
            "a current preview token cannot mix rolled and current values from different pet revisions");
    }

    private static async Task CheckMalformedGrowthResetFailsClosedAsync()
    {
        var character = CharacterWithPhoenixFeather(stack: 1);
        var executor = new DelegatingPetDurableCommandExecutor();
        await using var fixture = PetDurableRawHandlerFixture.Create(
            character,
            character,
            [CreatePet([])],
            executor,
            hasLocalDevelopmentCapability: true);
        var arguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        arguments[0] = PetManagerProtocol.GrowthResetActionSubId;
        arguments[1] = 0;
        arguments[2] = 0;
        await InvokeAsync(
            fixture.Handler,
            DecodeExactRequest(CreateActionPacket(
                PetManagerProtocol.GrowthResetMenuSubId,
                arguments,
                dialogIndex: PetManagerProtocol.PointResetDialogIndex)));
        Check.Equal(0, executor.ResetGrowthCount,
            "malformed Growth reset never reaches persistence");
        Check.Equal(0, fixture.ReadLegacyPackets().Count,
            "malformed Growth reset emits no misleading result");
    }

    private static GameCharacter CharacterWithPhoenixFeather(short stack)
    {
        var feather = CompactItemEntry.Parse(
            $"[{PetItemCatalog.PhoenixFeather},,,,,,0,1,1,{stack},0,0]");
        return new GameCharacter
        {
            Id = PetEggHatchProtocolChecks.CharacterId,
            AccountId = PetEggHatchProtocolChecks.AccountId,
            Name = "test2",
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                PhoenixFeatherSlot,
                feather.ToCompactString()),
            Equipment = GameDefaults.DefaultEquipment(1)
        };
    }

    private static PetDurableReceipt GrowthResetReceipt(
        CommandEnvelope<PetGrowthResetCommand> envelope,
        PetDurableReceiptStatus status,
        PetBootstrapSnapshot pet,
        bool succeeded) =>
        new(
            CommandFamily.PetGrowthReset,
            status,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            succeeded && status is (
                PetDurableReceiptStatus.PetGrowthPreviewed or
                PetDurableReceiptStatus.PetGrowthReset)
                ? PhoenixFeatherSlot
                : -1,
            EquipmentSlot: -1,
            pet.PetId,
            pet.Level,
            pet.Experience,
            succeeded ? pet.Revision : 0,
            pet.IsCarried,
            pet.IsSummoned,
            PresenceOperation: 0,
            AggregateRevision: succeeded ? 1 : 0,
            AuditReference: "pet-manager-growth-reset-handler-check",
            OutboxEventId: succeeded ? Guid.NewGuid() : null,
            GrowthPreview: status ==
                PetDurableReceiptStatus.PetGrowthPreviewed
                ? GrowthPreview(envelope, pet)
                : null);

    private static PetGrowthPreviewSnapshot GrowthPreview(
        CommandEnvelope<PetGrowthResetCommand> envelope,
        PetBootstrapSnapshot pet)
    {
        var currentRates = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .Select(static stat => stat.BaseGrowthRate)
            .ToArray();
        var rates = RolledGrowthRates(currentRates);
        var modifierMinimum = pet.CompletedRebirths * .10m;
        var modifierStep = pet.CompletedRebirths * .02m;
        var rebirthModifiers = Enumerable.Range(0, 6)
            .Select(index => modifierMinimum + (index * modifierStep))
            .ToArray();
        return new PetGrowthPreviewSnapshot(
            envelope.Command.Identity.OperationId,
            pet.PetId,
            pet.Level,
            pet.Revision,
            new PetContentStatVector(
                rates[0], rates[1], rates[2],
                rates[3], rates[4], rates[5]),
            DateTimeOffset.UtcNow.AddMinutes(2),
            new PetContentStatVector(
                currentRates[0], currentRates[1], currentRates[2],
                currentRates[3], currentRates[4], currentRates[5]),
            PetGrowthPreviewRateSemantics
                .NatureBaseWithRebirthModifier,
            pet.CompletedRebirths,
            new PetContentStatVector(
                rebirthModifiers[0], rebirthModifiers[1],
                rebirthModifiers[2], rebirthModifiers[3],
                rebirthModifiers[4], rebirthModifiers[5]));
    }

    private static decimal[] RolledGrowthRates(
        IReadOnlyList<decimal> currentRates) =>
        currentRates
            .Select(static (rate, index) =>
                rate + ((index + 1) / 100m))
            .ToArray();
}
