using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckLateCharacterAdmission()
    {
        var map = CreateMap(
            MedusaEncounterDifficulty.Enhanced,
            playerCapacity: 2);
        var bound = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]);
        Check.True(
            bound.IsBound &&
            map.TryAdmitMedusaCharacter(102, out var added) &&
            added &&
            map.TryAdmitMedusaCharacter(102, out var duplicateAdded) &&
            !duplicateAdded &&
            !map.TryAdmitMedusaCharacter(103, out _) &&
            map.TryGetMedusaOwnershipSnapshot(out var admitted) &&
            admitted.Run.AdmittedCharacterIds.SequenceEqual(
                new[] { 101, 102 }),
            "an active Medusa owner admits one late party member exactly once and enforces capacity");

        Check.True(
            !map.RollBackLateMedusaCharacterAdmission(101) &&
            map.RollBackLateMedusaCharacterAdmission(102) &&
            map.TryAdmitMedusaCharacter(103, out var replacementAdded) &&
            replacementAdded &&
            map.TryGetMedusaOwnershipSnapshot(out var replaced) &&
            replaced.Run.AdmittedCharacterIds.SequenceEqual(
                new[] { 101, 103 }),
            "a failed late transfer can release its roster slot without removing the original roster");
    }
}
