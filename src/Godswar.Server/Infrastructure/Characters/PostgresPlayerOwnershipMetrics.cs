using System.Diagnostics;
using System.Diagnostics.Metrics;
using Godswar.Server.Application.Characters;

namespace Godswar.Server.Infrastructure.Characters;

internal enum PlayerOwnershipValidationStage : byte
{
    Transaction = 1,
    PostCommit = 2
}

internal static class PostgresPlayerOwnershipMetrics
{
    public const string MeterName =
        "Godswar.Server.Infrastructure.PlayerOwnership";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Validations =
        Meter.CreateCounter<long>(
            "godswar_player_ownership_validations_total",
            description:
            "PostgreSQL player ownership validation outcomes.");

    public static void Record(
        PlayerOwnershipValidationStage stage,
        PlayerOwnershipValidationStatus status)
    {
        Validations.Add(
            1,
            new TagList
            {
                { "stage", Stage(stage) },
                { "outcome", Outcome(status) }
            });
    }

    private static string Stage(PlayerOwnershipValidationStage stage) =>
        stage switch
        {
            PlayerOwnershipValidationStage.Transaction =>
                "transaction",
            PlayerOwnershipValidationStage.PostCommit =>
                "post_commit",
            _ => throw new ArgumentOutOfRangeException(
                nameof(stage),
                stage,
                "Unsupported ownership validation stage.")
        };

    private static string Outcome(
        PlayerOwnershipValidationStatus status) =>
        status switch
        {
            PlayerOwnershipValidationStatus.Current => "current",
            PlayerOwnershipValidationStatus.OwnershipLost =>
                "ownership_lost",
            PlayerOwnershipValidationStatus.CharacterNotFound =>
                "character_not_found",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unsupported ownership validation status.")
        };
}
