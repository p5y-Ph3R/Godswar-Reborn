namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool RejectBlockedNonWalkMovement() =>
        !IsElementalMovementAllowed(DateTimeOffset.UtcNow);
}
