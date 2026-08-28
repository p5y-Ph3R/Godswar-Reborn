using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryGetActiveMedusaCaptureDifficulty(
        ClientSession session,
        out MedusaEncounterDifficulty difficulty)
    {
        ArgumentNullException.ThrowIfNull(session);
        difficulty = default;
        if (!_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != 200 ||
            !WorldInstances.TryFind(
                context.WorldInstanceId,
                out var runtime))
        {
            return false;
        }

        var ownership = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                out var snapshot)
                    ? snapshot
                    : null);
        if (ownership is null ||
            ownership.Run.State != MedusaRunState.Active ||
            !ownership.Run.AdmittedCharacterIds.Contains(
                context.CharacterId) ||
            ownership.Difficulty is not (
                MedusaEncounterDifficulty.Enhanced or
                MedusaEncounterDifficulty.Mythic))
        {
            return false;
        }

        difficulty = ownership.Difficulty;
        return true;
    }
}
