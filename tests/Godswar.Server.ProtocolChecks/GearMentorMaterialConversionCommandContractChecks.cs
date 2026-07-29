using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    GearMentorMaterialConversionCommandContractChecks
{
    private static readonly Guid ClientOperationId =
        Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly CommandSubject Subject = new(347, 7);

    public static Task RunAsync()
    {
        CheckCommandBounds();
        CheckCanonicalEnvelopes();
        CheckEnvelopeConflicts();
        CheckNativeResultMapping();
        CheckReceiptAndResultInvariants();
        return Task.CompletedTask;
    }

    private static void CheckCommandBounds()
    {
        Check.True(
            TryCreateTransform(
                GearMentorTransformCrystalCommandEnvelope
                    .AthensGearMentorNpcId,
                0,
                "v1:item=4234;quantity=1;bound=1",
                out _),
            "Transform Crystal accepts Athens and slot zero");
        Check.True(
            TryCreateCombine(
                GearMentorCombineGemPiecesCommandEnvelope
                    .SpartaGearMentorNpcId,
                95,
                new string('é', 256),
                out _),
            "Combine Gem Pieces accepts inclusive UTF-8 and slot bounds");

        foreach (var npcId in new[] { -1, 0, 5000 })
        {
            Check.True(
                !TryCreateTransform(npcId, 0, "state", out _),
                $"Transform Crystal rejects NPC {npcId}");
            Check.True(
                !TryCreateCombine(npcId, 0, "state", out _),
                $"Combine Gem Pieces rejects NPC {npcId}");
        }
        foreach (var slot in new[] { -1, 96, int.MaxValue })
        {
            Check.True(
                !TryCreateTransform(
                    GearMentorTransformCrystalCommandEnvelope
                        .AthensGearMentorNpcId,
                    slot,
                    "state",
                    out _),
                $"Transform Crystal rejects slot {slot}");
            Check.True(
                !TryCreateCombine(
                    GearMentorCombineGemPiecesCommandEnvelope
                        .AthensGearMentorNpcId,
                    slot,
                    "state",
                    out _),
                $"Combine Gem Pieces rejects slot {slot}");
        }
        foreach (var state in new[]
                 {
                     string.Empty,
                     "state\ninjection",
                     new string('é', 257),
                     "\ud800"
                 })
        {
            Check.True(
                !TryCreateTransform(
                    GearMentorTransformCrystalCommandEnvelope
                        .AthensGearMentorNpcId,
                    0,
                    state,
                    out _),
                "Transform Crystal rejects an invalid expected state");
            Check.True(
                !TryCreateCombine(
                    GearMentorCombineGemPiecesCommandEnvelope
                        .AthensGearMentorNpcId,
                    0,
                    state,
                    out _),
                "Combine Gem Pieces rejects an invalid expected state");
        }

        Check.True(
            !GearMentorTransformCrystalCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                GearMentorTransformCrystalCommandEnvelope
                    .AthensGearMentorNpcId,
                0,
                "state",
                out _),
            "Transform Crystal requires a client UUID");
        Check.True(
            !GearMentorCombineGemPiecesCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                GearMentorCombineGemPiecesCommandEnvelope
                    .AthensGearMentorNpcId,
                0,
                "state",
                out _),
            "Combine Gem Pieces requires a client UUID");

        Check.Equal(
            7,
            (int)CommandFamily.GearMentorTransformCrystal,
            "Transform Crystal command-family wire code");
        Check.Equal(
            8,
            (int)CommandFamily.GearMentorCombineGemPieces,
            "Combine Gem Pieces command-family wire code");
        foreach (var family in new[]
                 {
                     CommandFamily.GearMentorTransformCrystal,
                     CommandFamily.GearMentorCombineGemPieces
                 })
        {
            Check.Equal(
                (int)CommandIdentityStrength.ClientOperationId,
                (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                    family),
                $"{family} uses explicit client-operation identity");
        }
        Check.Equal(
            "gear_mentor_transform_crystal",
            CommandMetrics.FamilyCode(
                CommandFamily.GearMentorTransformCrystal),
            "Transform Crystal has a bounded metric family");
        Check.Equal(
            "gear_mentor_combine_gem_pieces",
            CommandMetrics.FamilyCode(
                CommandFamily.GearMentorCombineGemPieces),
            "Combine Gem Pieces has a bounded metric family");
    }

    private static void CheckCanonicalEnvelopes()
    {
        const string expectedState =
            "v1:item=4234;quantity=1;bound=1";
        var transform = CreateTransformEnvelope(
            ClientOperationId,
            GearMentorTransformCrystalCommandEnvelope
                .AthensGearMentorNpcId,
            12,
            expectedState,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var transformRetry = CreateTransformEnvelope(
            ClientOperationId,
            GearMentorTransformCrystalCommandEnvelope
                .AthensGearMentorNpcId,
            12,
            expectedState,
            Guid.NewGuid(),
            CommandTransportKind.SecureCommand);
        var combine = CreateCombineEnvelope(
            ClientOperationId,
            GearMentorCombineGemPiecesCommandEnvelope
                .AthensGearMentorNpcId,
            12,
            expectedState,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);

        Check.Equal(
            transform.OperationId,
            transformRetry.OperationId,
            "Transform operation identity survives reconnect");
        Check.Equal(
            transform.RequestHash,
            transformRetry.RequestHash,
            "Transform request hash survives transport replacement");
        Check.Equal(
            ExpectedRequestHash(
                CommandFamily.GearMentorTransformCrystal,
                transform.Command.NpcId,
                transform.Command.SelectedKitBagSlot,
                transform.Command.ExpectedCompactItemState),
            transform.RequestHash,
            "Transform canonical request uses network order");
        Check.Equal(
            ExpectedRequestHash(
                CommandFamily.GearMentorCombineGemPieces,
                combine.Command.NpcId,
                combine.Command.SelectedKitBagSlot,
                combine.Command.ExpectedCompactItemState),
            combine.RequestHash,
            "Combine canonical request uses network order");
        Check.True(
            !string.Equals(
                transform.OperationId,
                combine.OperationId,
                StringComparison.Ordinal),
            "command family separates UUID operation identities");
        Check.True(
            !string.Equals(
                transform.RequestHash,
                combine.RequestHash,
                StringComparison.Ordinal),
            "command family separates identical canonical requests");

        Check.Equal(
            transform.OperationId,
            GearMentorTransformCrystalCommandEnvelope.CreateOperationId(
                Subject,
                ClientOperationId),
            "Transform replay identity needs no selection context");
        Check.Equal(
            ExpectedOperationId(
                CommandFamily.GearMentorTransformCrystal,
                Subject,
                ClientOperationId),
            transform.OperationId,
            "Transform operation scope uses network-order UUID bytes");
        Check.Equal(
            combine.OperationId,
            GearMentorCombineGemPiecesCommandEnvelope.CreateOperationId(
                Subject,
                ClientOperationId),
            "Combine replay identity needs no selection context");
        Check.Equal(
            ExpectedOperationId(
                CommandFamily.GearMentorCombineGemPieces,
                Subject,
                ClientOperationId),
            combine.OperationId,
            "Combine operation scope uses network-order UUID bytes");
        Check.Throws<ArgumentException>(
            () =>
                GearMentorTransformCrystalCommandEnvelope
                    .CreateOperationId(Subject, Guid.Empty),
            "Transform replay rejects an empty UUID");
        Check.Throws<ArgumentException>(
            () =>
                GearMentorCombineGemPiecesCommandEnvelope
                    .CreateOperationId(Subject, Guid.Empty),
            "Combine replay rejects an empty UUID");
        Check.Throws<ArgumentOutOfRangeException>(
            () =>
                GearMentorTransformCrystalCommandEnvelope
                    .CreateOperationId(
                        new CommandSubject(0, Subject.CharacterId),
                        ClientOperationId),
            "Transform replay rejects an unauthenticated subject");
    }

    private static void CheckEnvelopeConflicts()
    {
        var transform = CreateTransformEnvelope(
            ClientOperationId,
            GearMentorTransformCrystalCommandEnvelope
                .AthensGearMentorNpcId,
            12,
            "state-a",
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var combine = CreateCombineEnvelope(
            ClientOperationId,
            GearMentorCombineGemPiecesCommandEnvelope
                .AthensGearMentorNpcId,
            12,
            "state-a",
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);

        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)GearMentorTransformCrystalCommandEnvelope.Validate(
                transform),
            "valid Transform envelope validates");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)GearMentorCombineGemPiecesCommandEnvelope.Validate(
                combine),
            "valid Combine envelope validates");

        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCorrelation,
            (int)GearMentorTransformCrystalCommandEnvelope.Validate(
                transform with
                {
                    Connection = new CommandConnectionCorrelation(
                        Guid.NewGuid(),
                        CommandTransportKind.LegacyTcp)
                }),
            "Transform rejects legacy-TCP UUID provenance");
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCorrelation,
            (int)GearMentorCombineGemPiecesCommandEnvelope.Validate(
                combine with
                {
                    Connection = new CommandConnectionCorrelation(
                        Guid.NewGuid(),
                        CommandTransportKind.LegacyTcp)
                }),
            "Combine rejects legacy-TCP UUID provenance");

        AssertTransformRequestConflict(
            transform with
            {
                Command = transform.Command with
                {
                    SelectedKitBagSlot = 13
                }
            },
            "Transform slot participates in request hash");
        AssertTransformRequestConflict(
            transform with
            {
                Command = transform.Command with
                {
                    ExpectedCompactItemState = "state-b"
                }
            },
            "Transform item state participates in request hash");
        AssertCombineRequestConflict(
            combine with
            {
                Command = combine.Command with
                {
                    NpcId =
                        GearMentorCombineGemPiecesCommandEnvelope
                            .SpartaGearMentorNpcId
                }
            },
            "Combine NPC participates in request hash");

        Check.Equal(
            (int)CommandEnvelopeValidation.OperationIdentityConflict,
            (int)GearMentorTransformCrystalCommandEnvelope.Validate(
                transform with
                {
                    Command = transform.Command with
                    {
                        ClientOperationId = Guid.NewGuid()
                    }
                }),
            "Transform changed UUID conflicts with operation identity");
        Check.Equal(
            (int)CommandEnvelopeValidation.OperationIdentityConflict,
            (int)GearMentorCombineGemPiecesCommandEnvelope.Validate(
                combine with
                {
                    Command = combine.Command with
                    {
                        ClientOperationId = Guid.NewGuid()
                    }
                }),
            "Combine changed UUID conflicts with operation identity");
    }

    private static bool TryCreateTransform(
        int npcId,
        int slot,
        string expectedState,
        out GearMentorTransformCrystalCommand command) =>
        GearMentorTransformCrystalCommandEnvelope.TryCreateCommand(
            ClientOperationId,
            npcId,
            slot,
            expectedState,
            out command);

    private static bool TryCreateCombine(
        int npcId,
        int slot,
        string expectedState,
        out GearMentorCombineGemPiecesCommand command) =>
        GearMentorCombineGemPiecesCommandEnvelope.TryCreateCommand(
            ClientOperationId,
            npcId,
            slot,
            expectedState,
            out command);

    private static CommandEnvelope<GearMentorTransformCrystalCommand>
        CreateTransformEnvelope(
            Guid operationId,
            int npcId,
            int slot,
            string expectedState,
            Guid connectionId,
            CommandTransportKind transport)
    {
        Check.True(
            GearMentorTransformCrystalCommandEnvelope.TryCreateCommand(
                operationId,
                npcId,
                slot,
                expectedState,
                out var command),
            "test Transform command is valid");
        return GearMentorTransformCrystalCommandEnvelope.Create(
            Subject,
            new CommandConnectionCorrelation(connectionId, transport),
            DateTimeOffset.UtcNow,
            command);
    }

    private static CommandEnvelope<GearMentorCombineGemPiecesCommand>
        CreateCombineEnvelope(
            Guid operationId,
            int npcId,
            int slot,
            string expectedState,
            Guid connectionId,
            CommandTransportKind transport)
    {
        Check.True(
            GearMentorCombineGemPiecesCommandEnvelope.TryCreateCommand(
                operationId,
                npcId,
                slot,
                expectedState,
                out var command),
            "test Combine command is valid");
        return GearMentorCombineGemPiecesCommandEnvelope.Create(
            Subject,
            new CommandConnectionCorrelation(connectionId, transport),
            DateTimeOffset.UtcNow,
            command);
    }

    private static void AssertTransformRequestConflict(
        CommandEnvelope<GearMentorTransformCrystalCommand> envelope,
        string description) =>
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)GearMentorTransformCrystalCommandEnvelope.Validate(
                envelope),
            description);

    private static void AssertCombineRequestConflict(
        CommandEnvelope<GearMentorCombineGemPiecesCommand> envelope,
        string description) =>
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)GearMentorCombineGemPiecesCommandEnvelope.Validate(
                envelope),
            description);

    private static string ExpectedRequestHash(
        CommandFamily family,
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
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(destination, 1);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[sizeof(ushort)..],
            npcId);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[(sizeof(ushort) + sizeof(int))..],
            checked((ushort)slot));
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[
                (sizeof(ushort) + sizeof(int) + sizeof(ushort))..],
            checked((ushort)stateBytes.Length));
        stateBytes.CopyTo(
            destination[
                (sizeof(ushort) +
                 sizeof(int) +
                 sizeof(ushort) +
                 sizeof(ushort))..]);
        return HashRequest(family, canonical);
    }

    private static string HashRequest(
        CommandFamily family,
        ReadOnlySpan<byte> canonical)
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
            (ushort)family);
        offset += sizeof(ushort);
        canonical.CopyTo(input.AsSpan(offset));
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
