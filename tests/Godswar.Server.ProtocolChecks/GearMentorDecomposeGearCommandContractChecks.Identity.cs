using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDecomposeGearCommandContractChecks
{
    private static void CheckCanonicalEnvelope()
    {
        var command = ValidCommand();
        var tlsEnvelope = CreateEnvelope(
            command,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var secureEnvelope = CreateEnvelope(
            command,
            Guid.NewGuid(),
            CommandTransportKind.SecureCommand);

        Check.Equal(
            tlsEnvelope.OperationId,
            secureEnvelope.OperationId,
            "Decompose operation identity survives reconnect");
        Check.Equal(
            tlsEnvelope.RequestHash,
            secureEnvelope.RequestHash,
            "Decompose request hash ignores transport replacement");
        Check.Equal(
            ExpectedRequestHash(command),
            tlsEnvelope.RequestHash,
            "Decompose canonical request uses bounded network order");
        Check.Equal(
            ExpectedOperationId(Subject, ClientOperationId),
            tlsEnvelope.OperationId,
            "Decompose UUID operation scope uses network order");
        Check.Equal(
            tlsEnvelope.OperationId,
            GearMentorDecomposeGearCommandEnvelope.CreateOperationId(
                Subject,
                ClientOperationId),
            "Decompose replay identity requires no selection state");

        var reversed = command with
        {
            Selections = command.Selections.Reverse().ToImmutableArray()
        };
        var reversedEnvelope = CreateEnvelope(
            reversed,
            Guid.NewGuid(),
            CommandTransportKind.SecureCommand);
        Check.True(
            !string.Equals(
                tlsEnvelope.RequestHash,
                reversedEnvelope.RequestHash,
                StringComparison.Ordinal),
            "selection order participates in the Decompose request hash");
        Check.Equal(
            tlsEnvelope.OperationId,
            reversedEnvelope.OperationId,
            "selection order does not change the client UUID identity");

        Check.Throws<ArgumentException>(
            () =>
                GearMentorDecomposeGearCommandEnvelope.CreateOperationId(
                    Subject,
                    Guid.Empty),
            "Decompose replay rejects an empty UUID");
        Check.Throws<ArgumentOutOfRangeException>(
            () =>
                GearMentorDecomposeGearCommandEnvelope.CreateOperationId(
                    new CommandSubject(0, Subject.CharacterId),
                    ClientOperationId),
            "Decompose replay rejects an unauthenticated subject");
    }

    private static void CheckEnvelopeConflicts()
    {
        var envelope = CreateEnvelope(
            ValidCommand(),
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)GearMentorDecomposeGearCommandEnvelope.Validate(
                envelope),
            "valid Decompose envelope validates");
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCorrelation,
            (int)GearMentorDecomposeGearCommandEnvelope.Validate(
                envelope with
                {
                    Connection = new CommandConnectionCorrelation(
                        Guid.NewGuid(),
                        CommandTransportKind.LegacyTcp)
                }),
            "Decompose validation rejects legacy UUID provenance");

        AssertRequestConflict(
            envelope with
            {
                Command = envelope.Command with
                {
                    NpcId = GearMentorDecomposeGearCommandEnvelope
                        .SpartaGearMentorNpcId
                }
            },
            "Decompose NPC participates in request hash");
        AssertRequestConflict(
            envelope with
            {
                Command = envelope.Command with
                {
                    Selections = envelope.Command.Selections.SetItem(
                        0,
                        envelope.Command.Selections[0] with
                        {
                            ExpectedCompactItemState = "changed"
                        })
                }
            },
            "each exact item snapshot participates in request hash");
        AssertRequestConflict(
            envelope with
            {
                Command = envelope.Command with
                {
                    Selections = envelope.Command.Selections.SetItem(
                        0,
                        envelope.Command.Selections[0] with
                        {
                            SelectedKitBagSlot = 13
                        })
                }
            },
            "each selected bag slot participates in request hash");

        Check.Equal(
            (int)CommandEnvelopeValidation.OperationIdentityConflict,
            (int)GearMentorDecomposeGearCommandEnvelope.Validate(
                envelope with
                {
                    Command = envelope.Command with
                    {
                        ClientOperationId = Guid.NewGuid()
                    }
                }),
            "changed Decompose UUID conflicts with operation identity");
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCommand,
            (int)GearMentorDecomposeGearCommandEnvelope.Validate(
                envelope with
                {
                    Command = envelope.Command with
                    {
                        Selections =
                            ImmutableArray<
                                GearMentorDecomposeSelection>.Empty
                    }
                }),
            "forged empty selection list is invalid intent");
    }

    private static CommandEnvelope<GearMentorDecomposeGearCommand>
        CreateEnvelope(
            GearMentorDecomposeGearCommand command,
            Guid connectionId,
            CommandTransportKind transport) =>
        GearMentorDecomposeGearCommandEnvelope.Create(
            Subject,
            new CommandConnectionCorrelation(connectionId, transport),
            DateTimeOffset.UtcNow,
            command);

    private static void AssertRequestConflict(
        CommandEnvelope<GearMentorDecomposeGearCommand> envelope,
        string description) =>
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)GearMentorDecomposeGearCommandEnvelope.Validate(
                envelope),
            description);

    private static string ExpectedRequestHash(
        GearMentorDecomposeGearCommand command)
    {
        var states = command.Selections
            .Select(static selection =>
                Encoding.UTF8.GetBytes(
                    selection.ExpectedCompactItemState))
            .ToArray();
        var canonical = new byte[
            sizeof(ushort) +
            sizeof(int) +
            sizeof(byte) +
            (command.Selections.Length *
                (sizeof(ushort) + sizeof(ushort))) +
            states.Sum(static state => state.Length)];
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(destination, 1);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[sizeof(ushort)..],
            command.NpcId);
        destination[sizeof(ushort) + sizeof(int)] =
            checked((byte)command.Selections.Length);
        var offset = sizeof(ushort) + sizeof(int) + sizeof(byte);
        for (var index = 0; index < command.Selections.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                destination[offset..],
                checked((ushort)command.Selections[index]
                    .SelectedKitBagSlot));
            offset += sizeof(ushort);
            BinaryPrimitives.WriteUInt16BigEndian(
                destination[offset..],
                checked((ushort)states[index].Length));
            offset += sizeof(ushort);
            states[index].CopyTo(destination[offset..]);
            offset += states[index].Length;
        }

        return HashRequest(canonical);
    }

    private static string HashRequest(ReadOnlySpan<byte> canonical)
    {
        var domain =
            Encoding.ASCII.GetBytes("godswar.command.request.v1\0");
        var input = new byte[
            domain.Length +
            sizeof(int) +
            sizeof(ushort) +
            canonical.Length];
        domain.CopyTo(input, 0);
        var offset = domain.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset),
            CommandEnvelopeContract.CurrentVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt16BigEndian(
            input.AsSpan(offset),
            (ushort)CommandFamily.GearMentorDecomposeGear);
        offset += sizeof(ushort);
        canonical.CopyTo(input.AsSpan(offset));
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string ExpectedOperationId(
        CommandSubject subject,
        Guid operationId)
    {
        Span<byte> operationScope = stackalloc byte[16];
        Check.True(
            operationId.TryWriteBytes(
                operationScope,
                bigEndian: true,
                out var written) &&
            written == operationScope.Length,
            "test Decompose UUID writes in network order");

        var domain =
            Encoding.ASCII.GetBytes("godswar.command.operation.v1\0");
        var input = new byte[
            domain.Length +
            sizeof(int) +
            sizeof(ushort) +
            (sizeof(int) * 2) +
            operationScope.Length];
        domain.CopyTo(input, 0);
        var offset = domain.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset),
            CommandEnvelopeContract.CurrentVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt16BigEndian(
            input.AsSpan(offset),
            (ushort)CommandFamily.GearMentorDecomposeGear);
        offset += sizeof(ushort);
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset),
            subject.AccountId);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset),
            subject.CharacterId);
        offset += sizeof(int);
        operationScope.CopyTo(input.AsSpan(offset));
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
