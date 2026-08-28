using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        public bool IsCharacterAdmitted(int characterId) =>
            _run.IsCharacterAdmitted(characterId);
    }

    internal MedusaInstanceCharacterAdmissionResult
        CheckMedusaCharacterAdmission(int characterId)
    {
        lock (_medusaOwnershipGate)
        {
            return CheckMedusaCharacterAdmissionCore(characterId);
        }
    }

    private void ExecuteWithMedusaCharacterAdmission(
        int characterId,
        Action action)
    {
        lock (_medusaOwnershipGate)
        {
            RequireMedusaCharacterAdmissionCore(characterId);
            action();
        }
    }

    private TResult ExecuteWithMedusaCharacterAdmission<TResult>(
        int characterId,
        Func<TResult> action)
    {
        lock (_medusaOwnershipGate)
        {
            RequireMedusaCharacterAdmissionCore(characterId);
            return action();
        }
    }

    private MedusaInstanceCharacterAdmissionResult
        CheckMedusaCharacterAdmissionCore(int characterId)
    {
        if (_medusaInstanceOwner is null)
        {
            return new(
                MedusaInstanceCharacterAdmissionOutcome
                    .InstanceUnbound);
        }

        return new(
            _medusaInstanceOwner.IsCharacterAdmitted(characterId)
                ? MedusaInstanceCharacterAdmissionOutcome
                    .CharacterAdmitted
                : MedusaInstanceCharacterAdmissionOutcome
                    .CharacterNotAdmitted);
    }

    private void RequireMedusaCharacterAdmissionCore(int characterId)
    {
        if (!CheckMedusaCharacterAdmissionCore(characterId).MayEnter)
        {
            throw new InvalidOperationException(
                $"Character {characterId} is not admitted to Medusa " +
                $"instance {WorldInstanceId}.");
        }
    }
}
