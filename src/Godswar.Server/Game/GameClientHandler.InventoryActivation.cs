using Godswar.Server.Application.Commands;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleBreakItemAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine(
                "[equip-re] BreakItem ignored: no active character");
            return;
        }

        if (!TryReadBreakItemEquip(
                packet.Payload,
                out var sourceSlot))
        {
            Console.WriteLine(
                "[equip-re] BreakItem ignored: payload does not " +
                "contain a valid bag page/index");
            return;
        }

        if (packet.ClientOperationId is { } operationId)
        {
            await HandleDurableBagItemActivationAsync(
                operationId,
                sourceSlot,
                cancellationToken);
            return;
        }

        var itemId = KitBagSlots.GetItemId(
            _character.KitBag,
            sourceSlot);
        var isPetEgg =
            RequirePetContent().TryGetSpeciesByEggItemId(itemId, out _);
        var isEquipment =
            EquipmentSlots.TryGetAuthoritativeSlot(
                RequireItemContent().Templates,
                itemId,
                out var authoritativeEquipmentSlot);

        if (_session.IsSecure || _petDurableCommands is not null)
        {
            // Opcode 10051 is shared by right-click equipment activation and
            // pet-egg hatching. Until the client supplies a truthful action
            // discriminator, the secure request cannot be routed to either
            // durable aggregate without guessing. Never downgrade secure or
            // otherwise identified traffic into either compatibility
            // mutation. Unidentified raw pet eggs fail closed below; only
            // the separate equipment compatibility path remains available.
            Console.WriteLine(
                "[equip-re] BreakItem ignored: operation identity is " +
                "ambiguous between equipment activation and pet hatch");
            if (isEquipment)
            {
                await SendEquipRejectionRefreshAsync(
                    requestedEquipmentSlot: -1,
                    resolvedEquipmentSlot:
                        authoritativeEquipmentSlot,
                    bagSlot: sourceSlot,
                    cancellationToken);
            }
            else
            {
                await SendKitBagRefreshAsync(cancellationToken);
            }

            return;
        }

        if (isPetEgg)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.BagItemActivation);
            Console.WriteLine(
                "[equip-re] BreakItem ignored: pet hatch requires " +
                "durable operation identity");
            await SendKitBagRefreshAsync(cancellationToken);
            return;
        }

        if (!isEquipment)
        {
            Console.WriteLine(
                $"[equip-re] BreakItem ignored: sourceSlot={sourceSlot} " +
                $"item={itemId} is not genuine equipment");
            return;
        }

        await HandleEquipItemAsync(
            sourceSlot,
            requestedEquipmentSlot: -1,
            itemIdHint: 0,
            cancellationToken);
    }
}
