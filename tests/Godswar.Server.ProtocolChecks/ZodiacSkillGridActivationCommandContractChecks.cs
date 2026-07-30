using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;

namespace Godswar.Server.ProtocolChecks;

internal static class ZodiacSkillGridActivationCommandContractChecks
{
    private static readonly CommandSubject Subject = new(347, 7);

    public static Task RunAsync()
    {
        CheckStableTransitionIdentity();
        CheckStrictIntentAndEnvelopeValidation();
        CheckReceiptAndProjectionEvidence();
        CheckPolicyAndMetrics();
        return Task.CompletedTask;
    }

    private static void CheckStableTransitionIdentity()
    {
        var original = CreateEnvelope(
            gridIndex: 1,
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var reconnected = CreateEnvelope(
            gridIndex: 1,
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);

        Check.Equal(
            original.OperationId,
            reconnected.OperationId,
            "Zodiac activation identity survives reconnect");
        Check.Equal(
            original.RequestHash,
            reconnected.RequestHash,
            "Zodiac activation request survives reconnect");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)ZodiacSkillGridActivationCommandEnvelope.Validate(
                reconnected),
            "reconnected Zodiac activation remains valid");

        var otherGrid = CreateEnvelope(
            gridIndex: 2,
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        Check.True(
            original.OperationId != otherGrid.OperationId,
            "another grid is another inactive-to-active transition");
        Check.True(
            original.RequestHash != otherGrid.RequestHash,
            "canonical Zodiac request binds the grid");

        var otherCharacter = CreateEnvelope(
            gridIndex: 1,
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp,
            new CommandSubject(347, 8));
        Check.True(
            original.OperationId != otherCharacter.OperationId,
            "authenticated character participates in activation identity");
        Check.Equal(
            original.RequestHash,
            otherCharacter.RequestHash,
            "canonical request excludes authenticated identity");
    }

