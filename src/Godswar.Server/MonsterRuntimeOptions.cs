using System.Text.Json.Serialization;
using Godswar.Server.Game;

namespace Godswar.Server;

internal sealed class MonsterRuntimeOptions
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MonsterRuntimeMode Runtime { get; set; } =
        MonsterRuntimeMode.Ecs;

    public void Validate()
    {
        if (!Enum.IsDefined(Runtime))
        {
            throw new InvalidDataException(
                $"Unsupported monster runtime mode '{Runtime}'.");
        }
    }
}
