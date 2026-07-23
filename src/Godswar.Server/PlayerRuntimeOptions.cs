using System.Text.Json.Serialization;
using Godswar.Server.Game;

namespace Godswar.Server;

internal sealed class PlayerRuntimeOptions
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlayerRuntimeMode Runtime { get; set; } =
        PlayerRuntimeMode.Ecs;

    public void Validate()
    {
        if (!Enum.IsDefined(Runtime))
        {
            throw new InvalidDataException(
                $"Unsupported player runtime mode '{Runtime}'.");
        }
    }
}