    private static void CheckStrictIntentAndEnvelopeValidation()
    {
        Check.True(
            ZodiacSkillGridActivationCommandEnvelope.TryCreateCommand(
                0,
                expectedLevel: 0,
                out _),
            "first Zodiac grid is valid");
        Check.True(
            ZodiacSkillGridActivationCommandEnvelope.TryCreateCommand(
                15,
                expectedLevel: 0,
                out _),
            "last Zodiac grid is valid");
        Check.True(
            !ZodiacSkillGridActivationCommandEnvelope.TryCreateCommand(
                -1,
                expectedLevel: 0,
                out _),
            "negative Zodiac grid is invalid intent");
        Check.True(
            !ZodiacSkillGridActivationCommandEnvelope.TryCreateCommand(
                16,
                expectedLevel: 0,
                out _),
            "out-of-catalog Zodiac grid is invalid intent");
        Check.True(
            !ZodiacSkillGridActivationCommandEnvelope.TryCreateCommand(
                1,
                expectedLevel: 1,
                out _),
            "activation only represents inactive-to-active");

        var envelope = CreateEnvelope(
            gridIndex: 1,
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCommand,
            (int)ZodiacSkillGridActivationCommandEnvelope.Validate(
                envelope with
                {
                    Command = envelope.Command with
                    {
                        ExpectedLevel = 1
                    }
                }),
            "changed expected level is rejected before persistence");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)ZodiacSkillGridActivationCommandEnvelope.Validate(
                envelope with
                {
                    Command = envelope.Command with
                    {
                        GridIndex = 2
                    }
                }),
            "changed grid conflicts with canonical request");
        Check.Equal(
            (int)CommandEnvelopeValidation.OperationIdentityConflict,
            (int)ZodiacSkillGridActivationCommandEnvelope.Validate(
                envelope with
                {
                    Subject = new CommandSubject(347, 8)
                }),
            "operation digest is bound to authenticated character");
    }

    private static void CheckReceiptAndProjectionEvidence()
    {
        var eventId = Guid.NewGuid();
        var paid = new ZodiacSkillGridActivationExecutionReceipt(
            characterId: 7,
            gridIndex: 1,
            goldCost: 2_300,
            goldBefore: 5_000,
            goldAfter: 2_700,
            currentLevel: 1,
            selectedSkillId: -1,
            walletRevision: 1,
            auditReference: "audit:zodiac-grid:1",
            outboxEventId: eventId);
        var committed =
            ZodiacSkillGridActivationExecutionResult.Committed(paid);
        Check.True(
            committed.IsSuccess &&
            committed.IsDurable &&
            committed.HasAuthoritativeProjection,
            "committed activation carries durable current projection");
        Check.Equal(
            2_700,
            committed.CurrentGold,
            "committed projection matches historical Gold");

        var duplicate =
            ZodiacSkillGridActivationExecutionResult.Duplicate(
                paid,
                currentGold: 1_900,
                currentLevel: 4,
                selectedSkillId: 10_057,
                currentWalletRevision: 9);
        Check.Equal(
            2_700,
            duplicate.Receipt!.GoldAfter,
            "duplicate preserves historical receipt Gold");
        Check.Equal(
            1_900,
            duplicate.CurrentGold,
            "duplicate carries newer authoritative Gold");
        Check.Equal(
            4,
            (int)duplicate.CurrentLevel,
            "duplicate carries newer authoritative grid level");

        var free = new ZodiacSkillGridActivationExecutionReceipt(
            characterId: 7,
            gridIndex: 0,
            goldCost: 0,
            goldBefore: 5_000,
            goldAfter: 5_000,
            currentLevel: 1,
            selectedSkillId: -1,
            walletRevision: 0,
            auditReference: "audit:zodiac-grid:free",
            outboxEventId: Guid.NewGuid());
        Check.True(
            ZodiacSkillGridActivationExecutionResult
                .Committed(free).IsSuccess,
            "free activation need not fabricate a wallet revision");

        var transient =
            ZodiacSkillGridActivationExecutionResult.PreconditionFailed(
                currentGold: 2_299,
                currentLevel: 0,
                selectedSkillId: -1,
                currentWalletRevision: 3);
        Check.True(
            !transient.IsDurable &&
            transient.HasAuthoritativeProjection &&
            transient.CurrentGold == 2_299,
            "insufficient Gold remains retryable with current projection");
        Check.True(
            !ZodiacSkillGridActivationExecutionResult
                .PreconditionFailed().HasAuthoritativeProjection,
            "missing ownership can fail without fabricating projection");

        Check.Throws<ArgumentOutOfRangeException>(
            () => new ZodiacSkillGridActivationExecutionReceipt(
                7,
                1,
                2_300,
                5_000,
                2_701,
                1,
                -1,
                1,
                "audit:zodiac-grid:bad-arithmetic",
                Guid.NewGuid()),
            "receipt enforces exact Gold arithmetic");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new ZodiacSkillGridActivationExecutionReceipt(
                7,
                1,
                2_300,
                5_000,
                2_700,
                1,
                -1,
                0,
                "audit:zodiac-grid:bad-revision",
                Guid.NewGuid()),
            "paid activation requires a wallet revision");
        Check.Throws<ArgumentException>(
            () => ZodiacSkillGridActivationExecutionResult.Duplicate(
                paid,
                currentGold: 5_000,
                currentLevel: 0,
                selectedSkillId: -1,
                currentWalletRevision: 1),
            "duplicate projection cannot predate activation");
        Check.Throws<ArgumentException>(
            () => new ZodiacSkillGridActivationExecutionResult(
                ZodiacSkillGridActivationExecutionDisposition.Committed,
                paid),
            "durable result cannot omit current projection");
    }

    private static void CheckPolicyAndMetrics()
    {
        Check.Equal(
            (int)CommandIdentityStrength.LegacyAggregateVersion,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.ZodiacSkillGridActivation),
            "Zodiac activation uses its one-time aggregate transition");
        Check.Equal(
            "zodiac_skill_grid_activation",
            CommandMetrics.FamilyCode(
                CommandFamily.ZodiacSkillGridActivation),
            "Zodiac activation metric label is bounded");
    }

    private static CommandEnvelope<ZodiacSkillGridActivationCommand>
        CreateEnvelope(
            int gridIndex,
            Guid connectionId,
            CommandTransportKind transport,
            CommandSubject? subject = null)
    {
        Check.True(
            ZodiacSkillGridActivationCommandEnvelope.TryCreateCommand(
                gridIndex,
                expectedLevel: 0,
                out var command),
            "test activation command is valid");
        return ZodiacSkillGridActivationCommandEnvelope.Create(
            subject ?? Subject,
            new CommandConnectionCorrelation(
                connectionId,
                transport),
            DateTimeOffset.UtcNow,
            command);
    }
}
