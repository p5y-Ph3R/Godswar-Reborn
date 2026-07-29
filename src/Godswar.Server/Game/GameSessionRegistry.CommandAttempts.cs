using Godswar.Server.Application.Commands;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly BoundedCommandAttemptRegistry _commandAttempts =
        new();

    internal BoundedCommandAttemptRegistry CommandAttempts =>
        _commandAttempts;
}
