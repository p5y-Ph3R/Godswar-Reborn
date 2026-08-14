using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetManagerUtilityHandlerChecks
{
    public const string CheckName =
        "Authoritative Pet Manager utility handler and projection";
    private const int MaterialSlot = 0;
    private const int PackedSlot = 1;
    private static readonly MethodInfo HandlePetManagerMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePetManagerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "GameClientHandler.HandlePetManagerAsync was not found.");

    public static async Task RunAsync()
    {
        await CheckCommittedSealProjectionAsync();
        await CheckInPlaceSealProjectionAsync();
        await CheckSealReplayDoesNotRepeatCollectionMutationAsync();
        await CheckNarrowUtilityProjectionsAsync();
        await CheckSecurePackedUnsealSettlementAsync();
        await CheckPackedUnsealReplacesSummonedCompanionAsync();
        await CheckPackedUnsealRestoresOnlyFullHealthAsync();
        await CheckPackedUnsealReplayDoesNotRepeatPresenceAsync();
        await CheckPackedPetDetailAuthorizationAsync();
    }

    private static async Task CheckInPlaceSealProjectionAsync()
    {
        var initialPet = CreatePet(revision: 7);
        var initial = CharacterWithItem(10108, MaterialSlot);
        var updated = CharacterWithItem(
            10109,
            MaterialSlot,
            linkedPetId: initialPet.PetId,
            bound: 1);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            PetManagerUtility = envelope =>
                PetDurableExecutionResult.Committed(
                    SuccessReceipt(
                        envelope,
                        initialPet,
                        PetManagerUtilityOperation.Seal,
                        sealSlot: MaterialSlot))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initial,
            updated,
            [],
            executor,
            hasLocalDevelopmentCapability: true);

        await InvokeNpcAsync(
            fixture.Handler,
            PetManagerProtocol.SealMenuSubId,
            PetManagerProtocol.SealActionSubId);

        var packets = fixture.ReadLegacyPackets();
        var opcodes = packets.Select(ReadOpcode).ToArray();
        var clearIndex = Array.IndexOf(opcodes, (ushort)0x2744);
        var detailIndexes = opcodes
            .Select((opcode, index) => (opcode, index))
            .Where(pair => pair.opcode == 0x2731)
            .Select(pair => pair.index)
            .ToArray();
        var slotIndexes = opcodes
            .Select((opcode, index) => (opcode, index))
            .Where(pair => pair.opcode == 0x2748)
            .Select(pair => pair.index)
            .ToArray();
        Check.True(
            clearIndex >= 0 &&
            packets[clearIndex].Length == 16 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                packets[clearIndex].AsSpan(8)) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                packets[clearIndex].AsSpan(10)) == MaterialSlot &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packets[clearIndex].AsSpan(12)) == uint.MaxValue,
            "in-place Seal explicitly clears the changed occupied slot with native 10052");
        Check.True(
            detailIndexes.Length == 8 &&
            slotIndexes.Length == 96 &&
            detailIndexes[0] == clearIndex + 1 &&
            slotIndexes[0] == detailIndexes[^1] + 1,
            "in-place Seal orders 10052 before the complete 10033/10056 bag refresh");

        var packedRecord = packets[detailIndexes[0]].AsSpan(24, 72);
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(packedRecord) == 10109 &&
            packedRecord[26] == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packedRecord.Slice(56)) == initialPet.PetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packets[slotIndexes[0]].AsSpan(20)) == 10109,
            "10033/10056 rehydrate the same slot as a bound packed jade with its linked pet ID");
    }

    private static async Task CheckCommittedSealProjectionAsync()
    {
        var initialPet = CreatePet(revision: 7);
        var initial = CharacterWithItem(10108, MaterialSlot);
        var updated = CharacterWithItem(
            10109,
            PackedSlot,
            linkedPetId: initialPet.PetId,
            bound: 1);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            PetManagerUtility = envelope =>
                PetDurableExecutionResult.Committed(
                    SuccessReceipt(envelope, initialPet,
                        PetManagerUtilityOperation.Seal))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initial,
            updated,
            [],
            executor,
            hasLocalDevelopmentCapability: true);

        await InvokeNpcAsync(
            fixture.Handler,
            PetManagerProtocol.SealMenuSubId,
            PetManagerProtocol.SealActionSubId);

        Check.True(
            executor.PetManagerUtilityCount == 1 &&
            executor.PetManagerUtilityEnvelope?.Command.Operation ==
                PetManagerUtilityOperation.Seal &&
            executor.PetManagerUtilityEnvelope.Command.KitBagSlot == -1,
            "Seal targets only the locked summoned pet, never a packet pet ID");
        var packets = fixture.ReadLegacyPackets();
        var opcodes = packets.Select(ReadOpcode).ToArray();
        var recallIndex = Array.IndexOf(
            opcodes,
            Opcodes.PetOperationResult);
        var listIndex = Array.IndexOf(opcodes, (ushort)10_237);
        Check.True(
            recallIndex >= 0 && listIndex == recallIndex + 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packets[recallIndex].AsSpan(4)) == initialPet.PetId &&
            packets[recallIndex][8] ==
                (byte)PetOperationResultCode.RecallSucceeded,
            "committed Seal tears down the summoned model immediately before 10237");
        Check.Equal(
            (ushort)Opcodes.NpcFunctionActionResponse,
            opcodes[^1],
            "Seal ends on the stock NPC result page");
        Check.Equal(
            PetManagerProtocol.SealSucceededResultSubId,
            BinaryPrimitives.ReadInt32LittleEndian(
                packets[^1].AsSpan(12)),
            "Seal reports stock success 1053");
    }

    private static async Task
        CheckSealReplayDoesNotRepeatCollectionMutationAsync()
    {
        var historical = CreatePet(revision: 7);
        var current = CharacterWithItem(
            10109,
            PackedSlot,
            linkedPetId: historical.PetId,
            bound: 1);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            PetManagerUtility = envelope =>
                PetDurableExecutionResult.Duplicate(
                    SuccessReceipt(envelope, historical,
                        PetManagerUtilityOperation.Seal))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            current,
            current,
            [],
            executor,
            hasLocalDevelopmentCapability: true);
        await InvokeNpcAsync(
            fixture.Handler,
            PetManagerProtocol.SealMenuSubId,
            PetManagerProtocol.SealActionSubId);

        var opcodes = fixture.ReadLegacyPackets().Select(ReadOpcode).ToArray();
        Check.True(
            !opcodes.Contains(Opcodes.PetOperationResult) &&
            !opcodes.Contains((ushort)10_237),
            "duplicate Seal returns its receipt without replaying Recall or 10237");
    }

    private static async Task CheckNarrowUtilityProjectionsAsync()
    {
        await CheckNarrowUtilityProjectionAsync(
            PetManagerUtilityOperation.CheckGrowth,
            PetManagerProtocol.GrowthCheckMenuSubId,
            PetManagerProtocol.GrowthCheckActionSubId,
            materialId: 10106,
            expectGenderRefresh: false);
        await CheckNarrowUtilityProjectionAsync(
            PetManagerUtilityOperation.ChangeGender,
            PetManagerProtocol.ChangeGenderMenuSubId,
            PetManagerProtocol.ChangeGenderActionArgumentValue,
            materialId: 11015,
            expectGenderRefresh: true);
        await CheckNarrowUtilityProjectionAsync(
            PetManagerUtilityOperation.ClaimPetCall,
            PetManagerProtocol.ClaimPetCallMenuSubId,
            argument0: -1,
            materialId: 0,
            expectGenderRefresh: false);
        await CheckNarrowUtilityProjectionAsync(
            PetManagerUtilityOperation.ClaimMerge,
            PetManagerProtocol.ClaimMergeMenuSubId,
            argument0: -1,
            materialId: 0,
            expectGenderRefresh: false);
    }

    private static async Task CheckNarrowUtilityProjectionAsync(
        PetManagerUtilityOperation operation,
        int subId,
        int argument0,
        uint materialId,
        bool expectGenderRefresh)
    {
        var initialPet = CreatePet(revision: 7);
        var updatedPet = operation switch
        {
            PetManagerUtilityOperation.CheckGrowth => initialPet with
            {
                GrowthRevealed = true,
                Revision = 8
            },
            PetManagerUtilityOperation.ChangeGender => initialPet with
            {
                Sex = 1,
                Revision = 8
            },
            _ => initialPet
        };
        var initial = materialId == 0
            ? CharacterWithItem(0, -1)
            : CharacterWithItem(materialId, MaterialSlot);
        var grantedId = operation == PetManagerUtilityOperation.ClaimPetCall
            ? 11003u
            : 11004u;
        var updated = operation is PetManagerUtilityOperation.ClaimPetCall or
                PetManagerUtilityOperation.ClaimMerge
            ? CharacterWithItem(grantedId, PackedSlot)
            : CharacterWithItem(0, -1);
        var projectedPets = operation is
                PetManagerUtilityOperation.ClaimPetCall or
                PetManagerUtilityOperation.ClaimMerge
            ? Array.Empty<PetBootstrapSnapshot>()
            : [updatedPet];
        var executor = new DelegatingPetDurableCommandExecutor
        {
            PetManagerUtility = envelope =>
                PetDurableExecutionResult.Committed(
                    SuccessReceipt(envelope, initialPet, operation))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            initial,
            updated,
            projectedPets,
            executor,
            hasLocalDevelopmentCapability: true);
        await InvokeNpcAsync(fixture.Handler, subId, argument0);

        var packets = fixture.ReadLegacyPackets();
        Check.True(
            packets.All(packet => ReadOpcode(packet) != 10_237),
            $"{operation} never rebuilds the owned-pet collection");
        var genderPackets = packets.Where(packet =>
            ReadOpcode(packet) == Opcodes.PetLevelUpgrade &&
            packet.Length == 76).ToArray();
        Check.Equal(
            expectGenderRefresh ? 1 : 0,
            genderPackets.Length,
            $"{operation} has the expected narrow gender projection count");
    }

    private static async Task CheckSecurePackedUnsealSettlementAsync()
    {
        var operationId = Guid.Parse("5E034565-7792-4CDD-928B-176199A9BB7C");
        var sealedPet = CreatePet(revision: 7) with
        {
            ActivityState = "sealed",
            IsCarried = false,
            IsSummoned = false
        };
        var unsealedPet = sealedPet with
        {
            ActivityState = "owned",
            IsCarried = true,
            IsSummoned = true,
            Revision = 8
        };
        var initial = CharacterWithItem(
            10109,
            MaterialSlot,
            linkedPetId: sealedPet.PetId,
            bound: 1);
        var updated = CharacterWithItem(0, -1);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            PetManagerUtility = envelope =>
                PetDurableExecutionResult.Committed(
                    SuccessReceipt(envelope, sealedPet,
                        PetManagerUtilityOperation.Unseal))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            updated,
            [unsealedPet],
            executor);
        await fixture.InvokeAsync(BreakItemPacket(MaterialSlot, operationId));

        Check.True(
            executor.PetManagerUtilityEnvelope is { } envelope &&
            envelope.Command.Operation == PetManagerUtilityOperation.Unseal &&
            envelope.Command.KitBagSlot == MaterialSlot &&
            envelope.Command.Identity.OperationId == operationId &&
            envelope.Command.Identity.IsSecureClient,
            "secure opcode10051 routes packed 10109 to exact family55 Unseal");
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.CommandFamily ==
                (ushort)CommandFamily.PetManagerUtility &&
            result.ResultCode ==
                (uint)PetDurableReceiptStatus.PetUnsealed &&
            result.Disposition == SecureLegacyCommandDisposition.Applied &&
            result.OperationId == operationId,
            "family26-shaped intent receives one exact family55 Unseal settlement");
        var packets = fixture.Transport.ReadLegacyPackets();
        var opcodes = packets.Select(ReadOpcode).ToArray();
        var listIndex = Array.IndexOf(opcodes, (ushort)10_237);
        var takeIndex = packets.ToList().FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetOperationResult(
                checked((uint)unsealedPet.PetId),
                PetOperationResultCode.TakeSucceeded)));
        var callOutIndex = packets.ToList().FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetOperationResult(
                checked((uint)unsealedPet.PetId),
                PetOperationResultCode.CallOutSucceeded)));
        var energyIndex = Array.IndexOf(opcodes, Opcodes.PetEnergy);
        var extendedIndex = Array.IndexOf(opcodes, (ushort)10_167);
        var gameDataIndex = Array.IndexOf(opcodes, (ushort)10_166);
        Check.True(
            listIndex >= 0 &&
            listIndex < takeIndex &&
            takeIndex < callOutIndex &&
            callOutIndex < energyIndex &&
            energyIndex < extendedIndex &&
            extendedIndex < gameDataIndex,
            "committed Unseal loads the pet, selects and summons it, projects full energy, then refreshes carried passives");
        Check.Equal(
            1_800u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packets[energyIndex].AsSpan(4)),
            "committed Unseal immediately projects a full native energy gauge");
        Check.True(
            !opcodes.Contains((ushort)10_248),
            "live Unseal does not misuse world-ready opcode 10248");
    }

    private static async Task CheckPackedPetDetailAuthorizationAsync()
    {
        var pet = CreatePet(revision: 7) with
        {
            ActivityState = "sealed",
            IsCarried = false,
            IsSummoned = false
        };
        var character = CharacterWithItem(
            10109,
            MaterialSlot,
            linkedPetId: pet.PetId,
            bound: 1);
        var snapshot = PetDurableHandlerFixture.CreateSnapshot(
            character,
            [pet]).Character!.Pets.Single();
        var reader = new DetailReader(snapshot);
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [],
            new DelegatingPetDurableCommandExecutor(),
            sealedPetSnapshots: reader);

        await fixture.InvokeAsync(PackedDetailPacket(pet.PetId + 1));
        Check.True(
            fixture.Transport.ReadLegacyPackets().Count == 0 &&
            reader.LastAccountId == character.AccountId &&
            reader.LastCharacterId == character.Id,
            "another pet ID cannot enumerate a packed pet outside the authenticated link");

        await fixture.InvokeAsync(PackedDetailPacket(pet.PetId));
        var response = fixture.Transport.ReadLegacyPackets().Single();
        Check.True(
            response.Length == 172 &&
            ReadOpcode(response) == Opcodes.PackedPetDetailResponse &&
            BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4)) ==
                pet.PetId,
            "authorized packed tooltip returns one exact 10284 pet record");
    }

}
