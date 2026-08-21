using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Characters;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterLifecycleCommandContractChecks
{
    public static Task RunAsync()
    {
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var receivedAt = DateTimeOffset.UtcNow;
        var operationId = Guid.NewGuid();
        var create = CharacterCreateCommandEnvelope.Create(
            347,
            connection,
            receivedAt,
            new CharacterCreateCommand(
                operationId,
                0,
                "LifecycleHero",
                1,
                0,
                2,
                4,
                3,
                5,
                1));
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)CharacterCreateCommandEnvelope.Validate(create),
            "character create accepts a secure account-slot command");
        Check.Equal(
            0,
            create.Subject.CharacterId,
            "lifecycle commands deliberately scope identity to account slot zero");

        var explicitTempest = CharacterCreateCommandEnvelope.Create(
            347,
            connection,
            receivedAt,
            create.Command with { RealmId = RealmId.Tempest });
        var dwargon = CharacterCreateCommandEnvelope.Create(
            347,
            connection,
            receivedAt,
            create.Command with { RealmId = RealmId.Dwargon });
        Check.Equal(
            create.RequestHash,
            explicitTempest.RequestHash,
            "Tempest keeps the historical lifecycle request identity");
        Check.True(
            !string.Equals(
                create.RequestHash,
                dwargon.RequestHash,
                StringComparison.Ordinal),
            "the same lifecycle intent in another realm has a distinct request hash");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)CharacterCreateCommandEnvelope.Validate(dwargon),
            "Dwargon accepts a realm-scoped character create intent");
        Check.Throws<ArgumentOutOfRangeException>(
            () => CharacterCreateCommandEnvelope.Create(
                347,
                connection,
                receivedAt,
                create.Command with { RealmId = default }),
            "an unspecified lifecycle realm fails closed before persistence");
        Check.Equal(
            "347:0",
            CharacterLifecyclePersistenceCodec.AggregateKey(347, 0),
            "Tempest retains its historical aggregate key");
        Check.Equal(
            "347:2:0",
            CharacterLifecyclePersistenceCodec.AggregateKey(
                347,
                RealmId.Dwargon,
                0),
            "new realm streams include realm identity in their aggregate key");
        Check.Equal(
            CharacterLifecyclePersistenceCodec.RealmAggregateType,
            CharacterLifecyclePersistenceCodec.AggregateTypeFor(
                RealmId.Dwargon),
            "new realms use the realm-scoped aggregate contract");
        Check.Equal(
            CharacterLifecyclePersistenceCodec.RealmConsumerKey,
            CharacterLifecyclePersistenceCodec.ConsumerKeyFor(
                RealmId.Dwargon),
            "new realms use the realm-scoped durable consumer");

        var deleteCommand = new CharacterDeleteCommand(
            operationId,
            0,
            "LifecycleHero",
            10,
            8);
        var delete = CharacterDeleteCommandEnvelope.Create(
            347,
            connection,
            receivedAt,
            deleteCommand);
        var replayAfterStateLoss = CharacterDeleteCommandEnvelope.Create(
            347,
            connection,
            receivedAt.AddMinutes(2),
            deleteCommand with
            {
                ExpectedActiveCharacterId = null,
                ExpectedLifecycleVersion = null
            });
        Check.Equal(
            delete.RequestHash,
            replayAfterStateLoss.RequestHash,
            "delete hash excludes server-derived state needed only for a fresh precondition");
        Check.Equal(
            delete.OperationId,
            replayAfterStateLoss.OperationId,
            "delete replay keeps stable UUID identity after tombstoning");

        var changedName = CharacterDeleteCommandEnvelope.Create(
            347,
            connection,
            receivedAt,
            deleteCommand with { Name = "DifferentHero" });
        Check.True(
            !string.Equals(
                delete.RequestHash,
                changedName.RequestHash,
                StringComparison.Ordinal),
            "same delete UUID with a different name has a conflicting request hash");

        var restore = CharacterRestoreCommandEnvelope.Create(
            347,
            connection,
            receivedAt,
            new CharacterRestoreCommand(
                Guid.NewGuid(),
                0,
                10,
                8));
        var purge = CharacterPurgeCommandEnvelope.Create(
            347,
            connection,
            receivedAt,
            new CharacterPurgeCommand(
                Guid.NewGuid(),
                0,
                10,
                8));
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)CharacterRestoreCommandEnvelope.Validate(restore),
            "restore requires an exact tombstone id and version");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)CharacterPurgeCommandEnvelope.Validate(purge),
            "purge requires an exact tombstone id and version");

        var tampered = create with { RequestHash = new string('0', 64) };
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)CharacterCreateCommandEnvelope.Validate(tampered),
            "tampered lifecycle requests fail closed");

        foreach (var invalid in new[]
        {
            create.Command with { Gender = 2 },
            create.Command with { Camp = 2 },
            create.Command with { Profession = 4 },
            create.Command with { ZodiacType = 12 },
            create.Command with { Faith = 4 }
        })
        {
            var envelope = CharacterCreateCommandEnvelope.Create(
                347,
                connection,
                receivedAt,
                invalid);
            Check.Equal(
                (int)CommandEnvelopeValidation.InvalidCommand,
                (int)CharacterCreateCommandEnvelope.Validate(envelope),
                "out-of-domain secure character creation values fail closed");
        }

        CheckReceiptEvidenceContracts();
        return Task.CompletedTask;
    }
}
