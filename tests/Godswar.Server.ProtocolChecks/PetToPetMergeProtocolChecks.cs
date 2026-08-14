using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetToPetMergeProtocolChecks
{
    private static readonly Guid OperationId =
        Guid.Parse("2c7d20d8-1009-4b28-b139-0620a96d8277");
    private static readonly PetToPetMergeDelta Delta =
        new(395, 400, 405, 410, 415, 420, Rank: 325);

    public static async Task RunAsync()
    {
        CheckResultPacket();
        CheckCommandEnvelopeAndReceipt();
        await CheckCommittedProjectionAsync();
        await CheckNoSpiritRequestAsync();
        await CheckDuplicateProjectionAsync();
        await CheckDuplicateDuringOwnerMergeAsync();
        await CheckRawRequestIdentityAsync();
        await CheckMalformedRequestAsync();
    }

    private static void CheckResultPacket()
    {
        var expected = Convert.FromHexString(
            "26001D2811000000120000008B01000090010000950100009A0100009F010000A40100004501");
        Check.True(
            PacketBuilder.PetToPetMergeResult(17, 18, Delta)
                .SequenceEqual(expected),
            "pet Merge preserves the exact 38-byte opcode 10269 layout");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.PetToPetMergeResult(17, 17, Delta),
            "pet Merge result rejects one pet in both roles");
        var zeroRow = Delta with { Agility = 0 };
        Check.True(
            PacketBuilder.PetToPetMergeResult(17, 18, zeroRow)
                .AsSpan(12, sizeof(int)).SequenceEqual(new byte[sizeof(int)]),
            "pet Merge result carries an independently ineligible zero row");
        var allZero = new PetToPetMergeDelta(0, 0, 0, 0, 0, 0, 0);
        Check.True(
            PacketBuilder.PetToPetMergeResult(17, 18, allZero).Length == 38,
            "native Merge result permits an all-zero additive outcome");
    }

    private static void CheckCommandEnvelopeAndReceipt()
    {
        var subject = new CommandSubject(
            PetEggHatchProtocolChecks.AccountId,
            PetEggHatchProtocolChecks.CharacterId);
        var secureConnection = new CommandConnectionCorrelation(
            Guid.Parse("90d4ac4c-c23c-43c0-80d7-75a96ca4dfa9"),
            CommandTransportKind.SecureTlsLegacy);
        var command = new PetToPetMergeCommand(
            PetCommandOperationIdentity.SecureClient(OperationId),
            17,
            18,
            PetToPetMergeCommandEnvelope.StandardMaterialItemId,
            5);
        var envelope = PetToPetMergeCommandEnvelope.Create(
            subject,
            secureConnection,
            DateTimeOffset.Parse("2026-08-11T01:02:03Z"),
            command);
        Check.True(
            PetToPetMergeCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid,
            "secure pet Merge envelope validates");
        Check.True(
            envelope.Family == CommandFamily.PetToPetMerge,
            "pet Merge owns command family 49");

        var changed = PetToPetMergeCommandEnvelope.Create(
            subject,
            secureConnection,
            envelope.ReceivedAt,
            command with { MaterialQuantity = 4 });
        Check.True(
            !string.Equals(
                envelope.RequestHash,
                changed.RequestHash,
                StringComparison.Ordinal),
            "pet Merge request hash includes material quantity");

        var noSpirit = PetToPetMergeCommandEnvelope.Create(
            subject,
            secureConnection,
            envelope.ReceivedAt,
            command with { MaterialItemId = 0, MaterialQuantity = 0 });
        Check.True(
            PetToPetMergeCommandEnvelope.Validate(noSpirit) ==
                CommandEnvelopeValidation.Valid &&
            !string.Equals(
                envelope.RequestHash,
                noSpirit.RequestHash,
                StringComparison.Ordinal),
            "exact no-material Merge is valid and canonically distinct from a spirit request");
        Check.True(
            PetToPetMergeCommandEnvelope.Validate(
                PetToPetMergeCommandEnvelope.Create(
                    subject,
                    secureConnection,
                    envelope.ReceivedAt,
                    command with
                    {
                        MaterialItemId = 0,
                        MaterialQuantity = 1
                    })) == CommandEnvelopeValidation.InvalidCommand &&
            PetToPetMergeCommandEnvelope.Validate(
                PetToPetMergeCommandEnvelope.Create(
                    subject,
                    secureConnection,
                    envelope.ReceivedAt,
                    command with { MaterialQuantity = 0 })) ==
                CommandEnvelopeValidation.InvalidCommand,
            "mixed no-material and spirit identifiers fail envelope validation");

        var rawConnection = new CommandConnectionCorrelation(
            Guid.Parse("21a371d3-1360-4b5a-8ebf-f392a4357877"),
            CommandTransportKind.LegacyTcp);
        var raw = PetToPetMergeCommandEnvelope.CreateRawLocal(
            subject,
            rawConnection,
            envelope.ReceivedAt,
            command with
            {
                Identity = PetCommandOperationIdentity.RawLocalServer(
                    Guid.Parse("061c01ca-7807-4244-aa4f-a5b04603195f"),
                    rawConnection.ConnectionId)
            });
        Check.True(
            PetToPetMergeCommandEnvelope.Validate(raw) ==
                CommandEnvelopeValidation.Valid,
            "raw-local pet Merge receives server operation identity");

        var receipt = SuccessReceipt(
            PetDurableExecutionDisposition.Committed);
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.Decode(payload);
        Check.Equal(
            PetDurablePersistenceCodec.PetToPetMergeContractVersion,
            PetDurablePersistenceCodec.ContractVersionFor(
                CommandFamily.PetToPetMerge),
            "pet Merge durable receipt uses contract v2");
        Check.Equal(
            receipt,
            decoded,
            "pet Merge durable receipt preserves deputy and exact gains");
    }

    private static async Task CheckCommittedProjectionAsync()
    {
        var live = CharacterWithMaterial(stack: 5);
        var persisted = CharacterWithMaterial(stack: 0);
        var primary = CreatePet(17, revision: 8);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            MergePets = _ => PetDurableExecutionResult.Committed(
                SuccessReceipt(
                    PetDurableExecutionDisposition.Committed,
                    primary))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            live,
            persisted,
            [primary],
            executor);

        await fixture.InvokeAsync(CreateRequest(OperationId));

        var packets = fixture.Transport.ReadLegacyPackets();
        var merge = packets.Single(packet =>
            ReadOpcode(packet) == Opcodes.PetToPetMergeResult);
        Check.True(
            merge.SequenceEqual(
                PacketBuilder.PetToPetMergeResult(17, 18, Delta)),
            "fresh committed Merge emits its exact durable random result");
        Check.Equal(
            0,
            packets.Count(packet => ReadOpcode(packet) == 10_237),
            "fresh Merge does not rebuild native carry state");
        Check.True(
            packets.Any(packet => ReadOpcode(packet) == 0x2731),
            "fresh Merge refreshes authoritative material stacks");
        Check.True(
            executor.MergePetsEnvelope is { } envelope &&
            executor.MergePetsCount == 1 &&
            envelope.IdentityStrength ==
                CommandIdentityStrength.ClientOperationId &&
            envelope.Command.Identity.OperationId == OperationId &&
            envelope.Command.PrimaryPetId == 17 &&
            envelope.Command.DeputyPetId == 18 &&
            envelope.Command.MaterialItemId ==
                PetToPetMergeCommandEnvelope.StandardMaterialItemId &&
            envelope.Command.MaterialQuantity == 5,
            "secure handler forwards only captured IDs, template, and count");
    }

    private static async Task CheckDuplicateProjectionAsync()
    {
        var primary = CreatePet(17, revision: 8);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            MergePets = _ => PetDurableExecutionResult.Duplicate(
                SuccessReceipt(
                    PetDurableExecutionDisposition.Duplicate,
                    primary))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            CharacterWithMaterial(stack: 5),
            CharacterWithMaterial(stack: 0),
            [primary],
            executor);

        await fixture.InvokeAsync(CreateRequest(OperationId));

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            0,
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.PetToPetMergeResult),
            "duplicate Merge never reapplies additive opcode 10269");
        Check.Equal(
            1,
            packets.Count(packet => ReadOpcode(packet) == 10_237),
            "duplicate Merge reconciles through one authoritative pet list");
        Check.True(
            packets.Any(packet => ReadOpcode(packet) == 0x2731),
            "duplicate Merge reconciles its authoritative bag");
    }

    private static async Task CheckNoSpiritRequestAsync()
    {
        var executor = new DelegatingPetDurableCommandExecutor
        {
            MergePets = envelope => PetDurableExecutionResult.Rejected(
                RejectedReceipt(envelope))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            CharacterWithMaterial(stack: 0),
            CharacterWithMaterial(stack: 0),
            [CreatePet(17, revision: 7)],
            executor);

        await fixture.InvokeAsync(CreateRequest(
            OperationId,
            materialItemId: 0,
            materialQuantity: 0));

        Check.True(
            executor.MergePetsEnvelope is { } envelope &&
            executor.MergePetsCount == 1 &&
            envelope.Command.MaterialItemId == 0 &&
            envelope.Command.MaterialQuantity == 0,
            "native no-spirit Merge reaches the durable command unchanged");
    }

    private static async Task CheckRawRequestIdentityAsync()
    {
        var primary = CreatePet(17, revision: 7);
        var deputy = CreatePet(18, revision: 3) with
        {
            IsCarried = false,
            IsSummoned = false
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            MergePets = envelope => PetDurableExecutionResult.Rejected(
                RejectedReceipt(envelope))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(
            CharacterWithMaterial(stack: 5),
            CharacterWithMaterial(stack: 5),
            [primary, deputy],
            executor,
            hasLocalDevelopmentCapability: true);

        await fixture.InvokeAsync(CreateRequest(operationId: null));

        Check.True(
            executor.MergePetsEnvelope is { } envelope &&
            envelope.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId &&
            envelope.Connection.Transport == CommandTransportKind.LegacyTcp &&
            envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.RawLocalConnectionId ==
                envelope.Connection.ConnectionId,
            "raw-local pet Merge receives a connection-bound server identity");
    }

    private static async Task CheckDuplicateDuringOwnerMergeAsync()
    {
        var primary = CreatePet(17, revision: 9) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = true
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            MergePets = _ => PetDurableExecutionResult.Duplicate(
                SuccessReceipt(
                    PetDurableExecutionDisposition.Duplicate,
                    primary))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            CharacterWithMaterial(stack: 5),
            CharacterWithMaterial(stack: 0),
            [primary],
            executor);

        await fixture.InvokeAsync(CreateRequest(OperationId));

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            0,
            packets.Count(packet =>
                ReadOpcode(packet) is
                    Opcodes.PetToPetMergeResult or 10_237),
            "delayed Merge retry cannot add gains or rebuild an active owner Merge");
        Check.True(
            packets.Any(packet => ReadOpcode(packet) == 0x2731),
            "delayed Merge retry still reconciles consumed bag materials");
    }

    private static async Task CheckMalformedRequestAsync()
    {
        var executor = new DelegatingPetDurableCommandExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            CharacterWithMaterial(stack: 5),
            CharacterWithMaterial(stack: 5),
            [CreatePet(17, revision: 7)],
            executor);
        var packet = CreateRequest(OperationId);
        packet.Buffer[19] = 1;

        await fixture.InvokeAsync(packet);

        Check.Equal(
            0,
            executor.MergePetsCount,
            "nonzero pet Merge tail padding is rejected before persistence");

        await fixture.InvokeAsync(CreateRequest(
            OperationId,
            materialItemId: 0,
            materialQuantity: 1));
        await fixture.InvokeAsync(CreateRequest(
            OperationId,
            materialItemId:
                PetToPetMergeCommandEnvelope.StandardMaterialItemId,
            materialQuantity: 0));
        Check.Equal(
            0,
            executor.MergePetsCount,
            "mixed zero-template/material-count Merge shapes are rejected");
    }

    private static PetDurableReceipt SuccessReceipt(
        PetDurableExecutionDisposition disposition,
        PetBootstrapSnapshot? pet = null)
    {
        _ = disposition;
        pet ??= CreatePet(17, revision: 8);
        return new PetDurableReceipt(
            CommandFamily.PetToPetMerge,
            PetDurableReceiptStatus.PetToPetMerged,
            PetEggHatchProtocolChecks.AccountId,
            PetEggHatchProtocolChecks.CharacterId,
            KitBagSlot: 25,
            EquipmentSlot: -1,
            PetId: 17,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: pet.Revision,
            pet.IsCarried,
            pet.IsSummoned,
            PresenceOperation: 0,
            AggregateRevision: 1,
            AuditReference: "pet-to-pet-merge-check",
            OutboxEventId: Guid.Parse(
                "e255e45c-ef55-44bb-951c-874cb03d6716"),
            DeputyPetId: 18,
            PetMergeDelta: Delta);
    }

    private static PetDurableReceipt RejectedReceipt(
        CommandEnvelope<PetToPetMergeCommand> envelope) =>
        new(
            CommandFamily.PetToPetMerge,
            PetDurableReceiptStatus.PetMergeLevelTooLow,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            KitBagSlot: -1,
            EquipmentSlot: -1,
            PetId: envelope.Command.PrimaryPetId,
            PetLevel: 1,
            PetExperience: 0,
            PetRevision: 7,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 0,
            AggregateRevision: 0,
            AuditReference: "pet-to-pet-merge-rejected-check",
            OutboxEventId: null,
            DeputyPetId: envelope.Command.DeputyPetId);

    private static PetBootstrapSnapshot CreatePet(long petId, long revision)
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
            PetId = petId,
            Level = 30,
            IsCarried = petId == 17,
            IsSummoned = petId == 17,
            Revision = revision
        };
    }

    private static GameCharacter CharacterWithMaterial(int stack)
    {
        var bag = GameDefaults.EmptyKitBag;
        if (stack > 0)
        {
            var item = CompactItemEntry.Parse(
                $"[{PetToPetMergeCommandEnvelope.StandardMaterialItemId},,,,,,0,1,1,{stack},0,0]");
            bag = KitBagSlots.SetSlot(bag, 25, item.ToCompactString());
        }
        return new GameCharacter
        {
            Id = PetEggHatchProtocolChecks.CharacterId,
            AccountId = PetEggHatchProtocolChecks.AccountId,
            Name = "test2",
            KitBag = bag,
            Equipment = GameDefaults.DefaultEquipment(1)
        };
    }

    private static GamePacket CreateRequest(
        Guid? operationId,
        uint materialItemId =
            PetToPetMergeCommandEnvelope.StandardMaterialItemId,
        byte materialQuantity = 5)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetToPetMergeRequest);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), 17);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), 18);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            checked((int)materialItemId));
        packet[16] = materialQuantity;
        return new GamePacket(packet, operationId);
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
}
