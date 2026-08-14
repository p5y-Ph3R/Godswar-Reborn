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

internal static partial class PetManagerSkillUnlearnHandlerChecks
{
    private const int PotionSlot = 25;
    private static readonly MethodInfo HandlePetManagerMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePetManagerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePetManagerAsync was not found.");
    private static readonly FieldInfo CharacterField =
        typeof(GameClientHandler).GetField(
            "_character",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler._character was not found.");

    public static async Task RunAsync()
    {
        await CheckExactNavigationAsync();
        await CheckGrowthResetHandlerOrderingAsync();
        await CheckMalformedRequestsFailClosedAsync();
        await CheckNativeRejectedResultsAsync();
        await CheckSuccessfulRemovalAsync(
            subId: 106,
            expectedSlot: 0,
            initialPotionStack: 3,
            "first learned skill");
        await CheckSuccessfulRemovalAsync(
            subId: 114,
            expectedSlot: 6,
            initialPotionStack: 3,
            "remaining Strong Purge Potion stack");
        await CheckSuccessfulRemovalAsync(
            subId: 119,
            expectedSlot: 11,
            initialPotionStack: 1,
            "final Strong Purge Potion");
    }

    private static async Task CheckMalformedRequestsFailClosedAsync()
    {
        var character = CharacterWithPotion(stack: 3);
        var pet = CreatePet(Enumerable.Range(0, 12));
        var executor = new DelegatingPetDurableCommandExecutor();
        await using var fixture = PetDurableRawHandlerFixture.Create(
            character,
            character,
            [pet],
            executor,
            hasLocalDevelopmentCapability: true);

        var mixedPadding = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        mixedPadding[7] = 0;
        await InvokeAsync(
            fixture.Handler,
            DecodeFixedRequest(CreateActionPacket(106, mixedPadding)));

        var shortRequest = DecodeRequest(CreateActionPacket(
            114,
            Enumerable.Repeat(
                -1,
                PetManagerProtocol.FunctionArgumentCount - 1).ToArray()));
        Check.Equal(88, shortRequest.PacketLength,
            "malformed Pet Manager fixed frame is four bytes short");
        await InvokeAsync(fixture.Handler, shortRequest);

        var malformedNavigation = (int[])mixedPadding.Clone();
        await InvokeAsync(
            fixture.Handler,
            DecodeFixedRequest(CreateActionPacket(6, malformedNavigation)));

        var mismatchedEcho = DecodeFixedRequest(CreateActionPacket(
            subId: 119,
            repeatedDialogIndex: PetManagerProtocol.DialogIndex + 1));
        Check.True(
            mismatchedEcho.RepeatedDialogIndex !=
                mismatchedEcho.DialogIndex,
            "malformed Pet Manager mutation carries a mismatched dialogue echo");
        await InvokeAsync(fixture.Handler, mismatchedEcho);

        Check.Equal(0, executor.UnlearnSkillCount,
            "malformed Pet Manager fixed frames never reach persistence");
        var packets = fixture.ReadLegacyPackets();
        Check.True(
            packets is [var navigationPage] &&
            navigationPage.SequenceEqual(
                PacketBuilder.NpcFunctionActionResponse(
                    PetManagerProtocol.AthensNpcId,
                    PetManagerProtocol.DialogIndex,
                    16,
                    106, 107, 108, 109, 110, 111,
                    114, 115, 116, 117, 118, 119)),
            "read-only skill-menu navigation tolerates ignored client scratch state while malformed mutations fail closed");
    }

    private static async Task CheckNativeRejectedResultsAsync()
    {
        (PetDurableReceiptStatus Status, int NativeResult)[] cases =
        [
            (
                PetDurableReceiptStatus.PetNotTaken,
                PetManagerProtocol.NoSummonedPetResultSubId),
            (
                PetDurableReceiptStatus.StrongPurgePotionNotFound,
                PetManagerProtocol.MissingStrongPurgePotionResultSubId),
            (
                PetDurableReceiptStatus.PetSkillNotFound,
                PetManagerProtocol.EmptySkillSlotResultSubId)
        ];

        foreach (var (status, nativeResult) in cases)
        {
            var character = CharacterWithPotion(stack: 3);
            var pet = CreatePet(Enumerable.Range(0, 12));
            var executor = new DelegatingPetDurableCommandExecutor
            {
                UnlearnSkill = envelope =>
                    PetDurableExecutionResult.Rejected(
                        Receipt(envelope, status, pet, succeeded: false))
            };
            await using var fixture = PetDurableRawHandlerFixture.Create(
                character,
                character,
                [pet],
                executor,
                hasLocalDevelopmentCapability: true);

            await InvokeAsync(
                fixture.Handler,
                DecodeExactRequest(CreateSkillUnlearnActionPacket(106)));

            var packets = fixture.ReadLegacyPackets();
            Check.Equal(1, executor.UnlearnSkillCount,
                $"Pet Manager {status} is evaluated once");
            Check.True(
                packets is [var result] &&
                result.SequenceEqual(ResultPacket(nativeResult)),
                $"Pet Manager {status} maps to native result {nativeResult}");
            Check.Equal(0, packets.Count(packet =>
                    ReadOpcode(packet) == Opcodes.StorageItem),
                $"Pet Manager {status} does not consume a potion");
            Check.Equal(0, packets.Count(packet =>
                    ReadOpcode(packet) == 10_247),
                $"Pet Manager {status} does not mutate pet skills");
        }
    }

    private static async Task CheckSuccessfulRemovalAsync(
        int subId,
        int expectedSlot,
        short initialPotionStack,
        string scope)
    {
        var initialCharacter = CharacterWithPotion(initialPotionStack);
        var updatedCharacter = initialPotionStack == 1
            ? CharacterWithoutPotion(initialCharacter)
            : CharacterWithPotion(initialPotionStack - 1);
        var finalSkills = Enumerable.Range(0, 12)
            .Where(slot => slot != expectedSlot)
            .Select((sourceSlot, compactedSlot) =>
                new PetSkillSnapshot(
                    SkillId: 405 + sourceSlot,
                    SlotIndex: checked((short)compactedSlot),
                    SkillRank: 1,
                    SkillExperience: 0,
                    IsActive: true,
                    Revision: 8))
            .ToArray();
        var updatedPet = CreatePet(finalSkills, revision: 8);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            UnlearnSkill = envelope =>
                PetDurableExecutionResult.Committed(
                    Receipt(
                        envelope,
                        PetDurableReceiptStatus.PetSkillUnlearned,
                        updatedPet,
                        succeeded: true))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initialCharacter,
            updatedCharacter,
            [updatedPet],
            executor,
            hasLocalDevelopmentCapability: true);

        var request = DecodeExactRequest(
            CreateSkillUnlearnActionPacket(subId));
        await InvokeAsync(fixture.Handler, request);

        var packets = fixture.ReadLegacyPackets();
        var projectedCharacter = CharacterField.GetValue(fixture.Handler) as
            GameCharacter ?? throw new InvalidOperationException(
                "Pet Manager did not retain the refreshed character.");
        Check.Equal(1, executor.UnlearnSkillCount,
            $"{scope} executes exactly once");
        Check.True(
            executor.UnlearnSkillEnvelope is { } envelope &&
            envelope.Command.SkillSlot == expectedSlot &&
            envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.OperationId != Guid.Empty &&
            envelope.Command.Identity.RawLocalConnectionId ==
                envelope.Connection.ConnectionId,
            $"sub-ID {subId} binds authoritative skill slot {expectedSlot}");

        var expectedPackets = (initialPotionStack == 1
                ? new[]
                {
                    PacketBuilder.StorageItemKitBagDelete(PotionSlot)
                }
                : [])
            .Concat(PacketBuilder.KitBagDetailPages(updatedCharacter))
            .Concat(PacketBuilder.KitBagSlotIndexes(updatedCharacter))
            .Append(PacketBuilder.PetSkillState(updatedPet))
            .Append(PacketBuilder.PlayerStatusEffects(
                projectedCharacter,
                [],
                ClientStatusAggregate.Empty))
            .Append(PacketBuilder.PlayerStatusUpdate(
                projectedCharacter,
                ClientStatusAggregate.Empty))
            .Append(ResultPacket(
                PetManagerProtocol.SkillUnlearnedResultSubId))
            .ToArray();
        Check.Equal(expectedPackets.Length, packets.Count,
            $"{scope} emits one bounded authoritative projection");
        for (var index = 0; index < expectedPackets.Length; index++)
        {
            Check.True(
                expectedPackets[index].SequenceEqual(packets[index]),
                $"{scope} authoritative response frame {index}");
        }

        var expectedClearCount = initialPotionStack == 1 ? 1 : 0;
        Check.Equal(
            expectedClearCount,
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.StorageItem),
            initialPotionStack == 1
                ? $"{scope} clears the now-empty authoritative slot"
                : $"{scope} preserves the item object and cooling overlay");
        if (initialPotionStack == 1)
        {
            Check.True(
                packets.Any(packet => packet.SequenceEqual(
                    PacketBuilder.StorageItemKitBagDelete(PotionSlot))),
                $"{scope} clears exactly potion slot {PotionSlot}");
        }

