using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetSoulContractProtocolChecks
{
    public const string CheckName = "Native durable pet Soul Contract";

    private const int AccountId = 13;
    private const int CharacterId = 2;
    private const long PetId = 71;
    private const int MaterialId = 10105;
    private const ushort PlayerVitalsOpcode = 0x2771;
    private const ushort PlayerStatusOpcode = 0x27B6;
    private const ushort PlayerExtendedStatusOpcode = 0x27B7;

    public static async Task RunAsync()
    {
        CheckPolicyAndPersistenceCodec();
        CheckExactWireResult();
        await CheckCommittedProjectionAsync();
        await CheckDelayedDuplicateAsync();
        await CheckZeroSpiritRequestAsync();
        await CheckMalformedRequestAsync();
    }

    private static void CheckPolicyAndPersistenceCodec()
    {
        var raw = new PetSavvy(1m, 2m, 3m, 4m, 5m, 6m);
        Check.Equal(
            raw,
            PetSoulContractPolicy.ResolveDisplayedTotal(raw, 0),
            "stage zero has no Soul Contract bonus");
        for (byte stage = 1; stage <= 6; stage++)
        {
            var expected = stage + 2m;
            var displayed =
                PetSoulContractPolicy.ResolveDisplayedTotal(raw, stage);
            Check.Equal(
                raw.Agility + expected,
                displayed.Agility,
                $"Soul Contract stage {stage} fixed per-stat bonus");
        }
        var first = PetSoulContractPolicy.ResolveDisplayedTotal(raw, 1);
        var replacement =
            PetSoulContractPolicy.ResolveDisplayedTotal(raw, 6);
        Check.True(
            raw == new PetSavvy(1m, 2m, 3m, 4m, 5m, 6m) &&
            replacement.Agility - first.Agility == 5m,
            "re-signing replaces the stage without mutating raw Basic");

        var receipt = Receipt(
            PetDurableReceiptStatus.PetSoulContractSigned,
            Pet(6, revision: 12),
            quantity: 5,
            previousStage: 2,
            kitBagSlot: 0);
        var decoded = PetDurablePersistenceCodec.Decode(
            PetDurablePersistenceCodec.Encode(receipt));
        Check.True(
            decoded.Family == CommandFamily.PetSoulContract &&
            decoded.SoulContract == receipt.SoulContract,
            "Soul Contract receipt preserves replacement evidence");
    }

    private static void CheckExactWireResult()
    {
        Check.Equal(
            (ushort)10270,
            Opcodes.PetSoulContractRequest,
            "Soul Contract request opcode");
        Check.Equal(
            (ushort)10271,
            Opcodes.PetSoulContractResult,
            "Soul Contract result opcode");
        var packet = PacketBuilder.PetSoulContract(6);
        Check.True(
            packet.Length == 5 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet) == 5 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
                Opcodes.PetSoulContractResult &&
            packet[4] == 6,
            "10271 carries only the absolute stage byte");
    }

    private static async Task CheckCommittedProjectionAsync()
    {
        var operationId = Guid.NewGuid();
        var pet = Pet(6, revision: 12);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            SignSoulContract = _ =>
                PetDurableExecutionResult.Committed(
                    Receipt(
                        PetDurableReceiptStatus.PetSoulContractSigned,
                        pet,
                        quantity: 5,
                        previousStage: 1,
                        kitBagSlot: 0))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(withMaterial: true),
            Character(withMaterial: false),
            [pet],
            executor);

        await fixture.InvokeAsync(Request(operationId, MaterialId, 5));

        Check.True(
            executor.SignSoulContractCount == 1 &&
            executor.SignSoulContractEnvelope is { } envelope &&
            envelope.Family == CommandFamily.PetSoulContract &&
            envelope.Command.Identity.OperationId == operationId &&
            envelope.Command.MaterialTemplateId == MaterialId &&
            envelope.Command.Quantity == 5,
            "Soul Contract reaches one identity-bound durable command");
        var opcodes = fixture.Transport.ReadLegacyPackets()
            .Select(ReadOpcode)
            .ToArray();
        Check.True(
            opcodes.First() == Opcodes.PetSoulContractResult &&
            opcodes.Count(value =>
                value == Opcodes.PetSoulContractResult) == 1 &&
            !opcodes.Contains((ushort)10237) &&
            !opcodes.Contains(PlayerStatusOpcode) &&
            !opcodes.Contains(PlayerExtendedStatusOpcode) &&
            !opcodes.Contains(PlayerVitalsOpcode),
            "commit sends one narrow 10271 and no pet-list or player-vitals projection");
    }

    private static async Task CheckDelayedDuplicateAsync()
    {
        var operationId = Guid.NewGuid();
        var historical = Pet(6, revision: 12);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            SignSoulContract = _ =>
                PetDurableExecutionResult.Duplicate(
                    Receipt(
                        PetDurableReceiptStatus.PetSoulContractSigned,
                        historical,
                        5,
                        1,
                        0))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(false),
            Character(false),
            [],
            executor);

        await fixture.InvokeAsync(Request(operationId, MaterialId, 5));

        var opcodes = fixture.Transport.ReadLegacyPackets()
            .Select(ReadOpcode)
            .ToArray();
        Check.True(
            !opcodes.Contains(Opcodes.PetSoulContractResult) &&
            !opcodes.Contains((ushort)10237) &&
            fixture.Transport.CommandResults is
            [
                {
                    Disposition: SecureLegacyCommandDisposition.Replayed,
                    CommandFamily: (ushort)CommandFamily.PetSoulContract
                }
            ],
            "delayed duplicate settles without stale stage or pet rebuild");
    }

    private static async Task CheckZeroSpiritRequestAsync()
    {
        var executor = new DelegatingPetDurableCommandExecutor
        {
            SignSoulContract = envelope =>
                PetDurableExecutionResult.Rejected(
                    Receipt(
                        PetDurableReceiptStatus.PetSoulContractInvalidState,
                        Pet(0, revision: 11),
                        quantity: 0,
                        previousStage: 0,
                        kitBagSlot: -1,
                        evidence: false))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(false),
            Character(false),
            [Pet(0, revision: 11)],
            executor);

        await fixture.InvokeAsync(
            Request(Guid.NewGuid(), MaterialId, quantity: 0));

        Check.True(
            executor.SignSoulContractEnvelope is { } envelope &&
            envelope.Command.MaterialTemplateId == MaterialId &&
            envelope.Command.Quantity == 0,
            "stock q0 Soul Contract crosses the exact request boundary");
    }

    private static async Task CheckMalformedRequestAsync()
    {
        var executor = new DelegatingPetDurableCommandExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(false),
            Character(false),
            [Pet(0, revision: 11)],
            executor);
        var malformed = Request(Guid.NewGuid(), 10104, 1).Buffer.ToArray();
        await fixture.InvokeAsync(new GamePacket(malformed, Guid.NewGuid()));
        Check.Equal(
            0,
            executor.SignSoulContractCount,
            "Soul Contract rejects a non-10105 material before execution");
    }

    private static PetDurableReceipt Receipt(
        PetDurableReceiptStatus status,
        PetBootstrapSnapshot pet,
        int quantity,
        byte previousStage,
        int kitBagSlot,
        bool evidence = true) =>
        new(
            CommandFamily.PetSoulContract,
            status,
            AccountId,
            CharacterId,
            kitBagSlot,
            EquipmentSlot: -1,
            pet.PetId,
            pet.Level,
            pet.Experience,
            pet.Revision,
            pet.IsCarried,
            pet.IsSummoned,
            PresenceOperation: 0,
            AggregateRevision: 9,
            AuditReference: "soul-contract-protocol-check",
            OutboxEventId: status ==
                PetDurableReceiptStatus.PetSoulContractSigned
                    ? Guid.NewGuid()
                    : null,
            SoulContract: evidence
                ? new PetSoulContractEvidence(
                    pet.PetId,
                    previousStage,
                    PetSoulContractPolicy.StageForSpiritCount(quantity),
                    MaterialId,
                    checked((byte)quantity),
                    PetSoulContractPolicy.BasicSavvyIncreaseHundredths(
                        PetSoulContractPolicy.StageForSpiritCount(quantity)))
                : null);

    private static GamePacket Request(
        Guid? operationId,
        int material,
        byte quantity)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetSoulContractRequest);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), material);
        packet[8] = quantity;
        return new GamePacket(packet, operationId);
    }

    private static GameCharacter Character(bool withMaterial)
    {
        var bag = withMaterial
            ? KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                "[10105,,,,,,1,1,0,5]")
            : GameDefaults.EmptyKitBag;
        return new GameCharacter
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "test2",
            Profession = 1,
            Equipment = GameDefaults.DefaultEquipment(1),
            KitBag = bag
        };
    }

    private static PetBootstrapSnapshot Pet(byte stage, long revision) =>
        PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision) with
        {
            PetId = PetId,
            AccountId = AccountId,
            OwnerCharacterId = CharacterId,
            HasSoulContract = stage > 0,
            SoulContractStage = stage
        };

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
}
