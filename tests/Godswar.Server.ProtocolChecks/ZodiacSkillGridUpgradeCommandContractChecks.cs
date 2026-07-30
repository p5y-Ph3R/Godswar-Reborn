using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;

namespace Godswar.Server.ProtocolChecks;

internal static class ZodiacSkillGridUpgradeCommandContractChecks
{
    private static readonly CommandSubject Subject = new(347, 7);
    private static readonly Guid ClientOperationId =
        Guid.Parse("90228554-64e3-4cce-8e8b-104da5051550");

    public static Task RunAsync()
    {
        CheckNativeOperationIdentity();
        CheckIntentAndEnvelopeValidation();
        CheckReceiptEvidence();
        CheckExecutionEvidence();
        CheckPolicyAndMetrics();
        return Task.CompletedTask;
    }

    private static void CheckNativeOperationIdentity()
    {
        var original = CreateEnvelope(
            ClientOperationId,
            gridIndex: 1,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var reconnected = CreateEnvelope(
            ClientOperationId,
            gridIndex: 1,
            Guid.NewGuid(),
            CommandTransportKind.SecureCommand);

        Check.Equal(
            original.OperationId,
            reconnected.OperationId,
            "Zodiac upgrade identity survives reconnect");
        Check.Equal(
            original.RequestHash,
            reconnected.RequestHash,
            "Zodiac upgrade request survives reconnect");
        Check.Equal(
            original.OperationId,
            ZodiacSkillGridUpgradeCommandEnvelope.CreateOperationId(
                Subject,
                ClientOperationId),
            "Zodiac upgrade operation identity is reproducible");

        var otherGrid = CreateEnvelope(
            ClientOperationId,
            gridIndex: 2,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        Check.Equal(
            original.OperationId,
            otherGrid.OperationId,
            "one native UUID remains one operation scope");
        Check.True(
            original.RequestHash != otherGrid.RequestHash,
            "reusing a native UUID for another grid conflicts");

        var otherOperation = CreateEnvelope(
            Guid.Parse("2a85b812-1d46-43b9-8e96-0c7b4db2cd4f"),
            gridIndex: 1,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        Check.True(
            original.OperationId != otherOperation.OperationId,
            "another native UUID creates another upgrade operation");
        Check.Equal(
            original.RequestHash,
            otherOperation.RequestHash,
            "operation UUID is excluded from canonical grid intent");

        var otherCharacter = CreateEnvelope(
            ClientOperationId,
            gridIndex: 1,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy,
            new CommandSubject(347, 8));
        Check.True(
            original.OperationId != otherCharacter.OperationId,
            "operation identity binds the authenticated character");
        Check.Equal(
            original.RequestHash,
            otherCharacter.RequestHash,
            "canonical request excludes authenticated identity");
    }

    private static void CheckIntentAndEnvelopeValidation()
    {
        Check.True(
            ZodiacSkillGridUpgradeCommandEnvelope.TryCreateCommand(
                ClientOperationId,
                0,
                out _),
            "first Zodiac grid is valid upgrade intent");
        Check.True(
            ZodiacSkillGridUpgradeCommandEnvelope.TryCreateCommand(
                ClientOperationId,
                15,
                out _),
            "last Zodiac grid is valid upgrade intent");
        Check.True(
            !ZodiacSkillGridUpgradeCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                1,
                out _),
            "Zodiac upgrade requires a native operation UUID");
        Check.True(
            !ZodiacSkillGridUpgradeCommandEnvelope.TryCreateCommand(
                ClientOperationId,
                -1,
                out _),
            "negative Zodiac grid is invalid intent");
        Check.True(
            !ZodiacSkillGridUpgradeCommandEnvelope.TryCreateCommand(
                ClientOperationId,
                16,
                out _),
            "out-of-catalog Zodiac grid is invalid intent");

        var envelope = CreateEnvelope(
            ClientOperationId,
            gridIndex: 1,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)ZodiacSkillGridUpgradeCommandEnvelope.Validate(envelope),
            "authenticated Zodiac upgrade envelope validates");
        Check.Throws<ArgumentException>(
            () => CreateEnvelope(
                ClientOperationId,
                gridIndex: 1,
                Guid.NewGuid(),
                CommandTransportKind.LegacyTcp),
            "raw legacy transport cannot invent an operation UUID");
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCorrelation,
            (int)ZodiacSkillGridUpgradeCommandEnvelope.Validate(
                envelope with
                {
                    Connection = envelope.Connection with
                    {
                        Transport = CommandTransportKind.LegacyTcp
                    }
                }),
            "tampered raw transport provenance fails closed");
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCommand,
            (int)ZodiacSkillGridUpgradeCommandEnvelope.Validate(
                envelope with
                {
                    Command = envelope.Command with
                    {
                        ClientOperationId = Guid.Empty
                    }
                }),
            "empty operation UUID is rejected before persistence");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)ZodiacSkillGridUpgradeCommandEnvelope.Validate(
                envelope with
                {
                    Command = envelope.Command with
                    {
                        GridIndex = 2
                    }
                }),
            "changed grid conflicts with the canonical request");
        Check.Equal(
            (int)CommandEnvelopeValidation.OperationIdentityConflict,
            (int)ZodiacSkillGridUpgradeCommandEnvelope.Validate(
                envelope with
                {
                    Command = envelope.Command with
                    {
                        ClientOperationId =
                            Guid.Parse(
                                "2a85b812-1d46-43b9-8e96-0c7b4db2cd4f")
                    }
                }),
            "changed UUID conflicts only with operation identity");
        Check.Equal(
            (int)CommandEnvelopeValidation.OperationIdentityConflict,
            (int)ZodiacSkillGridUpgradeCommandEnvelope.Validate(
                envelope with
                {
                    Subject = new CommandSubject(347, 8)
                }),
            "operation digest is bound to authenticated ownership");
        Check.Throws<ArgumentException>(
            () => ZodiacSkillGridUpgradeCommandEnvelope.CreateOperationId(
                Subject,
                Guid.Empty),
            "operation derivation rejects an empty UUID");
    }

    private static void CheckReceiptEvidence()
    {
        var receipt = CreateReceipt();
        Check.Equal(
            (int)CommandFamily.ZodiacSkillGridUpgrade,
            (int)receipt.Family,
            "receipt identifies the Zodiac upgrade family");
        Check.True(
            receipt.AggregateRevision == 2,
            "committed grid level is the per-grid aggregate revision");

        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                energyAfter: 96,
                energyRemainderAfterX100: 37),
            "receipt enforces exact Zodiac-energy arithmetic");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(
                energyAfter: 95,
                energyRemainderAfterX100: 38),
            "receipt preserves the exact fractional energy remainder");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(talentPointsAfter: 94),
            "receipt enforces exact Talent Point arithmetic");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(currentLevel: 3),
            "receipt requires exactly one grid-level increment");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(previousLevel: 50, currentLevel: 50),
            "maximum grid level cannot be upgraded");
        Check.Throws<ArgumentException>(
            () => CreateReceipt(outboxEventId: Guid.Empty),
            "committed upgrade requires outbox evidence");
        Check.Throws<ArgumentOutOfRangeException>(
            () => CreateReceipt(auditReference: "audit\nforged"),
            "audit reference rejects control characters");

        var inactive = CreateRejectedReceipt(
            ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid);
        var maximum = CreateRejectedReceipt(
            ZodiacSkillGridUpgradeReceiptStatus.MaximumLevelReached);
        var gated = CreateRejectedReceipt(
            ZodiacSkillGridUpgradeReceiptStatus.ZodiacLevelTooLow);
        var insufficientEnergy = CreateRejectedReceipt(
            ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy);
        var insufficientTalent = CreateRejectedReceipt(
            ZodiacSkillGridUpgradeReceiptStatus
                .InsufficientTalentPoints);
        Check.True(
            !inactive.Succeeded &&
            !maximum.Succeeded &&
            !gated.Succeeded &&
            !insufficientEnergy.Succeeded &&
            !insufficientTalent.Succeeded &&
            inactive.OutboxEventId is null &&
            insufficientTalent.AggregateRevision is null,
            "all authoritative state rejections are durable without mutation");
        Check.Throws<ArgumentException>(
            () => CreateRejectedReceipt(
                ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy,
                outboxEventId: Guid.NewGuid()),
            "terminal rejection cannot carry outbox evidence");
        Check.Throws<ArgumentException>(
            () => CreateRejectedReceipt(
                ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy,
                energyBefore: 5),
            "insufficient-energy receipt proves the shortage");
    }

    private static void CheckExecutionEvidence()
    {
        var receipt = CreateReceipt();
        var committed =
            ZodiacSkillGridUpgradeExecutionResult.Committed(receipt);
        Check.True(
            committed.IsSuccess &&
            committed.IsDurable &&
            committed.HasAuthoritativeProjection,
            "committed upgrade carries durable authoritative evidence");
        Check.Equal(
            95,
            committed.CurrentEnergy,
            "committed projection matches receipt energy");
        Check.Equal(
            93,
            committed.CurrentTalentPoints,
            "committed projection matches receipt Talent Points");

        var duplicate =
            ZodiacSkillGridUpgradeExecutionResult.Duplicate(
                receipt,
                currentEnergy: 1_400,
                currentEnergyRemainderX100: 12,
                currentTalentPoints: 2_900,
                currentLevel: 4,
                selectedSkillId: 10_057);
        Check.Equal(
            95,
            duplicate.Receipt!.EnergyAfter,
            "duplicate preserves historical committed energy");
        Check.Equal(
            1_400,
            duplicate.CurrentEnergy,
            "duplicate carries the latest authoritative energy");
        Check.Equal(
            4,
            (int)duplicate.CurrentLevel,
            "duplicate carries a later grid projection");

        var rejectedReceipt = CreateRejectedReceipt(
            ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy);
        var terminal =
            ZodiacSkillGridUpgradeExecutionResult.TerminalRejected(
                rejectedReceipt);
        Check.True(
            terminal.IsDurable &&
            !terminal.IsSuccess &&
            terminal.HasAuthoritativeProjection &&
            terminal.Receipt!.OutboxEventId is null,
            "state rejection is permanent but has no mutation event");
        var rejectedReplay =
            ZodiacSkillGridUpgradeExecutionResult.Duplicate(
                rejectedReceipt,
                currentEnergy: 100,
                currentEnergyRemainderX100: 0,
                currentTalentPoints: 100,
                currentLevel: 2,
                selectedSkillId: -1);
        Check.True(
            rejectedReplay.IsDurable &&
            !rejectedReplay.IsSuccess,
            "replaying a rejected UUID cannot become a success");
        Check.True(
            !ZodiacSkillGridUpgradeExecutionResult
                .PreconditionFailed().HasAuthoritativeProjection,
            "missing ownership does not fabricate character state");
        Check.True(
            !ZodiacSkillGridUpgradeExecutionResult
                .RequestHashConflict().IsDurable,
            "request conflict has no mutation evidence");

        Check.Throws<ArgumentException>(
            () => ZodiacSkillGridUpgradeExecutionResult.Duplicate(
                receipt,
                currentEnergy: 95,
                currentEnergyRemainderX100: 37,
                currentTalentPoints: 93,
                currentLevel: 1,
                selectedSkillId: -1),
            "duplicate projection cannot predate its receipt");
        Check.Throws<ArgumentException>(
            () => ZodiacSkillGridUpgradeExecutionResult.Committed(
                rejectedReceipt),
            "terminal receipt cannot masquerade as a commit");
        Check.Throws<ArgumentException>(
            () => ZodiacSkillGridUpgradeExecutionResult.TerminalRejected(
                receipt),
            "successful receipt cannot masquerade as rejection");
    }

    private static void CheckPolicyAndMetrics()
    {
        Check.Equal(
            (int)CommandIdentityStrength.ClientOperationId,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.ZodiacSkillGridUpgrade),
            "repeatable Zodiac upgrades require native UUID identity");
        Check.Equal(
            "zodiac_skill_grid_upgrade",
            CommandMetrics.FamilyCode(
                CommandFamily.ZodiacSkillGridUpgrade),
            "Zodiac upgrade metric label is bounded");
    }

    private static CommandEnvelope<ZodiacSkillGridUpgradeCommand>
        CreateEnvelope(
            Guid operationId,
            int gridIndex,
            Guid connectionId,
            CommandTransportKind transport,
            CommandSubject? subject = null)
    {
        Check.True(
            ZodiacSkillGridUpgradeCommandEnvelope.TryCreateCommand(
                operationId,
                gridIndex,
                out var command),
            "test Zodiac upgrade command is valid");
        return ZodiacSkillGridUpgradeCommandEnvelope.Create(
            subject ?? Subject,
            new CommandConnectionCorrelation(connectionId, transport),
            DateTimeOffset.UtcNow,
            command);
    }

    private static ZodiacSkillGridUpgradeExecutionReceipt CreateReceipt(
        byte previousLevel = 1,
        byte currentLevel = 2,
        int energyAfter = 95,
        int energyRemainderAfterX100 = 37,
        int talentPointsAfter = 93,
        string auditReference = "audit:zodiac-grid-upgrade:1",
        Guid? outboxEventId = null) =>
        new(
            characterId: 7,
            status: ZodiacSkillGridUpgradeReceiptStatus.Succeeded,
            gridIndex: 1,
            previousLevel,
            currentLevel,
            currentZodiacLevel: 1,
            requiredZodiacLevel: 1,
            energyCost: 5,
            energyBefore: 100,
            energyRemainderBeforeX100: 37,
            energyAfter,
            energyRemainderAfterX100,
            talentPointCost: 7,
            talentPointsBefore: 100,
            talentPointsAfter,
            selectedSkillId: -1,
            auditReference,
            outboxEventId ?? Guid.NewGuid());

    private static ZodiacSkillGridUpgradeExecutionReceipt
        CreateRejectedReceipt(
            ZodiacSkillGridUpgradeReceiptStatus status,
            int? energyBefore = null,
            Guid? outboxEventId = null)
    {
        var previousLevel =
            status == ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid
                ? (byte)0
                : status ==
                    ZodiacSkillGridUpgradeReceiptStatus.MaximumLevelReached
                    ? (byte)50
                    : (byte)1;
        var requiredZodiacLevel = status is
            ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid or
            ZodiacSkillGridUpgradeReceiptStatus.MaximumLevelReached
                ? (byte)0
                : status ==
                    ZodiacSkillGridUpgradeReceiptStatus.ZodiacLevelTooLow
                    ? (byte)2
                    : (byte)1;
        var currentZodiacLevel = (byte)1;
        var energyCost = requiredZodiacLevel == 0 ? 0 : 5;
        var talentCost = requiredZodiacLevel == 0 ? 0 : 7;
        var resolvedEnergy = energyBefore ??
            (status ==
                ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy
                ? 4
                : 100);
        var talentPoints = status ==
            ZodiacSkillGridUpgradeReceiptStatus.InsufficientTalentPoints
                ? 6
                : 100;
        return new ZodiacSkillGridUpgradeExecutionReceipt(
            characterId: 7,
            status,
            gridIndex: 1,
            previousLevel,
            currentLevel: previousLevel,
            currentZodiacLevel,
            requiredZodiacLevel,
            energyCost,
            energyBefore: resolvedEnergy,
            energyRemainderBeforeX100: 0,
            energyAfter: resolvedEnergy,
            energyRemainderAfterX100: 0,
            talentPointCost: talentCost,
            talentPointsBefore: talentPoints,
            talentPointsAfter: talentPoints,
            selectedSkillId: -1,
            auditReference: "audit:zodiac-grid-upgrade:rejected",
            outboxEventId);
    }
}
