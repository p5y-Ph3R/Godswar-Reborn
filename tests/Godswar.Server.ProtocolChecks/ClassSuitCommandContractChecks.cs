using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static class ClassSuitCommandContractChecks
{
    public const string CheckName =
        "Durable Class Suit command identity contract";

    private static readonly Guid ClientOperationId =
        Guid.Parse("e5bb9b3c-b3e9-4ab0-bcc8-88546ccfd013");
    private static readonly CommandSubject Subject = new(7, 13);

    public static Task RunAsync()
    {
        CheckFamilies();
        CheckOperationShapesAndBounds();
        CheckSecureProvenanceAndCanonicalRequest();
        CheckRawLocalProvenance();
        return Task.CompletedTask;
    }

    private static void CheckFamilies()
    {
        var expected = new[]
        {
            (
                ClassSuitCommandOperation.ExchangeTierI,
                CommandFamily.ClassSuitExchangeTierI,
                34),
            (
                ClassSuitCommandOperation.ConvertToCommon,
                CommandFamily.ClassSuitConvertToCommon,
                35),
            (
                ClassSuitCommandOperation.UpgradeTierII,
                CommandFamily.ClassSuitUpgradeTierII,
                36),
            (
                ClassSuitCommandOperation.UpgradeTierIII,
                CommandFamily.ClassSuitUpgradeTierIII,
                37),
            (
                ClassSuitCommandOperation.UpgradeTierIV,
                CommandFamily.ClassSuitUpgradeTierIV,
                38),
            (
                ClassSuitCommandOperation.AddAttribute,
                CommandFamily.ClassSuitAddAttribute,
                39),
            (
                ClassSuitCommandOperation.DeleteAttribute,
                CommandFamily.ClassSuitDeleteAttribute,
                40)
        };

        foreach (var (operation, family, value) in expected)
        {
            Check.True(
                ClassSuitCommandEnvelope.Family(operation) == family,
                $"{operation} family identity");
            Check.Equal(value, (int)family, $"{operation} family value");
        }

        Check.True(
            ClassSuitCommandEnvelope.IsEndpoint(
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex) &&
            ClassSuitCommandEnvelope.IsEndpoint(
                ClassSuitCommandEnvelope.AthensNpcId,
                ClassSuitCommandEnvelope.DialogIndex),
            "both captured Gear Mentors are valid command endpoints");
    }

    private static void CheckOperationShapesAndBounds()
    {
        var identity =
            ClassSuitOperationIdentity.SecureClient(ClientOperationId);
        foreach (var operation in Enum.GetValues<ClassSuitCommandOperation>())
        {
            Check.True(
                TryCreateValid(identity, operation, out _),
                $"{operation} accepts its exact selection shape");
        }

        Check.True(
            !ClassSuitCommandEnvelope.TryCreateCommand(
                identity,
                ClassSuitCommandOperation.ExchangeTierI,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                Selection(0, "gear"),
                null,
                null,
                out _),
            "forward conversion requires one insignia");
        Check.True(
            !ClassSuitCommandEnvelope.TryCreateCommand(
                identity,
                ClassSuitCommandOperation.ConvertToCommon,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                Selection(0, "gear"),
                Selection(1, "unexpected"),
                null,
                out _),
            "reverse conversion accepts no material");
        Check.True(
            !ClassSuitCommandEnvelope.TryCreateCommand(
                identity,
                ClassSuitCommandOperation.AddAttribute,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                Selection(0, "gear"),
                Selection(1, "flame"),
                null,
                out _),
            "Add Attribute requires both flame and class stone");
        Check.True(
            !ClassSuitCommandEnvelope.TryCreateCommand(
                identity,
                ClassSuitCommandOperation.DeleteAttribute,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                Selection(0, "gear"),
                Selection(0, "water"),
                null,
                out _),
            "one slot cannot identify two selected items");

        foreach (var invalidSlot in new[] { -1, 96, int.MaxValue })
        {
            Check.True(
                !ClassSuitCommandEnvelope.TryCreateCommand(
                    identity,
                    ClassSuitCommandOperation.ConvertToCommon,
                    ClassSuitCommandEnvelope.SpartaNpcId,
                    ClassSuitCommandEnvelope.DialogIndex,
                    Selection(invalidSlot, "gear"),
                    null,
                    null,
                    out _),
                $"kit-bag slot {invalidSlot} fails closed");
        }

        foreach (var invalidState in new[]
                 {
                     string.Empty,
                     "state\ninjection",
                     new string('é', 257),
                     "\ud800"
                 })
        {
            Check.True(
                !ClassSuitCommandEnvelope.TryCreateCommand(
                    identity,
                    ClassSuitCommandOperation.ConvertToCommon,
                    ClassSuitCommandEnvelope.SpartaNpcId,
                    ClassSuitCommandEnvelope.DialogIndex,
                    Selection(0, invalidState),
                    null,
                    null,
                    out _),
                "invalid compact state fails closed");
        }

        Check.True(
            !ClassSuitCommandEnvelope.TryCreateCommand(
                identity,
                ClassSuitCommandOperation.ConvertToCommon,
                9999,
                ClassSuitCommandEnvelope.DialogIndex,
                Selection(0, "gear"),
                null,
                null,
                out _),
            "unrelated NPC cannot submit a Class Suit command");
        Check.True(
            !TryCreateValid(
                ClassSuitOperationIdentity.SecureClient(Guid.Empty),
                ClassSuitCommandOperation.ExchangeTierI,
                out _),
            "secure identity requires a non-empty client UUID");
    }

    private static void CheckSecureProvenanceAndCanonicalRequest()
    {
        var identity =
            ClassSuitOperationIdentity.SecureClient(ClientOperationId);
        Check.True(
            TryCreateValid(
                identity,
                ClassSuitCommandOperation.ExchangeTierI,
                out var command),
            "secure command fixture");
        var receivedAt = DateTimeOffset.UtcNow;
        var tlsConnection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var envelope = ClassSuitCommandEnvelope.Create(
            Subject,
            tlsConnection,
            receivedAt,
            command);

        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)ClassSuitCommandEnvelope.Validate(envelope),
            "secure TLS legacy provenance validates");
        Check.Equal(
            (int)CommandIdentityStrength.ClientOperationId,
            (int)envelope.IdentityStrength,
            "secure UUID remains client-owned");

        var secureCommandEnvelope = ClassSuitCommandEnvelope.Create(
            Subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureCommand),
            receivedAt,
            command);
        Check.True(
            envelope.OperationId == secureCommandEnvelope.OperationId &&
            envelope.RequestHash == secureCommandEnvelope.RequestHash,
            "secure reconnect transport does not change command identity");

        var changedGear = command with
        {
            Gear = Selection(
                command.Gear.KitBagSlot,
                "gear-state-changed")
        };
        var changedEnvelope = ClassSuitCommandEnvelope.Create(
            Subject,
            tlsConnection,
            receivedAt,
            changedGear);
        Check.True(
            envelope.OperationId == changedEnvelope.OperationId &&
            envelope.RequestHash != changedEnvelope.RequestHash,
            "same UUID with changed expected state is a request conflict");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)ClassSuitCommandEnvelope.Validate(
                envelope with { Command = changedGear }),
            "tampered selection fails canonical request validation");

        var athensCommand = command with
        {
            NpcId = ClassSuitCommandEnvelope.AthensNpcId
        };
        var athensEnvelope = ClassSuitCommandEnvelope.Create(
            Subject,
            tlsConnection,
            receivedAt,
            athensCommand);
        Check.True(
            envelope.OperationId == athensEnvelope.OperationId &&
            envelope.RequestHash != athensEnvelope.RequestHash,
            "endpoint changes conflict under the same operation UUID");

        Check.True(
            TryCreateValid(
                identity,
                ClassSuitCommandOperation.UpgradeTierII,
                out var tierTwo),
            "Tier-II command fixture");
        Check.True(
            ClassSuitCommandEnvelope.CreateOperationId(
                Subject,
                command.Operation,
                identity) !=
            ClassSuitCommandEnvelope.CreateOperationId(
                Subject,
                tierTwo.Operation,
                identity),
            "one UUID cannot alias two command families");

        Check.Throws<ArgumentException>(
            () => ClassSuitCommandEnvelope.Create(
                Subject,
                tlsConnection with
                {
                    Transport = CommandTransportKind.LegacyTcp
                },
                receivedAt,
                command),
            "raw legacy transport cannot claim a client UUID");
    }

    private static void CheckRawLocalProvenance()
    {
        var connectionId = Guid.Parse(
            "f3b625b2-d003-4127-be1b-d9066637778e");
        var identity = ClassSuitOperationIdentity.RawLocalServer(
            Guid.Parse("c5f1c544-e59a-4606-a7d1-286260228a22"),
            connectionId);
        Check.True(
            TryCreateValid(
                identity,
                ClassSuitCommandOperation.ConvertToCommon,
                out var command),
            "raw-local command fixture");
        var connection = new CommandConnectionCorrelation(
            connectionId,
            CommandTransportKind.LegacyTcp);
        var envelope = ClassSuitCommandEnvelope.Create(
            Subject,
            connection,
            DateTimeOffset.UtcNow,
            command);

        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)ClassSuitCommandEnvelope.Validate(envelope),
            "raw-local server identity validates on its connection");
        Check.Equal(
            (int)CommandIdentityStrength.ServerOperationId,
            (int)envelope.IdentityStrength,
            "raw-local UUID is explicitly server-owned");
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCorrelation,
            (int)ClassSuitCommandEnvelope.Validate(
                envelope with
                {
                    Connection = connection with
                    {
                        ConnectionId = Guid.NewGuid()
                    }
                }),
            "raw-local identity cannot move to another connection");
        Check.Throws<ArgumentException>(
            () => ClassSuitCommandEnvelope.Create(
                Subject,
                connection with
                {
                    Transport = CommandTransportKind.SecureTlsLegacy
                },
                envelope.ReceivedAt,
                command),
            "server-generated raw identity cannot claim secure provenance");

        var otherConnectionIdentity =
            ClassSuitOperationIdentity.RawLocalServer(
                identity.OperationId,
                Guid.NewGuid());
        Check.True(
            ClassSuitCommandEnvelope.CreateOperationId(
                Subject,
                command.Operation,
                identity) !=
            ClassSuitCommandEnvelope.CreateOperationId(
                Subject,
                command.Operation,
                otherConnectionIdentity),
            "raw-local operation identity is connection scoped");
    }

    private static bool TryCreateValid(
        ClassSuitOperationIdentity identity,
        ClassSuitCommandOperation operation,
        out ClassSuitCommand command)
    {
        ClassSuitCommandSelection? primary =
            operation == ClassSuitCommandOperation.ConvertToCommon
            ? null
            : Selection(1, "primary-material-state");
        ClassSuitCommandSelection? secondary =
            operation == ClassSuitCommandOperation.AddAttribute
            ? Selection(2, "secondary-material-state")
            : null;
        return ClassSuitCommandEnvelope.TryCreateCommand(
            identity,
            operation,
            ClassSuitCommandEnvelope.SpartaNpcId,
            ClassSuitCommandEnvelope.DialogIndex,
            Selection(0, "gear-state"),
            primary,
            secondary,
            out command);
    }

    private static ClassSuitCommandSelection Selection(
        int slot,
        string state) =>
        new(slot, state);
}
