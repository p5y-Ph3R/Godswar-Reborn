using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetDurableCommandContractChecks
{
    private static void CheckAppearanceChangeContract()
    {
        var operation = Guid.NewGuid();
        var command = new PetAppearanceChangeCommand(
            PetCommandOperationIdentity.SecureClient(operation),
            KitBagSlot: 53);
        var envelope = PetAppearanceChangeCommandEnvelope.Create(
            Subject(),
            Correlation(),
            DateTimeOffset.UtcNow,
            command);
        var retry = PetAppearanceChangeCommandEnvelope.Create(
            Subject(),
            Correlation(),
            envelope.ReceivedAt.AddSeconds(1),
            command);
        var otherSlot = PetAppearanceChangeCommandEnvelope.Create(
            Subject(),
            Correlation(),
            envelope.ReceivedAt,
            command with { KitBagSlot = 54 });
        Check.True(
            PetAppearanceChangeCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid &&
            envelope.Family == CommandFamily.PetAppearanceChange &&
            envelope.OperationId == retry.OperationId &&
            envelope.RequestHash == retry.RequestHash &&
            envelope.RequestHash != otherSlot.RequestHash &&
            LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.PetAppearanceChange) ==
                    CommandIdentityStrength.ClientOperationId,
            "appearance change has a stable family-52 retry identity and hashes its selected slot");

        var evidence = new PetAppearanceChangeEvidence(
            OldSpeciesId: 1,
            OldSpeciesName: "Rock Elf",
            NewSpeciesId: 45,
            NewSpeciesName: "Cupid",
            MagicJadeItemId: 11094,
            MagicJadeDisplayName: "Magic Jade: Cupid",
            MagicJadeItemInstanceId: 7001,
            KitBagSlot: 53,
            PetContentRevision: new string('A', 64),
            ItemContentRevision: new string('B', 64));
        var receipt = new PetDurableReceipt(
            CommandFamily.PetAppearanceChange,
            PetDurableReceiptStatus.PetAppearanceChanged,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: 53,
            EquipmentSlot: -1,
            PetId: 71,
            PetLevel: 20,
            PetExperience: 12_345,
            PetRevision: 9,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 0,
            AggregateRevision: 5,
            AuditReference: "appearance-contract-check",
            OutboxEventId: Guid.NewGuid(),
            AppearanceChange: evidence);
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        Check.Equal(
            receipt,
            PetDurablePersistenceCodec.DecodeAndVerify(
                System.Text.Encoding.UTF8.GetString(payload),
                PetDurablePersistenceCodec.Hash(payload)),
            "appearance receipt retains old/new species, jade instance, slot and revisions");
        Check.Equal(
            (uint)130,
            GameClientHandler.ResolvePetLegacyResultCode(receipt),
            "appearance success maps to stock result 130");
        Check.Throws<InvalidDataException>(
            () => (receipt with
            {
                AppearanceChange = evidence with
                {
                    NewSpeciesId = evidence.OldSpeciesId
                }
            }).Validate(),
            "appearance success rejects same-species evidence");
    }
}
