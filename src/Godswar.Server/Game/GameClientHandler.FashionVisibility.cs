using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static readonly TimeSpan FashionVisibilityTransitionWindow =
        TimeSpan.FromSeconds(1);
    private const int MaximumFashionVisibilityTransitionsPerWindow = 6;
    private DateTimeOffset _fashionVisibilityWindowStartedAt =
        DateTimeOffset.MinValue;
    private int _fashionVisibilityTransitionsInWindow;

    internal static bool TryReadFashionVisibilityRequest(
        ReadOnlySpan<byte> payload,
        out bool fashionHidden)
    {
        fashionHidden = false;
        if (payload.Length != 8)
        {
            return false;
        }

        // Native Origin.exe leaves the first DWORD uninitialized. Never use it
        // as identity or authority; only the final checkbox flag is meaningful
        // (0 = show Fashion, 1 = hide Fashion).
        var hiddenValue =
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (hiddenValue > 1)
        {
            return false;
        }

        fashionHidden = hiddenValue == 1;
        return true;
    }

    internal static bool HasEquippedFashion(
        GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return !EquipmentSlots.GetItem(
                character.Equipment,
                character.Profession,
                EquipmentSlots.Stylish)
            .IsEmpty;
    }

    internal static bool ResolveFashionHiddenAfterEquipmentChange(
        GameCharacter current,
        GameCharacter updated)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(updated);

        // Equipping a Fashion item always starts in Show mode. Unequipping also
        // clears the old hidden preference so a later Fashion equip cannot
        // inherit a stale unchecked state from an absent item.
        return HasEquippedFashion(current) == HasEquippedFashion(updated)
            ? current.FashionHidden
            : false;
    }

    private bool TryReserveFashionVisibilityTransition(
        DateTimeOffset now)
    {
        if (_fashionVisibilityWindowStartedAt ==
                DateTimeOffset.MinValue ||
            now - _fashionVisibilityWindowStartedAt >=
                FashionVisibilityTransitionWindow)
        {
            _fashionVisibilityWindowStartedAt = now;
            _fashionVisibilityTransitionsInWindow = 1;
            return true;
        }

        if (_fashionVisibilityTransitionsInWindow >=
            MaximumFashionVisibilityTransitionsPerWindow)
        {
            return false;
        }

        _fashionVisibilityTransitionsInWindow++;
        return true;
    }

    private async Task PublishFashionVisibilityAsync(
        bool broadcastObservers,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(
                _character,
                LocalPlayerObjectId,
                _itemContent?.FashionAppearances),
            cancellationToken,
            "SelfFashionVisibilityRefresh");
        await _session.SendAsync(
            PacketBuilder.EquipmentEffectVisibility(
                LocalPlayerObjectId,
                ResolveEquipmentEffectProjection(_character)),
            cancellationToken,
            "SelfFashionEffectVisibility");

        if (!broadcastObservers || !_worldPresenceAnnounced)
        {
            Console.WriteLine(
                $"[fashion] visibility character={_character.Name} " +
                $"hidden={_character.FashionHidden} observers=0");
            return;
        }

        var recipients =
            await _registry.BroadcastToCurrentWorldInstanceAsync(
                _session,
                PacketBuilder.EquipmentVisualRefresh(
                    _character,
                    WorldObjectIds.ForPlayer(_character.Id),
                    _itemContent?.FashionAppearances),
                cancellationToken,
                includeRoutingSession: false,
                label: "PlayerFashionVisibilityRefresh");
        if (recipients > 0)
        {
            await _registry.BroadcastToCurrentWorldInstanceAsync(
                _session,
                PacketBuilder.EquipmentEffectVisibility(
                    WorldObjectIds.ForPlayer(_character.Id),
                    ResolveEquipmentEffectProjection(_character)),
                cancellationToken,
                includeRoutingSession: false,
                label: "PlayerFashionEffectVisibility");
        }
        Console.WriteLine(
            $"[fashion] visibility character={_character.Name} " +
            $"hidden={_character.FashionHidden} observers={recipients}");
    }

    private Task PublishFashionVisibilityIfNeededAsync(
        bool visibilityChanged,
        bool broadcastObservers,
        bool rateLimited,
        bool forceSelfProjection,
        CancellationToken cancellationToken)
    {
        if (visibilityChanged || forceSelfProjection)
        {
            return PublishFashionVisibilityAsync(
                broadcastObservers,
                cancellationToken);
        }

        // The native checkbox already changed locally. On a rejected transition,
        // reassert the last accepted model without mutating or broadcasting it.
        return rateLimited
            ? PublishFashionVisibilityAsync(
                broadcastObservers: false,
                cancellationToken)
            : Task.CompletedTask;
    }
}
