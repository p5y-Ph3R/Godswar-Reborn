using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleMonsterLootPickupAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null ||
            packet.Payload.Length != 12)
        {
            return;
        }

        var playerObjectId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload);
        var monsterObjectId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload.Slice(4));
        var pickupIndex = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8));
        if (playerObjectId != LocalPlayerObjectId &&
            playerObjectId != CurrentPlayerObjectId ||
            !_registry.TryReserveMonsterLootPickup(
                _session,
                monsterObjectId,
                pickupIndex,
                DateTimeOffset.UtcNow,
                out var reservation))
        {
            return;
        }

        var completed = false;
        try
        {
            var result = await _store.PickupMonsterLootAsync(
                _account.Id,
                _character.Id,
                reservation.DeathEventId,
                reservation.RuleLootIndex,
                reservation.ItemId,
                reservation.Quantity,
                cancellationToken);
            if (!result.Succeeded || result.Character is null ||
                !RevalidateCurrentWorldEffectOwnership(
                    "monster_loot_pickup"))
            {
                if (result.Status ==
                    MonsterLootPickupStatus.InsufficientCapacity)
                {
                    await SendKitBagRefreshAsync(cancellationToken);
                }
                return;
            }

            InstallUpdatedCharacter(result.Character);
            _registry.UpdateCharacter(
                _session,
                _character,
                advanceWorldRevision: false);
            await _session.SendAsync(
                PacketBuilder.MonsterLootPickup(
                    LocalPlayerObjectId,
                    monsterObjectId,
                    pickupIndex),
                cancellationToken,
                "MonsterLootPickup");
            await SendKitBagRefreshAsync(cancellationToken);
            _registry.CompleteMonsterLootPickup(reservation);
            completed = true;
            Console.WriteLine(
                $"[loot] picked character={_character.Name} monster={monsterObjectId} item={reservation.ItemId} quantity={reservation.Quantity}");
        }
        finally
        {
            if (!completed)
            {
                _registry.ReleaseMonsterLootPickup(reservation);
            }
        }
    }
}
