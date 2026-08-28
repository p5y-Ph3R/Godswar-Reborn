using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleMedusaInvitationResponseAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Opcode == Opcodes.RepetitionResponse &&
            packet.Length == 16 &&
            packet.Buffer.Length == 16 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.Payload.Slice(sizeof(int))) == 0 &&
            _registry.IsSessionInMedusaInstance(_session))
        {
            // Stock instance-panel acknowledgements share opcode 10217 with
            // member invitations but carry a zero invitation identity.
            return;
        }

        if (!TryReadMedusaInvitationResponse(
                packet,
                out var clientSceneId,
                out var invitationId,
                out var accepted))
        {
            Console.Error.WriteLine(
                "[instance-caller] rejected malformed repetition " +
                $"response length={packet.Length}");
            return;
        }

        var result = _registry.RecordMedusaInvitationResponse(
            _session,
            clientSceneId,
            invitationId,
            accepted,
            DateTimeOffset.UtcNow);
        if (result.Invitation is not { } invitation)
        {
            Console.WriteLine(
                "[instance-caller] ignored stale repetition response " +
                $"invitation={invitationId}");
            return;
        }

        await PublishMedusaInvitationResetAsync(
            invitation,
            CancellationToken.None);
        if (result.Status == MedusaInvitationResponseStatus.Ready)
        {
            await AdmitInvitedMedusaMemberAsync(
                invitation,
                cancellationToken);
            return;
        }

        Console.WriteLine(
            "[instance-caller] Medusa member confirmation ended " +
            $"character={invitation.Invitee.CharacterName} " +
            $"status={result.Status} invitation={invitationId}");
    }

    private static bool TryReadMedusaInvitationResponse(
        GamePacket packet,
        out int clientSceneId,
        out int invitationId,
        out bool accepted)
    {
        clientSceneId = 0;
        invitationId = 0;
        accepted = false;
        if (packet.Opcode != Opcodes.RepetitionResponse ||
            packet.Length != 16 ||
            packet.Buffer.Length != 16)
        {
            return false;
        }

        clientSceneId = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload);
        invitationId = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(sizeof(int)));
        var response = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(sizeof(int) * 2));
        if (clientSceneId <= 0 ||
            invitationId <= 0 ||
            response is not (0 or 1))
        {
            return false;
        }

        accepted = response == 1;
        return true;
    }

    private static async Task<bool> PublishMedusaInvitationNoticeAsync(
        MedusaInstanceInvitation invitation,
        CancellationToken cancellationToken)
    {
        try
        {
            await invitation.Invitee.Session.SendAsync(
                PacketBuilder.RepetitionInvitation(
                    invitation.ClientSceneId,
                    invitation.InvitationId,
                    invitation.Party.Members[0].CharacterName),
                cancellationToken,
                "MedusaRepetitionInvitation");
            return true;
        }
        catch (Exception error) when (
            error is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "[instance-caller] failed Medusa confirmation " +
                $"notice character={invitation.Invitee.CharacterName}: " +
                error.Message);
            return false;
        }
    }

    private static async Task PublishMedusaInvitationResetAsync(
        MedusaInstanceInvitation invitation,
        CancellationToken cancellationToken)
    {
        try
        {
            await invitation.Invitee.Session.SendAsync(
                PacketBuilder.RepetitionReset(),
                cancellationToken,
                "MedusaRepetitionReset");
        }
        catch (Exception error) when (
            error is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "[instance-caller] failed Medusa confirmation reset " +
                $"character={invitation.Invitee.CharacterName}: " +
                error.Message);
        }
    }

    private async Task MonitorMedusaInvitationTimeoutAsync(
        MedusaInstanceInvitation invitation,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = invitation.ExpiresAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            var expired = _registry.ExpireMedusaInvitation(
                invitation.InvitationId,
                DateTimeOffset.UtcNow);
            if (expired is not null)
            {
                await PublishMedusaInvitationResetAsync(
                    expired,
                    CancellationToken.None);
                Console.WriteLine(
                    "[instance-caller] Medusa member confirmation timed " +
                    $"out character={expired.Invitee.CharacterName} " +
                    $"invitation={expired.InvitationId}");
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-caller] Medusa confirmation timeout failed: " +
                error.Message);
        }
    }

    private async Task AdmitInvitedMedusaMemberAsync(
        MedusaInstanceInvitation invitation,
        CancellationToken cancellationToken)
    {
        var member = invitation.Invitee;
        if (!ReferenceEquals(member.Session, _session) ||
            !MedusaIslandEncounterPolicy.TryGetDifficulty(
                invitation.Difficulty,
                out var encounter) ||
            !encounter.ContentMapId.TryGetLegacyValue(out var targetMapId) ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                encounter.ContentMapId.Value,
                out var clientSceneId) ||
            clientSceneId != invitation.ClientSceneId ||
            !MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "first-entry",
                out var entrance))
        {
            Console.Error.WriteLine(
                "[instance-caller] rejected invalid Medusa member " +
                $"invitation character={member.CharacterName}");
            return;
        }

        var reservationId = Guid.NewGuid();
        var claimedAt = DateTimeOffset.UtcNow;
        if (await TryClaimMedusaDailyEntryAsync(
                reservationId,
                invitation.Party.RealmId,
                _realmCalendar.GetDay(claimedAt),
                invitation.Difficulty,
                [member.CharacterId],
                claimedAt,
                cancellationToken) is null)
        {
            Console.WriteLine(
                "[instance-caller] Medusa member no longer eligible " +
                $"character={member.CharacterName}");
            return;
        }

        MedusaLateAdmissionResult admission;
        try
        {
            admission = _registry.TryAdmitMedusaCharacter(
                invitation.TargetWorldInstanceId,
                member.CharacterId);
        }
        catch (Exception error)
        {
            await ReleaseMedusaDailyEntryAsync(reservationId);
            Console.Error.WriteLine(
                "[instance-caller] Medusa member admission fault " +
                $"character={member.CharacterName}: {error.Message}");
            return;
        }
        if (!admission.Accepted)
        {
            await ReleaseMedusaDailyEntryAsync(reservationId);
            Console.Error.WriteLine(
                "[instance-caller] Medusa member admission rejected " +
                $"character={member.CharacterName}");
            return;
        }

        var command = new MedusaInstanceTransitionCommand(
            member.CharacterId,
            member.SourceWorldInstanceId,
            member.SourceMapId,
            member.Ownership,
            invitation.TargetWorldInstanceId,
            checked((byte)targetMapId),
            entrance.X,
            entrance.Z);
        bool moved;
        try
        {
            moved = await TryBeginMedusaInstanceTransitionAsync(
                command,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            RollBackLateMedusaAdmission(invitation, admission);
            await ReleaseMedusaDailyEntryAsync(reservationId);
            throw;
        }
        catch (Exception error)
        {
            RollBackLateMedusaAdmission(invitation, admission);
            await ReleaseMedusaDailyEntryAsync(reservationId);
            Console.Error.WriteLine(
                "[instance-caller] Medusa member transfer fault " +
                $"character={member.CharacterName}: {error.Message}");
            return;
        }
        if (!moved)
        {
            RollBackLateMedusaAdmission(invitation, admission);
            await ReleaseMedusaDailyEntryAsync(reservationId);
            Console.Error.WriteLine(
                "[instance-caller] Medusa member transfer rejected " +
                $"character={member.CharacterName}");
            return;
        }

        Console.WriteLine(
            "[instance-caller] Medusa member admitted " +
            $"character={member.CharacterName} " +
            $"instance={invitation.TargetWorldInstanceId}");
    }

    private void RollBackLateMedusaAdmission(
        MedusaInstanceInvitation invitation,
        MedusaLateAdmissionResult admission)
    {
        if (admission.Added)
        {
            _registry.RollBackLateMedusaCharacterAdmission(
                invitation.TargetWorldInstanceId,
                invitation.Invitee.CharacterId);
        }
    }
}
