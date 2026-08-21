using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Zodiac;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ZodiacSkillGridSelectionCommandContractChecks
{
    private static readonly CommandSubject Subject = new(347, 7);
    private static readonly Guid OperationId =
        Guid.Parse("76127b8b-76e6-4ef9-b69b-cba3acac7f02");

    public static Task RunAsync()
    {
        CheckEnvelopeIdentity();
        CheckManagedWireBoundary();
        CheckDomainPolicy();
        CheckDurableEvidence();
        return Task.CompletedTask;
    }

    private static void CheckManagedWireBoundary()
    {
        var packet = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            10_297);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8),
            0xFF);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10),
            102);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            1);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16),
            10_057);
        Check.True(
            ZodiacSyncRequest.TryParse(packet, out var request) &&
            request.IsSkillGridSelection,
            "managed route accepts exact native SID-102 intent");

        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            8);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16),
            10_025);
        Check.True(
            ZodiacSyncRequest.TryParse(packet, out request) &&
            request.IsSkillGridSelection &&
            request.Value1 == 8 &&
            request.Value2 == 10_025,
            "managed route preserves an all-class defense selection");

        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(20),
            1);
        Check.True(
            ZodiacSyncRequest.TryParse(packet, out request) &&
            !request.IsSkillGridSelection,
            "managed route rejects nonzero SID-102 tail");
    }

    private static void CheckEnvelopeIdentity()
    {
        var original = CreateEnvelope(0, 10_057);
        var reconnected = CreateEnvelope(
            0,
            10_057,
            Guid.NewGuid(),
            CommandTransportKind.SecureCommand);
        Check.Equal(
            original.OperationId,
            reconnected.OperationId,
            "Zodiac selection UUID survives reconnect");
        Check.Equal(
            original.RequestHash,
            reconnected.RequestHash,
            "Zodiac selection intent survives reconnect");

        var changedKind = CreateEnvelope(0, 10_050);
        Check.Equal(
            original.OperationId,
            changedKind.OperationId,
            "one native UUID remains one operation scope");
        Check.True(
            original.RequestHash != changedKind.RequestHash,
            "reusing a UUID for another selected Kind conflicts");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)ZodiacSkillGridSelectionCommandEnvelope.Validate(
                original with
                {
                    Command = original.Command with
                    {
                        SelectedSkillKind = 10_050
                    }
                }),
            "tampered Zodiac selection request hash fails closed");

        Check.True(
            ZodiacSkillGridSelectionCommandEnvelope.TryCreateCommand(
                OperationId,
                15,
                -1,
                out _),
            "last Zodiac grid accepts native clear intent");
        Check.True(
            !ZodiacSkillGridSelectionCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                0,
                10_057,
                out _) &&
            !ZodiacSkillGridSelectionCommandEnvelope.TryCreateCommand(
                OperationId,
                -1,
                10_057,
                out _) &&
            !ZodiacSkillGridSelectionCommandEnvelope.TryCreateCommand(
                OperationId,
                16,
                10_057,
                out _) &&
            !ZodiacSkillGridSelectionCommandEnvelope.TryCreateCommand(
                OperationId,
                0,
                -2,
                out _),
            "Zodiac selection rejects invalid native intent");
        Check.Throws<ArgumentOutOfRangeException>(
            () => CreateEnvelope(
                0,
                10_057,
                Guid.NewGuid(),
                CommandTransportKind.LegacyTcp),
            "raw legacy transport cannot invent a durable UUID");
    }

    private static void CheckDomainPolicy()
    {
        var mage = CreateCharacter(profession: 3);
        CheckStatus(
            mage,
            gridIndex: 0,
            selectedSkillKind: 10_057,
            learned: true,
            ZodiacSkillGridSelectionStatus.Succeeded,
            "learned class skill commits");
        Check.Equal(
            10_057,
            mage.ZodiacSkillGridSkillIds![0],
            "committed selection updates only the target grid");

        CheckStatus(
            mage,
            0,
            10_057,
            learned: true,
            ZodiacSkillGridSelectionStatus.AlreadySelected,
            "same selected Kind is terminal");
        CheckStatus(
            mage,
            1,
            10_057,
            learned: true,
            ZodiacSkillGridSelectionStatus.DuplicateSkillInRow,
            "same row rejects duplicate selected Kind");
        CheckStatus(
            mage,
            4,
            10_057,
            learned: true,
            ZodiacSkillGridSelectionStatus.SkillKindNotAllowedForGrid,
            "grid row enforces the Kind prefix");
        CheckStatus(
            mage,
            1,
            10_003,
            learned: true,
            ZodiacSkillGridSelectionStatus.SkillKindNotAllowedForClass,
            "selection enforces profession ownership");
        CheckStatus(
            mage,
            1,
            10_050,
            learned: false,
            ZodiacSkillGridSelectionStatus.SkillNotLearned,
            "selection requires a learned runtime family");

        CheckStatus(
            mage,
            8,
            10_025,
            learned: false,
            ZodiacSkillGridSelectionStatus.Succeeded,
            "defense row accepts an enemy profession attack skill");
        CheckStatus(
            mage,
            12,
            20_028,
            learned: false,
            ZodiacSkillGridSelectionStatus.Succeeded,
            "second defense row accepts an enemy profession skill");
        CheckStatus(
            mage,
            9,
            10_001,
            learned: false,
            ZodiacSkillGridSelectionStatus.SkillKindNotAllowedForClass,
            "defense row still rejects a Kind absent from SkillChoice");

        var inactive = CreateCharacter(profession: 3);
        inactive.ZodiacSkillGridLevels![8] = 0;
        CheckStatus(
            inactive,
            8,
            10_057,
            learned: true,
            ZodiacSkillGridSelectionStatus.InactiveGrid,
            "inactive grid cannot select a skill");

        CheckStatus(
            mage,
            0,
            -1,
            learned: true,
            ZodiacSkillGridSelectionStatus.Succeeded,
            "native minus-one intent clears a selection");
    }

    private static void CheckDurableEvidence()
    {
        var eventId =
            Guid.Parse("bd410544-7e5f-46a0-846d-a6318345aab2");
        var receipt = new ZodiacSkillGridSelectionExecutionReceipt(
            characterId: 7,
            ZodiacSkillGridSelectionReceiptStatus.Succeeded,
            gridIndex: 0,
            currentLevel: 1,
            previousSkillKind: -1,
            selectedSkillKind: 10_057,
            aggregateRevision: 1,
            auditReference: "91",
            outboxEventId: eventId);
        var encoded =
            ZodiacSkillGridSelectionPersistenceCodec.Encode(receipt);
        var decoded =
            ZodiacSkillGridSelectionPersistenceCodec.Decode(encoded);
        Check.Equal(
            receipt,
            decoded,
            "Zodiac selection evidence round-trips exactly");
        Check.Equal(
            decoded,
            ZodiacSkillGridSelectionPersistenceCodec.DecodeAndVerify(
                System.Text.Encoding.UTF8.GetString(encoded),
                ZodiacSkillGridSelectionPersistenceCodec.Hash(encoded),
                ZodiacSkillGridSelectionPersistenceCodec
                    .CommittedResultCode,
                expectedAuditId: 91),
            "Zodiac selection durable hash verifies");

        var committed =
            ZodiacSkillGridSelectionExecutionResult.Committed(receipt);
        Check.True(
            committed.HasAuthoritativeProjection &&
            committed.CurrentLevel == 1 &&
            committed.SelectedSkillKind == 10_057 &&
            committed.CurrentRevision == 1,
            "committed result carries authoritative projection");
        var replayed =
            ZodiacSkillGridSelectionExecutionResult.Duplicate(
                receipt,
                currentLevel: 2,
                selectedSkillKind: 10_050,
                currentRevision: 3);
        Check.True(
            replayed.Receipt == receipt &&
            replayed.SelectedSkillKind == 10_050 &&
            replayed.CurrentRevision == 3,
            "replay separates immutable receipt from current projection");

        var rejected = new ZodiacSkillGridSelectionExecutionReceipt(
            characterId: 7,
            ZodiacSkillGridSelectionReceiptStatus.SkillNotLearned,
            gridIndex: 0,
            currentLevel: 1,
            previousSkillKind: -1,
            selectedSkillKind: -1,
            aggregateRevision: null,
            auditReference: "92",
            outboxEventId: null);
        Check.True(
            ZodiacSkillGridSelectionExecutionResult
                .TerminalRejected(rejected)
                .HasAuthoritativeProjection,
            "terminal rejection carries authoritative grid state");
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridSelectionPersistenceCodec.Decode(
                [0x7B, 0x7D]),
            "receipt decoder rejects missing evidence");
        Check.Throws<InvalidDataException>(
            () => ZodiacSkillGridSelectionPersistenceCodec.Decode(
                System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Encoding.UTF8.GetString(encoded)
                        .Replace(
                            "\"status\":1",
                            "\"status\":99",
                            StringComparison.Ordinal))),
            "receipt decoder rejects unknown outcome status");
    }

    private static CommandEnvelope<ZodiacSkillGridSelectionCommand>
        CreateEnvelope(
            int gridIndex,
            int selectedSkillKind,
            Guid? connectionId = null,
            CommandTransportKind transport =
                CommandTransportKind.SecureTlsLegacy)
    {
        if (!ZodiacSkillGridSelectionCommandEnvelope.TryCreateCommand(
                OperationId,
                gridIndex,
                selectedSkillKind,
                out var command))
        {
            throw new InvalidOperationException(
                "The test requested invalid Zodiac selection intent.");
        }

        return ZodiacSkillGridSelectionCommandEnvelope.Create(
            Subject,
            new CommandConnectionCorrelation(
                connectionId ?? Guid.NewGuid(),
                transport),
            DateTimeOffset.UtcNow,
            command);
    }

    private static GameCharacter CreateCharacter(byte profession)
    {
        var character = new GameCharacter
        {
            Profession = profession,
            ZodiacSkillGridLevels =
                ZodiacSkillGridCatalog.CreateEmptyLevels(),
            ZodiacSkillGridSkillIds =
                ZodiacSkillGridCatalog.CreateEmptySkillIds()
        };
        for (var index = 0;
             index < ZodiacSkillGridCatalog.GridCount;
             index++)
        {
            character.ZodiacSkillGridLevels[index] = 1;
        }

        return character;
    }

    private static void CheckStatus(
        GameCharacter character,
        int gridIndex,
        int selectedSkillKind,
        bool learned,
        ZodiacSkillGridSelectionStatus expected,
        string description)
    {
        var result = ZodiacSkillGridSelection.Apply(
            character,
            gridIndex,
            selectedSkillKind,
            learned);
        Check.Equal(
            (int)expected,
            (int)result.Status,
            description);
    }
}
