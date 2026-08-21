using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static readonly TimeSpan FashionEffectTransitionWindow =
        TimeSpan.FromSeconds(1);
    private const int MaximumFashionEffectTransitionsPerWindow = 6;
    private DateTimeOffset _fashionEffectWindowStartedAt =
        DateTimeOffset.MinValue;
    private int _fashionEffectTransitionsInWindow;
    private bool _fashionEffectRequestObserved;

    internal static bool TryReadFashionEffectVisibilityRequest(
        ReadOnlySpan<byte> payload,
        out bool effectsVisible)
    {
        effectsVisible = false;
        if (payload.Length != 12)
        {
            return false;
        }

        // Native Origin.exe sends an object ID first and leaves the final DWORD
        // uninitialized. Neither value is authority. The authenticated session
        // identifies the actor; only the middle checkbox value is meaningful.
        var visibleValue =
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]);
        if (visibleValue > 1)
        {
            return false;
        }

        effectsVisible = visibleValue == 1;
        return true;
    }

    internal static bool ResolveEquipmentEffectProjection(
        GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);

        // The Fashion Effect checkbox only applies while Fashion is both
        // equipped and shown. Ordinary gear must recover its rank effects when
        // Fashion is hidden or removed, while the client-owned preference is
        // retained for the next shown Fashion projection.
        return !HasEquippedFashion(character) ||
            character.FashionHidden ||
            character.EquipmentEffectsVisible;
    }

    private bool TryReserveFashionEffectTransition(
        DateTimeOffset now)
    {
        if (_fashionEffectWindowStartedAt == DateTimeOffset.MinValue ||
            now - _fashionEffectWindowStartedAt >=
                FashionEffectTransitionWindow)
        {
            _fashionEffectWindowStartedAt = now;
            _fashionEffectTransitionsInWindow = 1;
            return true;
        }

        if (_fashionEffectTransitionsInWindow >=
            MaximumFashionEffectTransitionsPerWindow)
        {
            return false;
        }

        _fashionEffectTransitionsInWindow++;
        return true;
    }

    private async Task HandleFashionEffectVisibilityAsync(
        GamePacket request,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine(
                "[fashion] ignored Effect visibility: no active character");
            return;
        }

        if (!TryReadFashionEffectVisibilityRequest(
                request.Payload,
                out var effectsVisible))
        {
            Console.WriteLine(
                $"[fashion] ignored malformed Effect visibility packet " +
                $"character={_character.Name} bytes={request.Payload.Length}");
            return;
        }

        var changed =
            _character.EquipmentEffectsVisible != effectsVisible;
        var firstRequest = !_fashionEffectRequestObserved;
        _fashionEffectRequestObserved = true;
        var rateLimited =
            changed &&
            !TryReserveFashionEffectTransition(DateTimeOffset.UtcNow);
        if (rateLimited)
        {
            Console.WriteLine(
                $"[fashion] rate-limited Effect visibility transition " +
                $"character={_character.Name}");
        }

        if (changed && !rateLimited)
        {
            _character.EquipmentEffectsVisible = effectsVisible;
            if (_worldPresenceAnnounced)
            {
                _registry.UpdateCharacter(
                    _session,
                    _character,
                    advanceWorldRevision: true);
            }
        }

        // The native client changes the checkbox optimistically but changes the
        // renderers only from this authoritative S2C projection. Answer the
        // login initialization and each accepted transition; exact duplicates
        // are idempotent and deliberately produce no traffic.
        await _session.SendAsync(
            PacketBuilder.EquipmentEffectVisibility(
                LocalPlayerObjectId,
                ResolveEquipmentEffectProjection(_character)),
            cancellationToken,
            "SelfEquipmentEffectVisibility");

        var recipients = 0;
        if (changed && !rateLimited && _worldPresenceAnnounced)
        {
            recipients =
                await _registry.BroadcastToCurrentWorldInstanceAsync(
                    _session,
                    PacketBuilder.EquipmentEffectVisibility(
                        CurrentPlayerObjectId,
                        ResolveEquipmentEffectProjection(_character)),
                    cancellationToken,
                    includeRoutingSession: false,
                    label: "PlayerEquipmentEffectVisibility");
        }

        Console.WriteLine(
            $"[fashion] Effect visibility character={_character.Name} " +
            $"visible={_character.EquipmentEffectsVisible} " +
            $"changed={changed && !rateLimited} " +
            $"duplicate={!firstRequest && !changed} " +
            $"rateLimited={rateLimited} observers={recipients}");
    }
}