        var skillPackets = packets
            .Where(packet => ReadOpcode(packet) == 10_247)
            .ToArray();
        Check.True(
            skillPackets is [var skillState] &&
            skillState.SequenceEqual(PacketBuilder.PetSkillState(updatedPet)),
            $"{scope} emits one authoritative compacted opcode 10247");
        Check.Equal(11, skillPackets[0][10],
            $"{scope} reports eleven remaining skills");
        for (var slot = 0; slot < finalSkills.Length; slot++)
        {
            Check.Equal(
                finalSkills[slot].SkillId,
                BinaryPrimitives.ReadUInt16LittleEndian(
                    skillPackets[0].AsSpan(12 + (slot * 2), 2)),
                $"{scope} compacts skill slot {slot}");
        }
        Check.True(
            packets[^1].SequenceEqual(ResultPacket(
                PetManagerProtocol.SkillUnlearnedResultSubId)),
            $"{scope} terminates with native success result 1063");
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        DecodedRequest request)
    {
        var task = HandlePetManagerMethod.Invoke(
            handler,
            [
                request.Packet,
                request.NpcId,
                request.DialogIndex,
                request.SubId,
                request.Arguments,
                CancellationToken.None
            ]) as Task ?? throw new InvalidOperationException(
                "Pet Manager handler returned no task.");
        await task;
    }

    private static GamePacket CreateActionPacket(
        int subId,
        int[]? arguments = null,
        int? repeatedDialogIndex = null,
        int? dialogIndex = null)
    {
        arguments ??= Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        var bytes = new byte[20 + (arguments.Length * sizeof(int))];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            PetManagerProtocol.AthensNpcId);
        var selectedDialogIndex =
            dialogIndex ?? PetManagerProtocol.DialogIndex;
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            selectedDialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            repeatedDialogIndex ?? selectedDialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + (index * sizeof(int))),
                arguments[index]);
        }
        return new GamePacket(bytes);
    }

    private static GamePacket CreateSkillUnlearnActionPacket(
        int selectedSubId)
    {
        var arguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        arguments[0] = selectedSubId;
        return CreateActionPacket(
            PetManagerProtocol.SkillUnlearnMenuSubId,
            arguments);
    }

    private static DecodedRequest DecodeExactRequest(GamePacket packet)
    {
        var request = DecodeFixedRequest(packet);
        Check.Equal(request.DialogIndex, request.RepeatedDialogIndex,
            "Pet Manager action repeats its dialogue index");
        return request;
    }

    private static DecodedRequest DecodeFixedRequest(GamePacket packet)
    {
        var request = DecodeRequest(packet);
        Check.Equal(92, request.PacketLength,
            "stock Pet Manager action is exactly 92 bytes");
        Check.Equal(PetManagerProtocol.FunctionArgumentCount,
            request.Arguments.Length,
            "stock Pet Manager action carries eighteen arguments");
        return request;
    }

    private static DecodedRequest DecodeRequest(GamePacket packet)
    {
        var bytes = packet.Buffer.AsSpan();
        Check.Equal((ushort)bytes.Length,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            "Pet Manager action length prefix");
        Check.Equal((ushort)Opcodes.NpcFunctionAction,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2)),
            "Pet Manager action opcode");
        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4));
        var dialogIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8));
        var repeatedDialogIndex =
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12));
        var subId = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16));
        var arguments = new int[(bytes.Length - 20) / sizeof(int)];
        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.Slice(20 + (index * sizeof(int))));
        }
        return new(
            packet,
            bytes.Length,
            npcId,
            dialogIndex,
            repeatedDialogIndex,
            subId,
            arguments);
    }

    private static PetDurableReceipt Receipt(
        CommandEnvelope<PetSkillUnlearnCommand> envelope,
        PetDurableReceiptStatus status,
        PetBootstrapSnapshot pet,
        bool succeeded) =>
        new(
            CommandFamily.PetSkillUnlearn,
            status,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            succeeded ? PotionSlot : -1,
            EquipmentSlot: -1,
            pet.PetId,
            pet.Level,
            pet.Experience,
            succeeded ? pet.Revision : 0,
            pet.IsCarried,
            pet.IsSummoned,
            PresenceOperation: 0,
            AggregateRevision: succeeded ? 1 : 0,
            AuditReference: "pet-manager-skill-unlearn-handler-check",
            OutboxEventId: succeeded ? Guid.NewGuid() : null);

    private static GameCharacter CharacterWithPotion(int stack)
    {
        var potion = CompactItemEntry.Parse(
            $"[{PetItemCatalog.StrongPurgePotion},,,,,,0,1,1,{stack},0,0]");
        return new GameCharacter
        {
            Id = PetEggHatchProtocolChecks.CharacterId,
            AccountId = PetEggHatchProtocolChecks.AccountId,
            Name = "test2",
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                PotionSlot,
                potion.ToCompactString()),
            Equipment = GameDefaults.DefaultEquipment(1)
        };
    }

    private static GameCharacter CharacterWithoutPotion(
        GameCharacter initial) =>
        new()
        {
            Id = initial.Id,
            AccountId = initial.AccountId,
            Name = initial.Name,
            Profession = initial.Profession,
            KitBag = KitBagSlots.ClearSlot(initial.KitBag, PotionSlot),
            Equipment = initial.Equipment
        };

    private static PetBootstrapSnapshot CreatePet(
        IEnumerable<int> skillSourceSlots) =>
        CreatePet(
            skillSourceSlots.Select(sourceSlot =>
                new PetSkillSnapshot(
                    405 + sourceSlot,
                    checked((short)sourceSlot),
                    1,
                    0,
                    true,
                    7)).ToArray(),
            revision: 7);

    private static PetBootstrapSnapshot CreatePet(
        IReadOnlyList<PetSkillSnapshot> skills,
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
        return PetEggHatchProtocolChecks.CreatePet(
            savvy,
            growth) with
        {
            IsCarried = true,
            IsSummoned = true,
            ContributesToCharacter = false,
            Revision = revision,
            OpenedSkillSlots = 12,
            AvailableSkillSlots = 12,
            Skills = skills
        };
    }

    private static byte[] ResultPacket(int resultSubId) =>
        PacketBuilder.NpcFunctionActionResponse(
            PetManagerProtocol.AthensNpcId,
            PetManagerProtocol.DialogIndex,
            resultSubId);

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));

    private sealed record DecodedRequest(
        GamePacket Packet,
        int PacketLength,
        uint NpcId,
        int DialogIndex,
        int RepeatedDialogIndex,
        int SubId,
        int[] Arguments);
}
