using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterSnapshotContractChecks
{
    private static void CheckPetScaledAddedFailures()
    {
        var valid = CreateValidSnapshot();
        var legacy = valid.Character!.Pets[0];
        var stats = Enumerable.Range(1, 6)
            .Select(index =>
            {
                var growth = index / 10m;
                var acceleration = index / 100m;
                var birth = 40m + index;
                return new CharacterPetStatValueSnapshot(
                    checked((short)index),
                    InitialSavvy: birth + index,
                    AddedSavvy: PetSavvyPersistenceContract.ResolveAdded(
                        legacy.Level,
                        growth,
                        acceleration),
                    BaseGrowthRate: growth,
                    GrowthAcceleration: acceleration,
                    Revision: index,
                    BirthInitialSavvy: birth,
                    RarityAddedSavvy: birth);
            })
            .ToImmutableArray();
        var v3Pet = legacy with
        {
            StatValues = stats,
            InitialSavvySourceVersion =
                PetSavvyPersistenceContract.SourceVersion
        };
        CharacterSnapshotContract.Validate(ReplacePet(valid, v3Pet));

        var staleStats = stats.SetItem(
            0,
            stats[0] with { AddedSavvy = stats[0].AddedSavvy + 0.01m });
        AssertInvalidPetSavvy(
            WithPet(v3Pet with { StatValues = staleStats }),
            "snapshot rejects stale materialized Added");
        AssertInvalidPetSavvy(
            WithPet(v3Pet with
            {
                InitialSavvySourceVersion = "savvy-plus-growth-v2"
            }),
            "snapshot rejects obsolete Savvy provenance");
        AssertInvalidPetSavvy(
            WithPet(v3Pet with { StatValues = stats.RemoveAt(5) }),
            "snapshot rejects incomplete scaled-Added vectors");
        AssertInvalidPetSavvy(
            WithPet(legacy with
            {
                StatValues = legacy.StatValues.SetItem(
                    0,
                    legacy.StatValues[0] with
                    {
                        BirthInitialSavvy = 1m,
                        RarityAddedSavvy = 1m
                    })
            }),
            "legacy snapshot rejects partial Savvy provenance");

        CharacterAccountSnapshot WithPet(CharacterPetSnapshot pet) =>
            ReplacePet(valid, pet);
    }

    private static CharacterAccountSnapshot ReplacePet(
        CharacterAccountSnapshot snapshot,
        CharacterPetSnapshot pet) =>
        snapshot with
        {
            Character = snapshot.Character! with
            {
                Pets = ImmutableArray.Create(pet)
            }
        };

    private static void AssertInvalidPetSavvy(
        CharacterAccountSnapshot snapshot,
        string message)
    {
        var failure = CaptureFailure(
            () => CharacterSnapshotContract.Validate(snapshot));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.InvalidData,
            (int)failure.Reason,
            message);
    }
}
