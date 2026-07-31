using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetPresenceProtocolChecks
{
    private const int AccountId = 13;
    private const int CharacterId = 2;
    private const uint PetId = 1;

    public static async Task RunAsync()
    {
        CheckOpcodeCatalog();
        CheckResultFrames();
        await CheckSuccessfulActionAsync(
            Opcodes.PetTakeRequest,
            PetPresenceOperation.Take,
            PetOperationResultCode.TakeSucceeded);
        await CheckSuccessfulActionAsync(
            Opcodes.PetCallOutRequest,
            PetPresenceOperation.CallOut,
            PetOperationResultCode.CallOutSucceeded);
        await CheckSuccessfulActionAsync(
            Opcodes.PetRecallRequest,
            PetPresenceOperation.Recall,
            PetOperationResultCode.RecallSucceeded);
        await CheckRejectedActionAsync();
        await CheckMalformedActionAsync();
    }

    private static void CheckOpcodeCatalog()
    {
        Check.Equal(
            (ushort)10_239,
            Opcodes.PetTakeRequest,
            "pet Take request opcode");
        Check.Equal(
            (ushort)10_240,
            Opcodes.PetCallOutRequest,
            "pet Call Out request opcode");
        Check.Equal(
            (ushort)10_241,
            Opcodes.PetRecallRequest,
            "pet Recall request opcode");
        Check.Equal(
            (ushort)10_244,
            Opcodes.PetOperationResult,
            "pet operation result opcode");
        Check.Equal(
            nameof(Opcodes.PetTakeRequest),
            Opcodes.Name(Opcodes.PetTakeRequest),
            "pet opcode has a diagnostic name");
    }

    private static void CheckResultFrames()
    {
        CheckFrame(
            PetOperationResultCode.TakeSucceeded,
            "090004280100000001");
        CheckFrame(
            PetOperationResultCode.TakeFailed,
            "090004280100000002");
        CheckFrame(
            PetOperationResultCode.RecallSucceeded,
            "090004280100000005");
        CheckFrame(
            PetOperationResultCode.RecallFailed,
            "090004280100000006");
        CheckFrame(
            PetOperationResultCode.CallOutSucceeded,
            "090004280100000007");
        CheckFrame(
            PetOperationResultCode.CallOutFailed,
            "090004280100000008");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetOperationResult(
                PetId,
                (PetOperationResultCode)0),
            "undefined native pet result is rejected");
    }

    private static async Task CheckSuccessfulActionAsync(
        ushort opcode,
        PetPresenceOperation expectedOperation,
        PetOperationResultCode expectedCode)
    {
        var operationId = Guid.NewGuid();
        var expectedCommandOperation = expectedOperation switch
        {
            PetPresenceOperation.Take =>
                PetPresenceCommandOperation.Take,
            PetPresenceOperation.CallOut =>
                PetPresenceCommandOperation.CallOut,
            PetPresenceOperation.Recall =>
                PetPresenceCommandOperation.Recall,
            _ => throw new ArgumentOutOfRangeException(
                nameof(expectedOperation))
        };
        var isSummoned = expectedOperation == PetPresenceOperation.CallOut;
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Transition = envelope =>
                PetDurableExecutionResult.Committed(
                    new PetDurableReceipt(
                        CommandFamily.PetPresenceTransition,
                        PetDurableReceiptStatus.PresenceChanged,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        KitBagSlot: -1,
                        EquipmentSlot: -1,
                        PetId,
                        PetLevel: 1,
                        PetExperience: 0,
                        PetRevision: 2,
                        IsCarried: true,
                        IsSummoned: isSummoned,
                        PresenceOperation:
                            checked((byte)((byte)envelope.Command.Operation + 1)),
                        AggregateRevision: 1,
                        AuditReference: "pet-presence-check",
                        OutboxEventId: Guid.NewGuid()))
        };
        var character = CreateCharacter();
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [CreatePet(isCarried: true, isSummoned, revision: 2)],
            executor);
        await fixture.InvokeAsync(
            CreateActionPacket(opcode, PetId, operationId));
        var packets = fixture.Transport.ReadLegacyPackets();
        var response = packets.Single(packet =>
            ReadOpcode(packet) == Opcodes.PetOperationResult);

        Check.True(
            response.SequenceEqual(
                PacketBuilder.PetOperationResult(PetId, expectedCode)),
            $"{expectedOperation} emits its native success code");
        Check.Equal(
            1,
            executor.TransitionCount,
            $"{expectedOperation} persists once");
        Check.True(
            executor.TransitionEnvelope is { } envelope &&
            envelope.Subject.AccountId == AccountId &&
            envelope.Subject.CharacterId == CharacterId &&
            envelope.Command.PetId == PetId &&
            envelope.Command.Operation == expectedCommandOperation &&
            envelope.Command.ClientOperationId == operationId,
            $"{expectedOperation} reaches the focused durable transition");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition:
                        SecureLegacyCommandDisposition.Applied,
                    CommandFamily:
                        (ushort)CommandFamily.PetPresenceTransition,
                    OperationId: var completedOperation
                }
            ] &&
            completedOperation == operationId,
            $"{expectedOperation} returns one durable command result");
    }

    private static async Task CheckRejectedActionAsync()
    {
        var operationId = Guid.NewGuid();
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Transition = envelope =>
                PetDurableExecutionResult.Rejected(
                    new PetDurableReceipt(
                        CommandFamily.PetPresenceTransition,
                        PetDurableReceiptStatus.PetNotTaken,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        KitBagSlot: -1,
                        EquipmentSlot: -1,
                        PetId,
                        PetLevel: 1,
                        PetExperience: 0,
                        PetRevision: 1,
                        IsCarried: false,
                        IsSummoned: false,
                        PresenceOperation: 2,
                        AggregateRevision: 0,
                        AuditReference: "pet-presence-rejection-check",
                        OutboxEventId: null))
        };
        var character = CreateCharacter();
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [CreatePet(isCarried: false, isSummoned: false, revision: 1)],
            executor);
        await fixture.InvokeAsync(
            CreateActionPacket(
                Opcodes.PetCallOutRequest,
                PetId,
                operationId));
        var response = fixture.Transport.ReadLegacyPackets().Single(packet =>
            ReadOpcode(packet) == Opcodes.PetOperationResult);

        Check.True(
            response.SequenceEqual(
                PacketBuilder.PetOperationResult(
                    PetId,
                    PetOperationResultCode.CallOutFailed)),
            "store rejection emits Call Out failure");
        Check.Equal(
            1,
            executor.TransitionCount,
            "rejected action reaches durable persistence once");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition:
                        SecureLegacyCommandDisposition.Rejected,
                    ResultCode: (uint)PetDurableReceiptStatus.PetNotTaken,
                    OperationId: var completedOperation
                }
            ] &&
            completedOperation == operationId,
            "rejected presence action returns its durable terminal result");
    }

    private static async Task CheckMalformedActionAsync()
    {
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Transition = _ => throw new InvalidOperationException(
                "Malformed presence requests cannot execute.")
        };
        var malformed = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(malformed, 7);
        BinaryPrimitives.WriteUInt16LittleEndian(
            malformed.AsSpan(2),
            Opcodes.PetTakeRequest);
        var character = CreateCharacter();
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [],
            executor);
        await fixture.InvokeAsync(
            new GamePacket(malformed, Guid.NewGuid()));
        await fixture.InvokeAsync(
            CreateActionPacket(Opcodes.PetTakeRequest, PetId));
        var responses = fixture.Transport.ReadLegacyPackets();

        Check.Equal(
            0,
            responses.Count,
            "malformed or unidentified Take emits no invented result");
        Check.Equal(
            0,
            executor.TransitionCount,
            "malformed pet request cannot reach persistence");
    }

    private static void CheckFrame(
        PetOperationResultCode code,
        string expectedHex)
    {
        var packet = PacketBuilder.PetOperationResult(PetId, code);
        Check.True(
            packet.SequenceEqual(Convert.FromHexString(expectedHex)),
            $"{code} frame bytes");
    }

    private static GamePacket CreateActionPacket(
        ushort opcode,
        uint petId,
        Guid? operationId = null)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), petId);
        return new GamePacket(packet, operationId);
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "test2",
            Equipment = GameDefaults.DefaultEquipment(1),
            KitBag = GameDefaults.EmptyKitBag
        };

    private static PetBootstrapSnapshot CreatePet(
        bool isCarried,
        bool isSummoned,
        long revision) =>
        new(
            PetId,
            AccountId,
            CharacterId,
            SpeciesId: 1,
            Name: "Rock Elf",
            Sex: 0,
            Level: 1,
            Experience: 0,
            PetAptitude.Godly,
            Rank: 0,
            CompletedRebirths: 0,
            RebirthsRemaining: 0,
            CompletedPetMerges: 0,
            HasSoulContract: false,
            HasOwnerMergeTalent: false,
            CurrentEnergy: 100,
            MaximumEnergy: 100,
            Amity: 100,
            Satiety: 100,
            RemainingLifetime: 600,
            AvailableStatPoints: 0,
            GrowthRevealed: true,
            IsBound: false,
            ActivityState: "owned",
            isCarried,
            isSummoned,
            // Summoning does not itself enable the optional owner-merge
            // contribution. That requires HasOwnerMergeTalent as a separate
            // persisted precondition.
            ContributesToCharacter: false,
            revision,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            Enumerable.Range(1, 6).Select(index =>
                new PetStatValueSnapshot(
                    checked((short)index),
                    InitialSavvy: index,
                    AddedSavvy: 0,
                    BaseGrowthRate: 1,
                    GrowthAcceleration: 0,
                    revision)).ToArray(),
            CharacterBonuses: [],
            Skills: []);
}
