using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetDurableCommandContractChecks
{
    private static void CheckPetBindContract()
    {
        var operationId = Guid.NewGuid();
        var identity = PetCommandOperationIdentity.SecureClient(
            operationId);
        var subject = new CommandSubject(13, 2);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var envelope = PetBindCommandEnvelope.Create(
            subject,
            correlation,
            DateTimeOffset.UtcNow,
            new PetBindCommand(identity));
        var retry = PetBindCommandEnvelope.Create(
            subject,
            correlation,
            DateTimeOffset.UtcNow.AddSeconds(5),
            new PetBindCommand(identity));
        Check.True(
            envelope.Family == CommandFamily.PetBind &&
            envelope.OperationId == retry.OperationId &&
            envelope.RequestHash == retry.RequestHash &&
            PetBindCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid &&
            LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.PetBind) ==
                CommandIdentityStrength.ClientOperationId,
            "pet bind has a dedicated retry-stable family-53 identity");

        var receipt = new PetDurableReceipt(
            CommandFamily.PetBind,
            PetDurableReceiptStatus.PetBound,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: -1,
            EquipmentSlot: -1,
            PetId: 71,
            PetLevel: 1,
            PetExperience: 0,
            PetRevision: 9,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 0,
            AggregateRevision: 4,
            AuditReference: "bind-contract",
            OutboxEventId: Guid.NewGuid());
        receipt.Validate();
        Check.True(
            PetDurablePersistenceCodec.Decode(
                PetDurablePersistenceCodec.Encode(receipt)) == receipt &&
            PetDurablePersistenceCodec.FamilyCode(
                CommandFamily.PetBind) == "pet_bind" &&
            PetDurablePersistenceCodec.EventType(
                CommandFamily.PetBind) == "pet.bound",
            "pet bind receipt is durable and replay-decodable");
    }
}
