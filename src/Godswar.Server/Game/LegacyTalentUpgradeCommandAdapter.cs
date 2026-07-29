using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;

namespace Godswar.Server.Game;

internal static class LegacyTalentUpgradeCommandAdapter
{
    public const int PayloadLength = 24;

    internal sealed record AdaptedCommand(
        CommandEnvelope<TalentUpgradeCommand> Envelope,
        int ClientTalentPoints);

    public static bool TryAdapt(
        ReadOnlySpan<byte> payload,
        CommandSubject subject,
        Guid connectionId,
        CommandTransportKind transport,
        DateTimeOffset receivedAt,
        out AdaptedCommand? adapted)
    {
        adapted = null;
        if (!GameClientHandler.TryReadTalentUpgrade(
                payload,
                out var talentId,
                out var expectedRank,
                out var clientTalentPoints) ||
            !TalentUpgradeCommandEnvelope.TryCreateCommand(
                talentId,
                expectedRank,
                out var command))
        {
            return false;
        }

        var envelope = TalentUpgradeCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                connectionId,
                transport),
            receivedAt,
            command);
        if (TalentUpgradeCommandEnvelope.Validate(envelope) !=
            CommandEnvelopeValidation.Valid)
        {
            return false;
        }

        adapted = new AdaptedCommand(
            envelope,
            clientTalentPoints);
        return true;
    }
}
