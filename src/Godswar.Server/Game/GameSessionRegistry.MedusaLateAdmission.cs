using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal readonly record struct MedusaLateAdmissionResult(
    bool Accepted,
    bool Added);

internal sealed partial class GameSessionRegistry
{
    internal MedusaLateAdmissionResult TryAdmitMedusaCharacter(
        WorldInstanceId worldInstanceId,
        int characterId)
    {
        if (!worldInstanceId.IsValid || characterId <= 0 ||
            !WorldInstances.TryFind(worldInstanceId, out var runtime))
        {
            return default;
        }

        var result = InvokeWorldOwner(
            runtime,
            map =>
            {
                var accepted = map.TryAdmitMedusaCharacter(
                    characterId,
                    out var added);
                return new MedusaLateAdmissionResult(accepted, added);
            });
        return result;
    }

    internal bool RollBackLateMedusaCharacterAdmission(
        WorldInstanceId worldInstanceId,
        int characterId)
    {
        if (!worldInstanceId.IsValid || characterId <= 0 ||
            !WorldInstances.TryFind(worldInstanceId, out var runtime))
        {
            return false;
        }

        return InvokeWorldOwner(
            runtime,
            map => map.RollBackLateMedusaCharacterAdmission(characterId));
    }

    internal bool IsMedusaCharacterAdmitted(
        WorldInstanceId worldInstanceId,
        int characterId) =>
        worldInstanceId.IsValid &&
        characterId > 0 &&
        WorldInstances.TryFind(worldInstanceId, out var runtime) &&
        InvokeWorldOwner(
            runtime,
            map => map.CheckMedusaCharacterAdmission(characterId))
        .Outcome == Godswar.Server.Game.WorldInstances
            .MedusaInstanceCharacterAdmissionOutcome.CharacterAdmitted;
}
