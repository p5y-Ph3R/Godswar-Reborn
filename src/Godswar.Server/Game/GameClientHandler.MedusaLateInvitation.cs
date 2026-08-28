namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task TryOfferLateMedusaEntryAsync(
        string inviterName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var status = _registry.TryBeginLateMedusaInvitation(
            _session,
            inviterName,
            now,
            out var invitation);
        if (status != MedusaPartyEntryStatus.Ready)
        {
            return;
        }

        var used = await TryFindUsedMedusaDailyEntryCharactersAsync(
            invitation.Party.RealmId,
            _realmCalendar.GetDay(now),
            [invitation.Invitee.CharacterId],
            cancellationToken);
        if (used is null || used.Contains(invitation.Invitee.CharacterId))
        {
            _registry.CancelMedusaInvitation(invitation.InvitationId);
            return;
        }

        if (!await PublishMedusaInvitationNoticeAsync(
                invitation,
                cancellationToken))
        {
            _registry.CancelMedusaInvitation(invitation.InvitationId);
            return;
        }

        _ = MonitorMedusaInvitationTimeoutAsync(
            invitation,
            CancellationToken.None);
    }
}
