using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetAppearanceChangeHandlerChecks
{
    public const string CheckName =
        "Authoritative Magic Jade appearance-change handler";
    private const int JadeSlot = 53;
    private const uint JadeItemId = 11094;
    private const uint LocalPlayerObjectId = 0x00001448;
    private static readonly MethodInfo HandlePetManagerMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePetManagerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "GameClientHandler.HandlePetManagerAsync was not found.");

    public static async Task RunAsync()
    {
        CheckNativeResultMapping();
        await CheckRawLocalSuccessProjectionAsync();
        await CheckDelayedReplayUsesCurrentReceiptPetAsync();
        await CheckMalformedMutationFailsClosedAsync();
    }

    private static void CheckNativeResultMapping()
    {
        (PetDurableReceiptStatus Status, uint Result)[] cases =
        [
            (PetDurableReceiptStatus.MagicJadeNotFound, 137),
            (PetDurableReceiptStatus.MagicJadeIncompatible, 138),
            (PetDurableReceiptStatus.PetAppearancePetUnavailable, 138),
            (PetDurableReceiptStatus.PetAppearancePetNotSummoned, 139),
            (PetDurableReceiptStatus.PetAppearancePetUnbound, 140)
        ];
        foreach (var (status, result) in cases)
        {
            var receipt = RejectedReceipt(status);
            Check.Equal(
                result,
                GameClientHandler.ResolvePetLegacyResultCode(receipt),
                $"appearance {status} maps to stock result {result}");
        }
    }

    private static async Task CheckRawLocalSuccessProjectionAsync()
    {
        var initialCharacter = CharacterWithJade();
        var updatedCharacter = CharacterWithoutJade(initialCharacter);
        var initialPet = CreatePet(speciesId: 1, revision: 7);
        var updatedPet = CreatePet(speciesId: 45, revision: 8);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            ChangeAppearance = envelope =>
                PetDurableExecutionResult.Committed(
                    SucceededReceipt(envelope, updatedPet))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initialCharacter,
            updatedCharacter,
            [updatedPet],
            executor,
            hasLocalDevelopmentCapability: true);

        var request = CreateRequest();
        await InvokeAsync(fixture.Handler, request);

        Check.True(
            executor.ChangeAppearanceCount == 1 &&
            executor.ChangeAppearanceEnvelope is { } envelope &&
            envelope.Family == CommandFamily.PetAppearanceChange &&
            envelope.Command.KitBagSlot == JadeSlot &&
            envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.OperationId != Guid.Empty &&
            envelope.Command.Identity.RawLocalConnectionId ==
                envelope.Connection.ConnectionId,
            "raw LocalDevelopment appearance mutation reaches family 52 with a connection-scoped identity");

        var expected = new List<byte[]>();
        expected.Add(PacketBuilder.StorageItemKitBagDelete(JadeSlot));
        expected.AddRange(PacketBuilder.KitBagDetailPages(updatedCharacter));
        expected.AddRange(PacketBuilder.KitBagSlotIndexes(updatedCharacter));
        expected.Add(PacketBuilder.PetOperationResult(
            checked((uint)updatedPet.PetId),
            PetOperationResultCode.RecallSucceeded));
        expected.Add(PacketBuilder.PetAppearanceRefresh(
            PetContentTestCatalog.Instance,
            updatedPet));
        expected.Add(PacketBuilder.PetOperationResult(
            checked((uint)updatedPet.PetId),
            PetOperationResultCode.CallOutSucceeded));
        expected.Add(PacketBuilder.PetWorldPresence(
            checked((uint)updatedPet.PetId),
            LocalPlayerObjectId));
        expected.Add(PacketBuilder.NpcFunctionActionResponse(
            PetManagerProtocol.AthensNpcId,
            PetManagerProtocol.DialogIndex,
            PetManagerProtocol.AppearanceChangeSucceededResultSubId));

        var packets = fixture.ReadLegacyPackets();
        Check.Equal(expected.Count, packets.Count,
            "appearance change emits one bounded authoritative projection");
        for (var index = 0; index < expected.Count; index++)
        {
            Check.True(
                expected[index].SequenceEqual(packets[index]),
                $"appearance projection frame {index} is canonical");
        }
        var appearanceRefresh = packets.Single(packet =>
            ReadOpcode(packet) == Opcodes.PetLevelUpgrade);
        Check.Equal(
            (ushort)72,
            BinaryPrimitives.ReadUInt16LittleEndian(appearanceRefresh),
            "appearance uses the non-destructive extended 10286 refresh");
        Check.Equal(
            (byte)updatedPet.SpeciesId,
            appearanceRefresh[68],
            "opcode 10286 projects the committed appearance species");
        Check.Equal(
            (byte)1,
            appearanceRefresh[69],
            "opcode 10286 projects the committed bound flag");
    }

    private static async Task CheckDelayedReplayUsesCurrentReceiptPetAsync()
    {
        var reusedCharacter = CharacterWithJade();
        var historical = CreatePet(speciesId: 45, revision: 8);
        var receiptPet = CreatePet(speciesId: 2, revision: 9) with
        {
            IsCarried = false,
            IsSummoned = false
        };
        var newerSummoned = CreatePet(speciesId: 3, revision: 4) with
        {
            PetId = historical.PetId + 1,
            Name = "Newer Summoned"
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            ChangeAppearance = envelope =>
                PetDurableExecutionResult.Duplicate(
                    SucceededReceipt(envelope, historical))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            reusedCharacter,
            reusedCharacter,
            [receiptPet, newerSummoned],
            executor,
            hasLocalDevelopmentCapability: true);

        await InvokeAsync(fixture.Handler, CreateRequest());

        var packets = fixture.ReadLegacyPackets();
        Check.Equal(
            2,
            packets.Count,
            "delayed appearance replay emits only a narrow pet refresh and modal result");
        Check.True(
            PacketBuilder.PetAppearanceRefresh(
                PetContentTestCatalog.Instance,
                receiptPet).SequenceEqual(packets[0]),
            "delayed replay refreshes the receipt pet's current appearance");
        Check.True(
            !packets.Any(packet =>
                ReadOpcode(packet) is 10_237 or 10_248) &&
            !packets.Any(packet =>
                ReadOpcode(packet) == Opcodes.StorageItem),
            "replay neither cycles a newer summoned pet nor clears a reused bag slot");
    }

    private static async Task CheckMalformedMutationFailsClosedAsync()
    {
        var character = CharacterWithJade();
        var pet = CreatePet(speciesId: 1, revision: 7);
        var executor = new DelegatingPetDurableCommandExecutor();
        await using var fixture = PetDurableRawHandlerFixture.Create(
            character,
            character,
            [pet],
            executor,
            hasLocalDevelopmentCapability: true);

        var missingActionMarker = CreateRequest(actionArgument: -1);
        await InvokeAsync(fixture.Handler, missingActionMarker);

        var descriptionPageMarker = CreateRequest(
            actionArgument:
                PetManagerProtocol.AppearanceChangeDescriptionSubId);
        await InvokeAsync(fixture.Handler, descriptionPageMarker);

        var request = CreateRequest();
        request.Arguments[5] = 0;
        await InvokeAsync(fixture.Handler, request);
        Check.True(
            executor.ChangeAppearanceCount == 0 &&
            fixture.ReadLegacyPackets().Count == 0,
            "missing, page-113, or non-canonical Magic Jade actions never reach persistence");
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        AppearanceRequest request)
    {
        var task = HandlePetManagerMethod.Invoke(
            handler,
            [
                request.Packet,
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                request.Arguments,
                CancellationToken.None
            ]) as Task ?? throw new InvalidOperationException(
                "Pet Manager appearance handler returned no task.");
        await task;
    }

    private static AppearanceRequest CreateRequest(
        int actionArgument =
            PetManagerProtocol.AppearanceChangeActionArgumentValue)
    {
        var arguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        arguments[0] = actionArgument;
        arguments[
            PetManagerProtocol
                .AppearanceChangeFirstScratchArgumentIndex] =
            0x04CB_1074;
        arguments[
            PetManagerProtocol
                .AppearanceChangeFirstScratchArgumentIndex + 1] =
            0;
        arguments[
            PetManagerProtocol
                .AppearanceChangeLastScratchArgumentIndex] =
            unchecked((int)0x8C35_0102);
        arguments[PetManagerProtocol.AppearanceChangeItemArgumentIndex] =
            (JadeSlot / 24 * 100) + (JadeSlot % 24);
        var bytes = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            PetManagerProtocol.AthensNpcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            PetManagerProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            PetManagerProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(16),
            PetManagerProtocol.AppearanceChangeMenuSubId);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + (index * sizeof(int))),
                arguments[index]);
        }
        return new(new GamePacket(bytes), arguments);
    }

    private static PetDurableReceipt SucceededReceipt(
        CommandEnvelope<PetAppearanceChangeCommand> envelope,
        PetBootstrapSnapshot pet) =>
        new(
            CommandFamily.PetAppearanceChange,
            PetDurableReceiptStatus.PetAppearanceChanged,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            JadeSlot,
            EquipmentSlot: -1,
            pet.PetId,
            pet.Level,
            pet.Experience,
            pet.Revision,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 0,
            AggregateRevision: 1,
            AuditReference: "appearance-handler-check",
            OutboxEventId: Guid.NewGuid(),
            AppearanceChange: new PetAppearanceChangeEvidence(
                1,
                "Rock Elf",
                45,
                "Cupid",
                JadeItemId,
                "Magic Jade: Cupid",
                MagicJadeItemInstanceId: 9001,
                JadeSlot,
                PetContentTestCatalog.Instance.Revision.Sha256,
                TestItemContent.Content.Templates.Revision.Sha256));

    private static PetDurableReceipt RejectedReceipt(
        PetDurableReceiptStatus status) =>
        new(
            CommandFamily.PetAppearanceChange,
            status,
            AccountId: PetEggHatchProtocolChecks.AccountId,
            CharacterId: PetEggHatchProtocolChecks.CharacterId,
            KitBagSlot: JadeSlot,
            EquipmentSlot: -1,
            PetId: 0,
            PetLevel: 0,
            PetExperience: 0,
            PetRevision: 0,
            IsCarried: false,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 0,
            AuditReference: "appearance-rejection-check",
            OutboxEventId: null);

    private static GameCharacter CharacterWithJade()
    {
        var jade = CompactItemEntry.Parse(
            $"[{JadeItemId},,,,,,0,1,0,1,0,0]");
        return new GameCharacter
        {
            Id = PetEggHatchProtocolChecks.CharacterId,
            AccountId = PetEggHatchProtocolChecks.AccountId,
            Name = "test2",
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                JadeSlot,
                jade.ToCompactString()),
            Equipment = GameDefaults.DefaultEquipment(1)
        };
    }

    private static GameCharacter CharacterWithoutJade(
        GameCharacter initial) =>
        new()
        {
            Id = initial.Id,
            AccountId = initial.AccountId,
            Name = initial.Name,
            Profession = initial.Profession,
            KitBag = KitBagSlots.ClearSlot(initial.KitBag, JadeSlot),
            Equipment = initial.Equipment
        };

    private static PetBootstrapSnapshot CreatePet(
        short speciesId,
        long revision)
    {
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            50m,
            new Random(50));
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_500,
            new Random(3_500));
        return PetEggHatchProtocolChecks.CreatePet(savvy, growth) with
        {
            SpeciesId = speciesId,
            Name = "Appearance Test",
            IsBound = true,
            IsCarried = true,
            IsSummoned = true,
            ContributesToCharacter = false,
            Revision = revision
        };
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));

    private sealed record AppearanceRequest(
        GamePacket Packet,
        int[] Arguments);
}
