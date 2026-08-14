using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetRebirthProtocolChecks
{
    private const int AccountId = 13;
    private const int CharacterId = 2;
    private const long PetId = 71;
    private const int MaterialId = 10104;
    private const int BagSlot = 0;
    private static readonly PetRebirthGrowthEvidence GrowthIncrease = new(
        new PetContentStatVector(
            0.10m,
            0.11m,
            0.12m,
            0.13m,
            0.14m,
            0.20m));

    public static async Task RunAsync()
    {
        CheckExactWireResult();
        await CheckCommittedProjectionAsync();
        await CheckCommittedProjectionRequiresGrowthEvidenceAsync();
        await CheckDuplicateProjectionAsync();
        await CheckRejectedProjectionAsync();
        await CheckZeroSpiritRequestShapesAsync();
        await CheckMalformedRequestBoundaryAsync();
        await CheckTokenlessSecureRequestAsync();
    }

    private static void CheckExactWireResult()
    {
        Check.Equal(
            (ushort)10272,
            Opcodes.PetRebirthRequest,
            "native pet rebirth request opcode");
        Check.Equal(
            (ushort)10273,
            Opcodes.PetRebirthResult,
            "native pet rebirth result opcode");
        var packet = PacketBuilder.PetRebirth(
            GrowthIncrease,
            12_345);
        Check.Equal(16, packet.Length, "rebirth result exact length");
        Check.Equal(
            (ushort)16,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "rebirth result header length");
        Check.Equal(
            Opcodes.PetRebirthResult,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "rebirth result header opcode");
        Check.True(
            packet.AsSpan(4, 6).SequenceEqual(
                new byte[] { 10, 11, 12, 13, 14, 20 }),
            "rebirth result carries six hundredth-unit Growth rolls in native stat order");
        Check.True(
            packet.AsSpan(10, 2).SequenceEqual(new byte[2]),
            "rebirth result reserved bytes remain zero");
        Check.Equal(
            12_345,
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12)),
            "rebirth result next-level requirement offset");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.PetRebirth(
                GrowthIncrease with
                {
                    Increase = GrowthIncrease.Increase with
                    {
                        Agility = 0m
                    }
                },
                1_500),
            "rebirth result rejects a zero Growth increase");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.PetRebirth(
                GrowthIncrease with
                {
                    Increase = GrowthIncrease.Increase with
                    {
                        Luck = 0.111m
                    }
                },
                1_500),
            "rebirth result rejects sub-hundredth Growth evidence");
    }

    private static async Task CheckCommittedProjectionAsync()
    {
        var operationId = Guid.NewGuid();
        var live = Character(WithMaterial: true);
        var persisted = Character(WithMaterial: false);
        var pet = RebornPet(revision: 12) with
        {
            Experience = 242_980_800L
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            RebirthPet = envelope =>
                PetDurableExecutionResult.Committed(
                    SuccessfulReceipt(envelope, pet))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            live,
            persisted,
            [pet],
            executor);

        await fixture.InvokeAsync(CreateRequest(operationId));

        Check.True(
            executor.RebirthPetCount == 1 &&
            executor.RebirthPetEnvelope is { } envelope &&
            envelope.Family == CommandFamily.PetRebirth &&
            envelope.Command.Identity.IsSecureClient &&
            envelope.Command.Identity.OperationId == operationId &&
            envelope.Command.MaterialTemplateId == MaterialId &&
            envelope.Command.Quantity == 5,
            "rebirth request reaches one identity-bound durable command");
        var packets = fixture.Transport.ReadLegacyPackets();
        var opcodes = packets.Select(ReadOpcode).ToArray();
        Check.Equal(
            Opcodes.PetRebirthResult,
            opcodes[0],
            "committed rebirth emits non-idempotent 10273 first");
        Check.Equal(
            1,
            opcodes.Count(opcode => opcode == Opcodes.PetRebirthResult),
            "committed rebirth emits 10273 exactly once");
        Check.True(
            opcodes.Contains(Opcodes.PetLevelUpgrade) &&
            Array.IndexOf(opcodes, Opcodes.PetLevelUpgrade) > 0 &&
            !opcodes.Contains((ushort)10237),
            "committed rebirth follows 10273 with narrow pet refresh only");
        Check.True(
            packets[0].SequenceEqual(PacketBuilder.PetRebirth(
                GrowthIncrease,
                1_500)),
            "10273 carries the exact committed Growth roll and reset pet next-level requirement");
        var progression = packets.Single(packet =>
            ReadOpcode(packet) == Opcodes.PetLevelUpgrade);
        Check.Equal(
            242_980_800u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                progression.AsSpan(12)),
            "following progression refresh carries the current EXP pool");
        Check.Equal(
            1_500u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                progression.AsSpan(16)),
            "following progression refresh carries the next-level cost");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition: SecureLegacyCommandDisposition.Applied,
                    CommandFamily: (ushort)CommandFamily.PetRebirth,
                    OperationId: var completed
                }
            ] && completed == operationId,
            "committed secure rebirth completes its exact operation");
    }

    private static async Task CheckDuplicateProjectionAsync()
    {
        var operationId = Guid.NewGuid();
        var character = Character(WithMaterial: false);
        var historicalPet = RebornPet(revision: 12);
        var currentPet = RebornPet(revision: 20) with
        {
            PetId = PetId + 1,
            CompletedRebirths = 0,
            RebirthsRemaining = 1
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            RebirthPet = envelope =>
                PetDurableExecutionResult.Duplicate(
                    SuccessfulReceipt(envelope, historicalPet) with
                    {
                        // Contract-v1 receipts predate exact roll evidence.
                        RebirthGrowth = null
                    })
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [currentPet],
            executor);

        await fixture.InvokeAsync(CreateRequest(operationId));

        var opcodes = fixture.Transport.ReadLegacyPackets()
            .Select(ReadOpcode)
            .ToArray();
        Check.Equal(
            0,
            opcodes.Count(opcode =>
                opcode == Opcodes.PetRebirthResult ||
                opcode == Opcodes.PetLevelUpgrade ||
                opcode == 10237),
            "delayed duplicate after a pet switch sends no historical pet mutation or destructive 10237");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition: SecureLegacyCommandDisposition.Replayed,
                    CommandFamily: (ushort)CommandFamily.PetRebirth
                }
            ],
            "duplicate secure rebirth is reported as a replay");
    }

    private static async Task
        CheckCommittedProjectionRequiresGrowthEvidenceAsync()
    {
        var operationId = Guid.NewGuid();
        var pet = RebornPet(revision: 12);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            RebirthPet = envelope =>
                PetDurableExecutionResult.Committed(
                    SuccessfulReceipt(envelope, pet) with
                    {
                        RebirthGrowth = null
                    })
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(WithMaterial: true),
            Character(WithMaterial: false),
            [pet],
            executor);

        await fixture.InvokeAsync(CreateRequest(operationId));

        Check.Equal(
            0,
            fixture.Transport.ReadLegacyPackets().Count(packet =>
                ReadOpcode(packet) == Opcodes.PetRebirthResult),
            "committed rebirth fails closed when exact roll evidence is absent");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "an unprojectable committed rebirth is not acknowledged as applied");
    }

    private static async Task CheckRejectedProjectionAsync()
    {
        var executor = RejectingExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(false),
            Character(false),
            [RebornPet(12)],
            executor);

        await fixture.InvokeAsync(CreateRequest(Guid.NewGuid()));

        Check.Equal(
            0,
            fixture.Transport.ReadLegacyPackets().Count(packet =>
                ReadOpcode(packet) == Opcodes.PetRebirthResult),
            "rejected rebirth never emits additive 10273");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition: SecureLegacyCommandDisposition.Rejected,
                    CommandFamily: (ushort)CommandFamily.PetRebirth
                }
            ],
            "rejected secure rebirth receives only its terminal result");
    }

    private static async Task CheckMalformedRequestBoundaryAsync()
    {
        var executor = RejectingExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(false),
            Character(false),
            [RebornPet(12)],
            executor);

        var malformed = CreateRequest(Guid.NewGuid()).Buffer.ToArray();
        malformed[9] = 1;
        await fixture.InvokeAsync(
            new GamePacket(malformed, Guid.NewGuid()));

        Check.Equal(
            0,
            executor.RebirthPetCount,
            "rebirth rejects nonzero reserved request bytes");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "malformed rebirth cannot fabricate a durable result");
    }

    private static async Task CheckZeroSpiritRequestShapesAsync()
    {
        var canonical = new[] { 0, 10104, 10098 }
            .Select(material => Convert.ToHexString(
                PetRebirthCommandContract.CanonicalRequest(
                    material,
                    quantity: 0)))
            .ToArray();
        Check.Equal(
            3,
            canonical.Distinct(StringComparer.Ordinal).Count(),
            "q0 rebirth request hash retains each native material shape");

        foreach (var material in new[] { 0, 10104, 10098 })
        {
            var executor = RejectingExecutor();
            await using var fixture = PetDurableHandlerFixture.Create(
                Character(false),
                Character(false),
                [RebornPet(12)],
                executor);
            await fixture.InvokeAsync(
                CreateRequest(Guid.NewGuid(), material, quantity: 0));
            Check.True(
                executor.RebirthPetEnvelope is { } envelope &&
                envelope.Command.MaterialTemplateId == material &&
                envelope.Command.Quantity == 0,
                $"q0 rebirth material {material} crosses the boundary");
        }

        var rejectedExecutor = RejectingExecutor();
        await using var rejectedFixture = PetDurableHandlerFixture.Create(
            Character(false),
            Character(false),
            [RebornPet(12)],
            rejectedExecutor);
        await rejectedFixture.InvokeAsync(
            CreateRequest(Guid.NewGuid(), material: 12345, quantity: 0));
        Check.Equal(
            0,
            rejectedExecutor.RebirthPetCount,
            "q0 rebirth rejects an unreviewed retained material");
    }

    private static async Task CheckTokenlessSecureRequestAsync()
    {
        var executor = RejectingExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            Character(false),
            Character(false),
            [RebornPet(12)],
            executor);

        await fixture.InvokeAsync(CreateRequest(operationId: null));

        Check.Equal(
            0,
            executor.RebirthPetCount,
            "secure rebirth cannot downgrade to a generated identity");
    }

    private static DelegatingPetDurableCommandExecutor RejectingExecutor() =>
        new()
        {
            RebirthPet = envelope => PetDurableExecutionResult.Rejected(
                new PetDurableReceipt(
                    CommandFamily.PetRebirth,
                    PetDurableReceiptStatus.PetRebirthInvalidMaterial,
                    envelope.Subject.AccountId,
                    envelope.Subject.CharacterId,
                    KitBagSlot: -1,
                    EquipmentSlot: -1,
                    PetId,
                    PetLevel: 1,
                    PetExperience: 0,
                    PetRevision: 12,
                    IsCarried: true,
                    IsSummoned: true,
                    PresenceOperation: 0,
                    AggregateRevision: 1,
                    AuditReference: "rebirth-rejection-check",
                    OutboxEventId: null))
        };

    private static PetDurableReceipt SuccessfulReceipt(
        CommandEnvelope<PetRebirthCommand> envelope,
        PetBootstrapSnapshot pet) =>
        new(
            CommandFamily.PetRebirth,
            PetDurableReceiptStatus.PetReborn,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            BagSlot,
            EquipmentSlot: -1,
            pet.PetId,
            pet.Level,
            pet.Experience,
            pet.Revision,
            pet.IsCarried,
            pet.IsSummoned,
            PresenceOperation: 0,
            AggregateRevision: 9,
            AuditReference: "rebirth-projection-check",
            OutboxEventId: Guid.NewGuid(),
            RebirthGrowth: GrowthIncrease);

    private static GamePacket CreateRequest(
        Guid? operationId,
        int material = MaterialId,
        byte quantity = 5)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetRebirthRequest);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            material);
        packet[8] = quantity;
        return new GamePacket(packet, operationId);
    }

    private static GameCharacter Character(bool WithMaterial)
    {
        var bag = WithMaterial
            ? KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                BagSlot,
                "[10104,,,,,,1,1,0,5]")
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

    private static PetBootstrapSnapshot RebornPet(long revision) =>
        PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision) with
        {
            PetId = PetId,
            AccountId = AccountId,
            OwnerCharacterId = CharacterId,
            Level = 1,
            Experience = 0,
            CompletedRebirths = 1,
            RebirthsRemaining = 0,
            HasSoulContract = true
        };

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
}
