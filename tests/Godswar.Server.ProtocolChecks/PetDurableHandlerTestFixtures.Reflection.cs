using System.Reflection;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal sealed partial class PetDurableHandlerFixture
{
    internal static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }
}
