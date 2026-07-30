using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Rewards;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterDeathRewardCommandContractChecks
{
    public static Task RunAsync()
    {
        var runtimeId =
            Guid.Parse("9f7827ac-7fd7-4f8d-bc61-6259df349a21");
        Check.True(
            MonsterDeathRewardCommandEnvelope.TryCreateCommand(
                runtimeId,
                mapId: 6,
                monsterObjectId: 11_913,
                spawnGeneration: 4,
                deathHealthRevision: 19,
                awardedExperience: 1_250,
                awardedTalentExperience: 25,
                out var command),
            "server-derived monster reward command is accepted");
        var repeatedDeathId =
            MonsterDeathRewardCommandEnvelope.DeriveDeathEventId(
                runtimeId,
                6,
                11_913,
                4,
                19);
        Check.Equal(
            command.DeathEventId,
            repeatedDeathId,
            "the same lethal mutation has a stable death identity");
        Check.True(
            command.DeathEventId !=
                MonsterDeathRewardCommandEnvelope.DeriveDeathEventId(
                    Guid.NewGuid(),
                    6,
                    11_913,
                    4,
                    19),
            "a new map runtime cannot repeat an old death identity");

        var subject = new CommandSubject(7, 13);
        var first = MonsterDeathRewardCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command);
        var retry = MonsterDeathRewardCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow.AddSeconds(1),
            command);
        Check.Equal(
            first.OperationId,
            retry.OperationId,
            "reconnect preserves the server death operation ID");
        Check.Equal(
            first.RequestHash,
            retry.RequestHash,
            "reconnect preserves the frozen reward request hash");
        Check.True(
            MonsterDeathRewardCommandEnvelope.Validate(first) ==
                CommandEnvelopeValidation.Valid,
            "canonical monster reward envelope validates");
        Check.True(
            first.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId,
            "monster reward identity is explicitly server-owned");

        Check.True(
            MonsterDeathRewardCommandEnvelope.TryCreateCommand(
                runtimeId,
                6,
                11_913,
                4,
                19,
                awardedExperience: 1_251,
                awardedTalentExperience: 25,
                out var changedReward),
            "same death can construct conflict evidence");
        var conflict = first with
        {
            Command = changedReward
        };
        Check.True(
            MonsterDeathRewardCommandEnvelope.Validate(conflict) ==
                CommandEnvelopeValidation.RequestHashConflict,
            "changed frozen reward is a request-hash conflict");

        Check.True(
            !MonsterDeathRewardCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                6,
                11_913,
                4,
                19,
                1,
                1,
                out _) &&
            !MonsterDeathRewardCommandEnvelope.TryCreateCommand(
                runtimeId,
                6,
                11_913,
                4,
                0,
                1,
                1,
                out _) &&
            !MonsterDeathRewardCommandEnvelope.TryCreateCommand(
                runtimeId,
                6,
                11_913,
                4,
                19,
                -1,
                1,
                out _),
            "empty, nonlethal, and negative identities are rejected");
        return Task.CompletedTask;
    }
}
