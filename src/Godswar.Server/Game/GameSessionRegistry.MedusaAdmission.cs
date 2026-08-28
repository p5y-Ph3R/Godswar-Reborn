using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private bool MayEnterWorldInstance(
        WorldInstanceRuntime runtime,
        int characterId)
    {
        var admission = InvokeWorldOwner(
            runtime,
            map => map.CheckMedusaCharacterAdmission(characterId));
        return admission.MayEnter;
    }

    private void RequireWorldInstanceAdmission(
        GameSessionContext context)
    {
        var runtime = GetRequiredWorldInstance(context);
        if (!MayEnterWorldInstance(runtime, context.CharacterId))
        {
            throw new InvalidOperationException(
                $"Character {context.CharacterId} is not admitted to " +
                $"world instance {context.WorldInstanceId}.");
        }
    }
}
