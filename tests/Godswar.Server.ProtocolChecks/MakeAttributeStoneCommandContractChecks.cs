using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MakeAttributeStoneCommandContractChecks
{
    private static readonly Guid ClientOperationId =
        Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly CommandSubject Subject = new(347, 7);

    public static Task RunAsync()
    {
        CheckCommandBounds();
        CheckCanonicalDeterminism();
        CheckStableNetworkOrderOperationScope();
        CheckEnvelopeConflicts();
        CheckNativeResultMapping();
        CheckReceiptAndResultInvariants();
        return Task.CompletedTask;
    }

    private static void CheckCommandBounds()
    {
        Check.True(
            TryCreate(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .AthensGearMentorNpcId,
                0,
                "v1:item=9900;quantity=99;bound=1",
                out _),
            "Make Attribute Stone accepts the Athens mentor and slot zero");
        Check.True(
            TryCreate(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .SpartaGearMentorNpcId,
                95,
                new string('é', 256),
                out _),
            "Make Attribute Stone accepts its inclusive UTF-8 and slot bounds");
        Check.True(
            !TryCreate(5000, 0, "state", out _),
            "Make Attribute Stone rejects a non-physical NPC ID");
        Check.True(
            !TryCreate(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .AthensGearMentorNpcId,
                -1,
                "state",
                out _),
            "Make Attribute Stone rejects a negative bag slot");
        Check.True(
            !TryCreate(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .AthensGearMentorNpcId,
                96,
                "state",
                out _),
            "Make Attribute Stone rejects a bag slot above 95");
        Check.True(
            !TryCreate(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .AthensGearMentorNpcId,
                0,
                string.Empty,
                out _),
            "Make Attribute Stone requires an expected compact state");
        Check.True(
            !TryCreate(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .AthensGearMentorNpcId,
                0,
                "state\ninjection",
                out _),
            "Make Attribute Stone rejects control characters");
        Check.True(
            !TryCreate(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .AthensGearMentorNpcId,
                0,
                new string('é', 257),
                out _),
            "Make Attribute Stone enforces its UTF-8 byte bound");
        Check.True(
            !TryCreate(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .AthensGearMentorNpcId,
                0,
                "\ud800",
                out _),
            "Make Attribute Stone rejects non-canonical invalid UTF-16");
        Check.True(
            !GearMentorMakeAttributeStoneCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                GearMentorMakeAttributeStoneCommandEnvelope
                    .AthensGearMentorNpcId,
                0,
                "state",
                out _),
            "Make Attribute Stone requires a client operation ID");
        Check.Equal(
            (int)CommandIdentityStrength.ClientOperationId,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.GearMentorMakeAttributeStone),
            "Make Attribute Stone has explicit client-operation identity");
        Check.Equal(
            "gear_mentor_make_attribute_stone",
            CommandMetrics.FamilyCode(
                CommandFamily.GearMentorMakeAttributeStone),
            "Make Attribute Stone has a bounded metric family");
    }

    private static void CheckCanonicalDeterminism()
    {
        const string expectedState =
            "v1:item=9900;quantity=99;bound=1";
        var original = CreateEnvelope(
            ClientOperationId,
            GearMentorMakeAttributeStoneCommandEnvelope
                .AthensGearMentorNpcId,
            12,
            expectedState,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var retry = CreateEnvelope(
            ClientOperationId,
            GearMentorMakeAttributeStoneCommandEnvelope
                .AthensGearMentorNpcId,
            12,
            expectedState,
            Guid.NewGuid(),
            CommandTransportKind.SecureCommand);

        Check.Equal(
            original.OperationId,
            retry.OperationId,
            "operation identity survives connection replacement");
        Check.Equal(
            original.RequestHash,
            retry.RequestHash,
            "canonical request survives transport replacement");
        Check.Equal(
            ExpectedRequestHash(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .AthensGearMentorNpcId,
                12,
                expectedState),
            original.RequestHash,
            "canonical request has a stable versioned network-order layout");
    }

    private static void CheckStableNetworkOrderOperationScope()
    {
        var envelope = CreateEnvelope(
            ClientOperationId,
            GearMentorMakeAttributeStoneCommandEnvelope
                .AthensGearMentorNpcId,
            12,
            "state",
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var independentlyDerived =
            ExpectedOperationId(Subject, ClientOperationId);
        Check.Equal(
            independentlyDerived,
            envelope.OperationId,
            "operation scope uses RFC 4122 network-order UUID bytes");
        Check.Equal(
            independentlyDerived,
            GearMentorMakeAttributeStoneCommandEnvelope.CreateOperationId(
                Subject,
                ClientOperationId),
            "replay lookup derives identity without selection state");
        Check.Throws<ArgumentException>(
            () =>
                GearMentorMakeAttributeStoneCommandEnvelope
                    .CreateOperationId(Subject, Guid.Empty),
            "replay identity rejects the empty UUID");
        Check.Throws<ArgumentOutOfRangeException>(
            () =>
                GearMentorMakeAttributeStoneCommandEnvelope
                    .CreateOperationId(
                        new CommandSubject(0, 7),
                        ClientOperationId),
            "replay identity rejects an unauthenticated subject");
    }

    private static void CheckEnvelopeConflicts()
    {
        var envelope = CreateEnvelope(
            ClientOperationId,
            GearMentorMakeAttributeStoneCommandEnvelope
                .AthensGearMentorNpcId,
            12,
            "state-a",
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)GearMentorMakeAttributeStoneCommandEnvelope.Validate(
                envelope),
            "well-formed Make Attribute Stone envelope validates");
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCorrelation,
            (int)GearMentorMakeAttributeStoneCommandEnvelope.Validate(
                envelope with
                {
                    Connection =
                        new CommandConnectionCorrelation(
                            Guid.NewGuid(),
                            CommandTransportKind.LegacyTcp)
                }),
            "Make Attribute Stone rejects untrusted legacy-TCP UUID " +
            "provenance");

        CheckRequestConflict(
            envelope with
            {
                Command = envelope.Command with
                {
                    NpcId =
                        GearMentorMakeAttributeStoneCommandEnvelope
                            .SpartaGearMentorNpcId
                }
            },
            "NPC ID participates in the request hash");
        CheckRequestConflict(
            envelope with
            {
                Command = envelope.Command with
                {
                    SelectedKitBagSlot = 13
                }
            },
            "selected slot participates in the request hash");
        CheckRequestConflict(
            envelope with
            {
                Command = envelope.Command with
                {
                    ExpectedCompactItemState = "state-b"
                }
            },
            "expected item state participates in the request hash");

        var changedOperation = envelope with
        {
            Command = envelope.Command with
            {
                ClientOperationId = Guid.Parse(
                    "ffeeddcc-bbaa-9988-7766-554433221100")
            }
        };
        Check.Equal(
            (int)CommandEnvelopeValidation.OperationIdentityConflict,
            (int)GearMentorMakeAttributeStoneCommandEnvelope.Validate(
                changedOperation),
            "changed UUID conflicts with the operation hash");

        var invalidField = envelope with
        {
            Command = envelope.Command with
            {
                ExpectedCompactItemState = string.Empty
            }
        };
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCommand,
            (int)GearMentorMakeAttributeStoneCommandEnvelope.Validate(
                invalidField),
            "invalid fields fail before digest comparison");
    }

    private static bool TryCreate(
        int npcId,
        int selectedKitBagSlot,
        string expectedState,
        out GearMentorMakeAttributeStoneCommand command) =>
        GearMentorMakeAttributeStoneCommandEnvelope.TryCreateCommand(
            ClientOperationId,
            npcId,
            selectedKitBagSlot,
            expectedState,
            out command);

    private static CommandEnvelope<GearMentorMakeAttributeStoneCommand>
        CreateEnvelope(
            Guid clientOperationId,
            int npcId,
            int selectedKitBagSlot,
            string expectedState,
            Guid connectionId,
            CommandTransportKind transport)
    {
        Check.True(
            GearMentorMakeAttributeStoneCommandEnvelope.TryCreateCommand(
                clientOperationId,
                npcId,
                selectedKitBagSlot,
                expectedState,
                out var command),
            "test Make Attribute Stone command is valid");
        return GearMentorMakeAttributeStoneCommandEnvelope.Create(
            Subject,
            new CommandConnectionCorrelation(connectionId, transport),
            DateTimeOffset.UtcNow,
            command);
    }

    private static void CheckRequestConflict(
        CommandEnvelope<GearMentorMakeAttributeStoneCommand> envelope,
        string description)
    {
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)GearMentorMakeAttributeStoneCommandEnvelope.Validate(
                envelope),
            description);
    }

    private static string ExpectedRequestHash(
        int npcId,
        int slot,
        string expectedState)
    {
        var stateBytes = Encoding.UTF8.GetBytes(expectedState);
        var canonical = new byte[
            sizeof(ushort) +
            sizeof(int) +
            sizeof(ushort) +
            sizeof(ushort) +
            stateBytes.Length];
        var canonicalSpan = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(canonicalSpan, 1);
        BinaryPrimitives.WriteInt32BigEndian(
            canonicalSpan[sizeof(ushort)..],
            npcId);
        BinaryPrimitives.WriteUInt16BigEndian(
            canonicalSpan[(sizeof(ushort) + sizeof(int))..],
            checked((ushort)slot));
        BinaryPrimitives.WriteUInt16BigEndian(
            canonicalSpan[
                (sizeof(ushort) + sizeof(int) + sizeof(ushort))..],
            checked((ushort)stateBytes.Length));
        stateBytes.CopyTo(
            canonicalSpan[
                (sizeof(ushort) +
                 sizeof(int) +
                 sizeof(ushort) +
                 sizeof(ushort))..]);

        return Hash(
            "godswar.command.request.v1\0",
            canonical,
            includeSubject: false,
            Subject);
    }

    private static string ExpectedOperationId(
        CommandSubject subject,
        Guid operationId)
    {
        Span<byte> scope = stackalloc byte[16];
        Check.True(
            operationId.TryWriteBytes(
                scope,
                bigEndian: true,
                out var written) &&
            written == scope.Length,
            "test UUID writes in network order");
        return Hash(
            "godswar.command.operation.v1\0",
            scope,
            includeSubject: true,
            subject);
    }

    private static string Hash(
        string domain,
        ReadOnlySpan<byte> suffix,
        bool includeSubject,
        CommandSubject subject)
    {
        var domainBytes = Encoding.ASCII.GetBytes(domain);
        var subjectBytes = includeSubject
            ? sizeof(int) * 2
            : 0;
        var input = new byte[
            domainBytes.Length +
            sizeof(int) +
            sizeof(ushort) +
            subjectBytes +
            suffix.Length];
        domainBytes.CopyTo(input, 0);
        var offset = domainBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset),
            CommandEnvelopeContract.CurrentVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt16BigEndian(
            input.AsSpan(offset),
            (ushort)CommandFamily.GearMentorMakeAttributeStone);
        offset += sizeof(ushort);
        if (includeSubject)
        {
            BinaryPrimitives.WriteInt32BigEndian(
                input.AsSpan(offset),
                subject.AccountId);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt32BigEndian(
                input.AsSpan(offset),
                subject.CharacterId);
            offset += sizeof(int);
        }

        suffix.CopyTo(input.AsSpan(offset));
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
