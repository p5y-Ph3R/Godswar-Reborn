using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetManagerUtilityHandlerChecks
{
    private static async Task InvokeNpcAsync(
        GameClientHandler handler,
        int subId,
        int argument0)
    {
        var arguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        arguments[0] = argument0;
        arguments[10] = 0x04CB_1074;
        arguments[11] = unchecked((int)0x8C35_0102);
        arguments[12] = int.MinValue;
        var task = HandlePetManagerMethod.Invoke(
            handler,
            [
                new GamePacket(NpcPacket(subId, arguments)),
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                subId,
                arguments,
                CancellationToken.None
            ]) as Task ?? throw new InvalidOperationException(
                "Pet Manager utility handler returned no task.");
        await task;
    }

    private static byte[] NpcPacket(int subId, IReadOnlyList<int> arguments)
    {
        var bytes = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2), Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4), PetManagerProtocol.AthensNpcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8), PetManagerProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12), PetManagerProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        for (var index = 0; index < arguments.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + index * sizeof(int)),
                arguments[index]);
        }
        return bytes;
    }

    private static GamePacket BreakItemPacket(int slot, Guid operationId)
    {
        var bytes = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2), Opcodes.BreakItem);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(12), checked((ushort)(slot / 24)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(14), checked((ushort)(slot % 24)));
        return new GamePacket(bytes, operationId);
    }

    private static GamePacket PackedDetailPacket(long petId)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2), Opcodes.PackedPetDetailRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4), checked((uint)petId));
        return new GamePacket(bytes);
    }

    private static PetDurableReceipt SuccessReceipt(
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        PetBootstrapSnapshot pet,
        PetManagerUtilityOperation operation,
        int sealSlot = PackedSlot)
    {
        var before = PetState(pet);
        var status = operation switch
        {
            PetManagerUtilityOperation.CheckGrowth =>
                PetDurableReceiptStatus.PetGrowthChecked,
            PetManagerUtilityOperation.Seal =>
                PetDurableReceiptStatus.PetSealed,
            PetManagerUtilityOperation.Unseal =>
                PetDurableReceiptStatus.PetUnsealed,
            PetManagerUtilityOperation.ClaimPetCall =>
                PetDurableReceiptStatus.PetCallClaimed,
            PetManagerUtilityOperation.ClaimMerge =>
                PetDurableReceiptStatus.PetMergeClaimed,
            PetManagerUtilityOperation.ChangeGender =>
                PetDurableReceiptStatus.PetGenderChanged,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var itemTemplate = operation switch
        {
            PetManagerUtilityOperation.CheckGrowth => 10106,
            PetManagerUtilityOperation.Seal or
                PetManagerUtilityOperation.Unseal => 10109,
            PetManagerUtilityOperation.ClaimPetCall => 11003,
            PetManagerUtilityOperation.ClaimMerge => 11004,
            PetManagerUtilityOperation.ChangeGender => 11015,
            _ => 0
        };
        var isClaim = operation is PetManagerUtilityOperation.ClaimPetCall or
            PetManagerUtilityOperation.ClaimMerge;
        var after = operation switch
        {
            PetManagerUtilityOperation.CheckGrowth => before with
            {
                GrowthRevealed = true,
                Revision = pet.Revision + 1
            },
            PetManagerUtilityOperation.Seal => before with
            {
                ActivityState = "sealed",
                IsCarried = false,
                IsSummoned = false,
                HasSoulContract = false,
                SoulContractStage = 0,
                Revision = pet.Revision + 1
            },
            PetManagerUtilityOperation.Unseal => before with
            {
                ActivityState = "owned",
                IsCarried = true,
                IsSummoned = true,
                Revision = pet.Revision + 1
            },
            PetManagerUtilityOperation.ChangeGender => before with
            {
                Sex = checked((byte)(1 - pet.Sex)),
                Revision = pet.Revision + 1
            },
            _ => null
        };
        var evidence = new PetManagerUtilityEvidence(
            operation,
            isClaim ? 0 : pet.PetId,
            itemTemplate,
            ItemInstanceId: 9001,
            KitBagSlot: operation == PetManagerUtilityOperation.Seal
                ? sealSlot
                : MaterialSlot,
            PreviousSex: pet.Sex,
            NewSex: operation == PetManagerUtilityOperation.ChangeGender
                ? checked((byte)(1 - pet.Sex))
                : pet.Sex,
            Growth: operation == PetManagerUtilityOperation.CheckGrowth
                ? new PetManagerGrowthEvidence(1, 2, 3, 4, 5, 6)
                : null,
            BeforePetState: isClaim ? null : before,
            AfterPetState: after);
        return new PetDurableReceipt(
            CommandFamily.PetManagerUtility,
            status,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            evidence.KitBagSlot,
            EquipmentSlot: -1,
            evidence.PetId,
            PetLevel: isClaim ? (short)0 : pet.Level,
            PetExperience: isClaim ? 0 : pet.Experience,
            PetRevision: isClaim ? 0 : pet.Revision + 1,
            IsCarried: operation is
                PetManagerUtilityOperation.CheckGrowth or
                PetManagerUtilityOperation.Unseal or
                PetManagerUtilityOperation.ChangeGender,
            IsSummoned: operation is
                PetManagerUtilityOperation.CheckGrowth or
                PetManagerUtilityOperation.Unseal or
                PetManagerUtilityOperation.ChangeGender,
            PresenceOperation: 0,
            AggregateRevision: 1,
            AuditReference: "utility-handler",
            OutboxEventId: Guid.NewGuid(),
            PetManagerUtility: evidence);
    }

    private static PetManagerUtilityPetState PetState(
        PetBootstrapSnapshot pet) =>
        new(
            pet.ActivityState,
            pet.IsCarried,
            pet.IsSummoned,
            pet.ContributesToCharacter,
            pet.GrowthRevealed,
            pet.HasSoulContract,
            pet.SoulContractStage,
            pet.Sex,
            pet.Revision)
        {
            CurrentEnergy = pet.CurrentEnergy,
            MaximumEnergy = pet.MaximumEnergy
        };

    private static GameCharacter CharacterWithItem(
        uint itemId,
        int slot,
        long linkedPetId = 0,
        short bound = 0)
    {
        var bag = GameDefaults.EmptyKitBag;
        if (itemId != 0)
        {
            var item = CompactItemEntry.Empty with
            {
                Id = itemId,
                Quality = 1,
                Grade = 1,
                Bound = bound,
                Stack = 1,
                LinkedSealedPetId = linkedPetId
            };
            bag = KitBagSlots.SetSlot(bag, slot, item.ToCompactString());
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

    private static PetBootstrapSnapshot CreatePet(long revision)
    {
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly, 50m, new Random(50));
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly, 3_500, new Random(3_500));
        return PetEggHatchProtocolChecks.CreatePet(savvy, growth) with
        {
            Name = "Utility Pet",
            IsBound = true,
            ActivityState = "owned",
            IsCarried = true,
            IsSummoned = true,
            ContributesToCharacter = false,
            Revision = revision
        };
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));

    private sealed class DetailReader(CharacterPetSnapshot authorized) :
        ISealedPetSnapshotReader
    {
        public int LastAccountId { get; private set; }
        public int LastCharacterId { get; private set; }

        public Task<CharacterPetSnapshot?> ReadAuthorizedSealedPetAsync(
            int accountId,
            int characterId,
            long petId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAccountId = accountId;
            LastCharacterId = characterId;
            return Task.FromResult<CharacterPetSnapshot?>(
                petId == authorized.PetId &&
                accountId == authorized.AccountId &&
                characterId == authorized.OwnerCharacterId
                    ? authorized
                    : null);
        }
    }
}
