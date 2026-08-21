using Godswar.Server.Application.Characters;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal ElementalMovementAuthority ResolveElementalMovementAuthority(
        ClientSession session,
        GameCharacter character,
        PlayerOwnershipFence ownership,
        float baseMovementMultiplier,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(character);
        var encoded = AuthoredElementalCombatV1.EncodeMovementMultiplier(
            baseMovementMultiplier);
        encoded = ElementalResonanceExecutionPolicy.ApplyPassiveBonuses(
            character.ElementalEquipment,
            maximumHealth: 0,
            movementSpeed: encoded).MovementSpeed;
        var allowed = true;
        if (ownership.IsValid)
        {
            var fence = new ElementalCombatSessionFence(
                character.Id,
                character.CurrentMap,
                ownership);
            if (TryGetElementalStatusAdjustment(
                    session,
                    fence,
                    observedAt.ToUnixTimeMilliseconds(),
                    encoded,
                    physicalDefense: 0,
                    magicDefense: 0,
                    hitRating: 0,
                    healingReceived: 0,
                    out var status))
            {
                allowed = status.MovementAllowed;
                encoded = status.MovementSpeed;
            }
        }

        return new ElementalMovementAuthority(
            allowed,
            AuthoredElementalCombatV1.DecodeMovementMultiplier(encoded),
            encoded);
    }

    internal ClientStatusAggregate ProjectElementalMovementStatus(
        ClientSession session,
        GameCharacter character,
        PlayerOwnershipFence ownership,
        in ClientStatusAggregate aggregate,
        DateTimeOffset observedAt)
    {
        var authority = ResolveElementalMovementAuthority(
            session,
            character,
            ownership,
            aggregate.MovementSpeedMultiplier,
            observedAt);
        return authority.EncodedMovementMultiplier > 0
            ? aggregate with
            {
                MovementSpeedMultiplier = authority.MovementMultiplier
            }
            : aggregate;
    }
}
