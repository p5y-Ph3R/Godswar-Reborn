using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetManagerUtilityProtocolChecks
{
    public const string CheckName =
        "Durable Pet Manager utility wire and evidence";

    public static Task RunAsync()
    {
        CheckExactRequestShapes();
        CheckGrowthPage();
        CheckResultMappings();
        CheckEvidenceCodecAndStageSixSeal();
        CheckEvidenceCodecAndAutoPresenceUnseal();
        CheckGenderRefresh();
        CheckPackedSealProjection();
        return Task.CompletedTask;
    }

    private static void CheckExactRequestShapes()
    {
        (int SubId, int Argument0, PetManagerUtilityRequestOperation
            Operation)[] cases =
        [
            (4, 104, PetManagerUtilityRequestOperation.CheckGrowth),
            (5, 105, PetManagerUtilityRequestOperation.Seal),
            (9, -1, PetManagerUtilityRequestOperation.ClaimPetCall),
            (10, -1, PetManagerUtilityRequestOperation.ClaimMerge),
            (11, 0, PetManagerUtilityRequestOperation.ChangeGender)
        ];
        foreach (var (subId, argument0, expected) in cases)
        {
            var arguments = UtilityArguments(argument0);
            Check.True(
                PetManagerProtocol.TryResolveUtilityMutation(
                    PetManagerProtocol.DialogIndex,
                    subId,
                    arguments,
                    out var actual) && actual == expected,
                $"utility root {subId} accepts proven scratch args 10-12");
            arguments[9] = 0;
            Check.True(
                !PetManagerProtocol.TryResolveUtilityMutation(
                    PetManagerProtocol.DialogIndex,
                    subId,
                    arguments,
                    out _),
                $"utility root {subId} rejects unproven padding");
        }

        var preview = UtilityArguments(-1);
        Check.True(
            PetManagerProtocol.IsGenderPreviewRequest(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.ChangeGenderMenuSubId,
                preview),
            "gender preview is the exact nonmutating root-11 shape");
        preview[13] = 0;
        Check.True(
            !PetManagerProtocol.IsGenderPreviewRequest(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.ChangeGenderMenuSubId,
                preview),
            "gender preview rejects padding outside native scratch fields");
    }

    private static int[] UtilityArguments(int argument0)
    {
        var arguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        arguments[0] = argument0;
        arguments[10] = unchecked((int)0x8C35_0102);
        arguments[11] = 0x04CB_1074;
        arguments[12] = int.MinValue;
        return arguments;
    }

    private static void CheckGrowthPage()
    {
        var result = PetManagerProtocol.BuildGrowthCheckSuccessPage(
            petId: 71,
            [1.239m, 2.349m, 3.459m, 4.569m, 5.679m, 6.789m]);
        Check.True(
            result.SequenceEqual(
                [71_001, 123_002, 234_003, 345_004,
                    456_005, 567_006, 678_007, 1_071]),
            "Growth check truncates effective rates to native hundredths");
        Check.Throws<ArgumentException>(
            () => PetManagerProtocol.BuildGrowthCheckSuccessPage(
                71,
                [1m, 2m]),
            "Growth check rejects an incomplete six-stat page");
    }

    private static void CheckResultMappings()
    {
        (PetManagerUtilityOperation Operation,
            PetDurableReceiptStatus Status, uint NativeResult)[] cases =
        [
            (PetManagerUtilityOperation.CheckGrowth,
                PetDurableReceiptStatus.PetManagerMaterialNotFound, 1041),
            (PetManagerUtilityOperation.Seal,
                PetDurableReceiptStatus.PetManagerBagFull, 1052),
            (PetManagerUtilityOperation.Seal,
                PetDurableReceiptStatus.PetManagerPetBound, 1072),
            (PetManagerUtilityOperation.ClaimPetCall,
                PetDurableReceiptStatus.PetManagerClaimAlreadyHeld, 10001),
            (PetManagerUtilityOperation.ClaimMerge,
                PetDurableReceiptStatus.PetManagerBagFull, 10000),
            (PetManagerUtilityOperation.ChangeGender,
                PetDurableReceiptStatus.PetManagerGenderPetUnbound, 150),
            (PetManagerUtilityOperation.ChangeGender,
                PetDurableReceiptStatus.PetManagerGenderUnavailable, 161)
        ];
        foreach (var (operation, status, expected) in cases)
        {
            var receipt = RejectedReceipt(operation, status);
            receipt.Validate();
            Check.Equal(
                expected,
                GameClientHandler.ResolvePetLegacyResultCode(receipt),
                $"{operation}/{status} stock result");
        }
    }

    private static void CheckEvidenceCodecAndStageSixSeal()
    {
        var before = new PetManagerUtilityPetState(
            "owned", true, true, false, true, true,
            SoulContractStage: 6, Sex: 0, Revision: 7);
        var after = new PetManagerUtilityPetState(
            "sealed", false, false, false, true, false,
            SoulContractStage: 0, Sex: 0, Revision: 8);
        var evidence = new PetManagerUtilityEvidence(
            PetManagerUtilityOperation.Seal,
            PetId: 71,
            ItemTemplateId: 10109,
            ItemInstanceId: 9001,
            KitBagSlot: 1,
            PreviousSex: 0,
            NewSex: 0,
            Growth: null,
            before,
            after);
        var receipt = new PetDurableReceipt(
            CommandFamily.PetManagerUtility,
            PetDurableReceiptStatus.PetSealed,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: 1,
            EquipmentSlot: -1,
            PetId: 71,
            PetLevel: 20,
            PetExperience: 100,
            PetRevision: 8,
            IsCarried: false,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 9,
            AuditReference: "utility-stage-six-seal",
            OutboxEventId: Guid.NewGuid(),
            PetManagerUtility: evidence);
        receipt.Validate();
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            System.Text.Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));
        Check.Equal(
            receipt,
            decoded,
            "family55 stage-six Seal evidence round trips canonically");
        Check.Throws<InvalidDataException>(
            () => (receipt with
            {
                PetRevision = receipt.PetRevision + 1
            }).Validate(),
            "utility receipt revision must equal authoritative after-state revision");
        var forgedEvidence = evidence with
        {
            AfterPetState = after with { Revision = after.Revision + 1 }
        };
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(
                receipt with
                {
                    PetRevision = forgedEvidence.AfterPetState!.Revision,
                    PetManagerUtility = forgedEvidence
                }),
            "codec rejects a forged Seal transition that skips a pet revision");
    }

    private static void CheckEvidenceCodecAndAutoPresenceUnseal()
    {
        var before = new PetManagerUtilityPetState(
            "sealed", false, false, false, true, false,
            SoulContractStage: 0, Sex: 0, Revision: 8)
        {
            CurrentEnergy = 31,
            MaximumEnergy = 100
        };
        var after = before with
        {
            ActivityState = "owned",
            IsCarried = true,
            IsSummoned = true,
            CurrentEnergy = 100,
            Revision = 9
        };
        var evidence = new PetManagerUtilityEvidence(
            PetManagerUtilityOperation.Unseal,
            PetId: 71,
            ItemTemplateId: 10109,
            ItemInstanceId: 9001,
            KitBagSlot: 1,
            PreviousSex: 0,
            NewSex: 0,
            Growth: null,
            before,
            after);
        var receipt = new PetDurableReceipt(
            CommandFamily.PetManagerUtility,
            PetDurableReceiptStatus.PetUnsealed,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: 1,
            EquipmentSlot: -1,
            PetId: 71,
            PetLevel: 20,
            PetExperience: 100,
            PetRevision: 9,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 0,
            AggregateRevision: 10,
            AuditReference: "utility-auto-presence-unseal",
            OutboxEventId: Guid.NewGuid(),
            PetManagerUtility: evidence);
        receipt.Validate();
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            System.Text.Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));
        Check.Equal(
            receipt,
            decoded,
            "Unseal auto-presence and full-energy evidence round-trips exactly");
        Check.Throws<InvalidDataException>(
            () => (receipt with { IsSummoned = false }).Validate(),
            "Unseal receipt cannot contradict its auto-summoned state");
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(
                receipt with
                {
                    PetManagerUtility = evidence with
                    {
                        AfterPetState = after with
                        {
                            CurrentEnergy = 99
                        }
                    }
                }),
            "new Unseal evidence cannot claim a partial energy restore");

        var legacyEvidence = evidence with
        {
            BeforePetState = before with
            {
                CurrentEnergy = null,
                MaximumEnergy = null
            },
            AfterPetState = after with
            {
                IsCarried = false,
                IsSummoned = false,
                CurrentEnergy = null,
                MaximumEnergy = null
            }
        };
        var legacy = receipt with
        {
            IsCarried = false,
            IsSummoned = false,
            AuditReference = "utility-legacy-inactive-unseal",
            PetManagerUtility = legacyEvidence
        };
        var legacyPayload = PetDurablePersistenceCodec.Encode(legacy);
        var legacyPayloadText =
            System.Text.Encoding.UTF8.GetString(legacyPayload);
        Check.True(
            !legacyPayloadText.Contains(
                "\"CurrentEnergy\"",
                StringComparison.Ordinal) &&
            !legacyPayloadText.Contains(
                "\"MaximumEnergy\"",
                StringComparison.Ordinal) &&
            !legacyPayloadText.Contains(
                "\"HasEnergyEvidence\"",
                StringComparison.Ordinal),
            "legacy Unseal JSON omits post-contract energy fields");
        Check.Equal(
            legacy,
            PetDurablePersistenceCodec.DecodeAndVerify(
                legacyPayloadText,
                PetDurablePersistenceCodec.Hash(legacyPayload)),
            "legacy inactive Unseal evidence remains replay-readable");
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(
                legacy with
                {
                    IsCarried = true,
                    IsSummoned = true
                }),
            "legacy receipt cannot forge active presence over inactive evidence");
        var activeWithoutEnergy = receipt with
        {
            PetManagerUtility = evidence with
            {
                BeforePetState = before with
                {
                    CurrentEnergy = null,
                    MaximumEnergy = null
                },
                AfterPetState = after with
                {
                    CurrentEnergy = null,
                    MaximumEnergy = null
                }
            }
        };
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.Encode(activeWithoutEnergy),
            "active Unseal receipts must pin the full-energy transition");
    }

    private static void CheckGenderRefresh()
    {
        var pet = CreatePet() with { Sex = 1 };
        var packet = PacketBuilder.PetGenderRefresh(
            PetContentTestCatalog.Instance,
            pet);
        Check.True(
            packet.Length == 76 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet) == 76 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
                Opcodes.PetLevelUpgrade &&
            packet[68] == pet.SpeciesId &&
            packet[69] == 1 &&
            packet[72] == 1 &&
            packet.AsSpan(70, 2).IndexOfAnyExcept((byte)0) < 0 &&
            packet.AsSpan(73, 3).IndexOfAnyExcept((byte)0) < 0,
            "76-byte gender refresh has exact bounded extension fields");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetGenderRefresh(
                PetContentTestCatalog.Instance,
                pet with { Sex = 2 }),
            "gender refresh rejects sex outside the stock binary field");
    }

    private static void CheckPackedSealProjection()
    {
        var packed = CompactItemEntry.Empty with
        {
            Id = PetItemCatalog.PackedSealJade,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 1,
            LinkedSealedPetId = 0x01020304
        };
        var character = new GameCharacter
        {
            Id = 2,
            AccountId = 13,
            Name = "Packed",
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                packed.ToCompactString()),
            Equipment = GameDefaults.DefaultEquipment(1)
        };
        var record = PacketBuilder.KitBagDetailPages(character)[0]
            .AsSpan(24, 72);
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(record) == 10109 &&
            record[26] == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(56)) ==
                0x01020304,
            "bound packed 10109 projects bound at raw +26 and its authorized linked pet at raw +56");

        var tradable = packed with { Bound = 0 };
        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            tradable.ToCompactString());
        record = PacketBuilder.KitBagDetailPages(character)[0]
            .AsSpan(24, 72);
        Check.True(
            record[26] == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(56)) ==
                0x01020304,
            "unbound packed 10109 remains tradable while retaining the same authorized linked pet ID");

        var ordinary = packed with { Id = 10108 };
        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            ordinary.ToCompactString());
        record = PacketBuilder.KitBagDetailPages(character)[0]
            .AsSpan(24, 72);
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(56)),
            "linked-pet field is never projected for empty Seal Jade");
        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            (packed with { LinkedSealedPetId = 0 }).ToCompactString());
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.KitBagDetailPages(character),
            "packed 10109 without an authoritative link fails closed");
    }

    private static PetDurableReceipt RejectedReceipt(
        PetManagerUtilityOperation operation,
        PetDurableReceiptStatus status) =>
        new(
            CommandFamily.PetManagerUtility,
            status,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: -1,
            EquipmentSlot: -1,
            PetId: 0,
            PetLevel: 0,
            PetExperience: 0,
            PetRevision: 0,
            IsCarried: false,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 0,
            AuditReference: "utility-rejection",
            OutboxEventId: null,
            PetManagerUtility: new PetManagerUtilityEvidence(
                operation, 0, 0, 0, -1, 0, 0, null));

    private static PetBootstrapSnapshot CreatePet()
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
            SpeciesId = 45,
            Name = "Gender Test",
            IsBound = true,
            IsCarried = true,
            IsSummoned = true,
            ContributesToCharacter = false,
            Revision = 8
        };
    }
}
