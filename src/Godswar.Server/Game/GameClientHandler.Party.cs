using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandlePartyPacketAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Opcode == Opcodes.RepetitionResponse)
        {
            await HandleMedusaInvitationResponseAsync(
                packet,
                cancellationToken);
            return true;
        }
        if (packet.Opcode == Opcodes.RepetitionLeave)
        {
            await HandleMedusaLeaderEndAsync(packet, cancellationToken);
            return true;
        }
        if (packet.Opcode == Opcodes.RepetitionPanelAction)
        {
            await HandleMedusaLeaderPanelActionAsync(
                packet,
                cancellationToken);
            return true;
        }
        if (!PartyProtocol.IsClientAction(packet.Opcode))
        {
            return false;
        }
        if (!PartyProtocol.TryReadAction(packet, out var action))
        {
            Console.Error.WriteLine(
                $"[party] rejected malformed opcode={packet.Opcode}");
            return true;
        }

        var canceledInvitation =
            _registry.CancelMedusaInvitationForSession(_session);
        if (canceledInvitation is not null)
        {
            await PublishMedusaInvitationResetAsync(
                canceledInvitation,
                cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var result = packet.Opcode switch
        {
            Opcodes.PartyInvite => _registry.InvitePartyMember(
                _session,
                action.FirstName,
                action.SecondName,
                now),
            Opcodes.PartyAccept => _registry.AcceptPartyInvite(
                _session,
                action.FirstName,
                action.SecondName,
                now),
            Opcodes.PartyRemove => _registry.RemovePartyMember(
                _session,
                action.FirstName,
                action.SecondName),
            Opcodes.PartyChangeLeader => _registry.ChangePartyLeader(
                _session,
                action.FirstName,
                action.SecondName),
            Opcodes.PartyDissolve => _registry.DissolveParty(
                _session,
                action.FirstName),
            Opcodes.PartyLeave => _registry.LeaveParty(
                _session,
                action.FirstName),
            Opcodes.PartyReject => _registry.RejectPartyInvite(
                _session,
                action.FirstName,
                action.SecondName,
                now),
            _ => throw new InvalidOperationException(
                "Unsupported party action.")
        };

        if (result.Status != PartyOperationStatus.Applied)
        {
            Console.WriteLine(
                $"[party] rejected opcode={packet.Opcode} " +
                $"status={result.Status}");
            return true;
        }

        await PublishPartyDeliveriesAsync(
            result.Deliveries,
            cancellationToken);
        if (packet.Opcode == Opcodes.PartyAccept)
        {
            await TryOfferLateMedusaEntryAsync(
                action.FirstName,
                now,
                cancellationToken);
        }
        return true;
    }

    private async Task LeavePartyForSessionExitAsync()
    {
        try
        {
            var canceledInvitation =
                _registry.CancelMedusaInvitationForSession(_session);
            if (canceledInvitation is not null)
            {
                await PublishMedusaInvitationResetAsync(
                    canceledInvitation,
                    CancellationToken.None);
            }
            var result = _registry.RemovePartySession(_session);
            await PublishPartyDeliveriesAsync(
                result.Deliveries,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[party] failed session-exit cleanup: {ex.Message}");
        }
    }

    private static async Task PublishPartyDeliveriesAsync(
        IReadOnlyList<PartyDelivery> deliveries,
        CancellationToken cancellationToken)
    {
        foreach (var delivery in deliveries)
        {
            try
            {
                await delivery.Recipient.SendAsync(
                    delivery.Packet,
                    cancellationToken,
                    delivery.Label);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine(
                    $"[party] failed {delivery.Label}: {ex.Message}");
            }
        }
    }
}
