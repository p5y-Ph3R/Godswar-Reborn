using System.Diagnostics.Metrics;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Characters;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerOwnershipContractChecks
{
    public static Task RunAsync()
    {
        var owner = new PlayerOwnershipFence(Guid.NewGuid(), 7);
        owner.Validate();
        Check.True(owner.IsValid, "positive non-empty fence is valid");
        Check.True(!default(PlayerOwnershipFence).IsValid,
            "default fence is invalid");

        var envelope = new CommandEnvelope<int>(
            CommandEnvelopeContract.CurrentVersion,
            CommandFamily.TalentUpgrade,
            CommandIdentityStrength.ClientOperationId,
            new CommandSubject(3, 7),
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureCommand),
            DateTimeOffset.UtcNow,
            new string('A', CommandEnvelopeContract.DigestHexLength),
            new string('B', CommandEnvelopeContract.DigestHexLength),
            1);
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidOwnership,
            (int)CommandEnvelopeContract.ValidateOwnership(envelope),
            "new command envelope is unbound by default");

        var bound =
            CommandEnvelopeContract.BindOwnership(envelope, owner);
        Check.Equal(
            owner,
            bound.Ownership,
            "binding installs the exact player fence");
        Check.Equal(
            envelope.OperationId,
            bound.OperationId,
            "ownership binding does not change operation identity");
        Check.Equal(
            envelope.RequestHash,
            bound.RequestHash,
            "ownership binding does not change request identity");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)CommandEnvelopeContract.ValidateOwnership(bound),
            "bound command ownership validates");

        ExpectThrows<ArgumentException>(
            () => CommandEnvelopeContract.BindOwnership(
                envelope,
                default),
            "default ownership cannot be bound");
        ExpectThrows<ArgumentException>(
            () => CommandEnvelopeContract.BindOwnership(
                envelope with
                {
                    Subject = new CommandSubject(
                        envelope.Subject.AccountId,
                        CharacterId: 0)
                },
                owner),
            "account-only lifecycle envelope cannot bind a character fence");

        AssertMetricDimensionsAreBounded();

        var missing = new PlayerOwnershipValidationResult(
            PlayerOwnershipValidationStatus.CharacterNotFound,
            StoredGeneration: null);
        try
        {
            missing.RequireCurrent();
        }
        catch (PlayerOwnershipValidationException error)
            when (error.Status ==
                  PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            "Assertion failed: missing ownership result raises its exact " +
            "rejection status.");
    }

    private static void AssertMetricDimensionsAreBounded()
    {
        var measurements =
            new List<IReadOnlyDictionary<string, string?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name ==
                    PostgresPlayerOwnershipMetrics.MeterName &&
                instrument.Name ==
                    "godswar_player_ownership_validations_total")
            {
                activeListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, _, tags, _) =>
            {
                var captured =
                    new Dictionary<string, string?>(
                        StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    captured[tag.Key] = tag.Value?.ToString();
                }
                measurements.Add(captured);
            });
        listener.Start();

        PostgresPlayerOwnershipMetrics.Record(
            PlayerOwnershipValidationStage.Transaction,
            PlayerOwnershipValidationStatus.Current);
        PostgresPlayerOwnershipMetrics.Record(
            PlayerOwnershipValidationStage.PostCommit,
            PlayerOwnershipValidationStatus.OwnershipLost);
        PostgresPlayerOwnershipMetrics.Record(
            PlayerOwnershipValidationStage.PostCommit,
            PlayerOwnershipValidationStatus.CharacterNotFound);

        Check.Equal(3, measurements.Count,
            "ownership metrics record each validation");
        var expectedStages = new HashSet<string>(
            ["transaction", "post_commit"],
            StringComparer.Ordinal);
        var expectedOutcomes = new HashSet<string>(
            ["current", "ownership_lost", "character_not_found"],
            StringComparer.Ordinal);
        foreach (var tags in measurements)
        {
            Check.Equal(2, tags.Count,
                "ownership metric has only bounded dimensions");
            Check.True(
                tags.TryGetValue("stage", out var stage) &&
                stage is not null &&
                expectedStages.Contains(stage),
                "ownership metric stage is from a bounded set");
            Check.True(
                tags.TryGetValue("outcome", out var outcome) &&
                outcome is not null &&
                expectedOutcomes.Contains(outcome),
                "ownership metric outcome is from a bounded set");
        }
    }

    private static void ExpectThrows<TException>(
        Action action,
        string description)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}.");
    }
}
