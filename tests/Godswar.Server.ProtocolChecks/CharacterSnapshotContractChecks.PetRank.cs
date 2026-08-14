using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterSnapshotContractChecks
{
    private static void CheckPetRankWireSafety()
    {
        var valid = CreateValidSnapshot();
        var pet = valid.Character!.Pets[0];
        var maximum = WithPetRank(
            valid,
            pet with { Rank = PetRankWirePolicy.MaximumRank });
        CharacterSnapshotContract.Validate(maximum);

        foreach (var invalidRank in new[] { 655.36m, 1.001m })
        {
            var failure = CaptureFailure(
                () => CharacterSnapshotContract.Validate(
                    WithPetRank(
                        valid,
                        pet with { Rank = invalidRank })));
            Check.Equal(
                (int)CharacterSnapshotFailureReason.InvalidData,
                (int)failure.Reason,
                $"snapshot rejects native-wire-unsafe pet rank {invalidRank}");
        }
    }

    private static CharacterAccountSnapshot WithPetRank(
        CharacterAccountSnapshot snapshot,
        CharacterPetSnapshot pet) =>
        snapshot with
        {
            Character = snapshot.Character! with
            {
                Pets = ImmutableArray.Create(pet)
            }
        };
}
