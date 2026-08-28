using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private Task SendInsufficientManaRejectionAsync(
        int currentMana,
        CancellationToken cancellationToken,
        string label) => SendSkillRejectionAsync(
        NativeErrorCodes.InsufficientMana,
        currentMana,
        cancellationToken,
        label);

    private Task SendSkillCooldownRejectionAsync(
        CancellationToken cancellationToken,
        string label) => SendSkillRejectionAsync(
        NativeErrorCodes.SkillNotReady,
        currentMana: null,
        cancellationToken,
        label);

    private Task SendSkillCastRejectionInterruptAsync(
        CancellationToken cancellationToken,
        string label) => SendSkillRejectionAsync(
        errorCode: null,
        currentMana: null,
        cancellationToken,
        label);

    private async Task SendSkillRejectionAsync(
        int? errorCode,
        int? currentMana,
        CancellationToken cancellationToken,
        string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var interruptPublishedCast = HasPendingSkillCast;
        if (errorCode is { } nativeError)
        {
            await _session.SendAsync(
                PacketBuilder.LocalizedError(nativeError),
                cancellationToken,
                $"{label}Notice");
        }
        if (currentMana is { } mana)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(
                    LocalPlayerObjectId,
                    mana),
                cancellationToken,
                $"{label}Mana");
        }

        await _session.SendAsync(
            PacketBuilder.SkillCastInterrupt(LocalPlayerObjectId),
            cancellationToken,
            $"{label}InterruptSelf");
        if (!interruptPublishedCast || _character is null)
        {
            return;
        }

        await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.SkillCastInterrupt(CurrentPlayerObjectId),
            cancellationToken,
            _session,
            $"{label}InterruptWorld");
    }
}
