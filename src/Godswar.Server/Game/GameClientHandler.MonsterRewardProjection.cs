using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private void ApplyMonsterRewardProjection(
        MonsterRewardSettlement settlement)
    {
        if (_character is null)
        {
            return;
        }

        var progression = settlement.Progression;
        var authoritative = settlement.Projection;
        _character.Level =
            authoritative?.Level ?? progression.CurrentLevel;
        _character.Experience =
            authoritative?.Experience ?? progression.CurrentExperience;
        _character.TalentExperience =
            authoritative?.TalentExperience ??
            progression.CurrentTalentExperience;
        _character.TalentPoints =
            authoritative?.TalentPoints ??
            progression.CurrentTalentPoints;
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        ApplyPetMonsterExperienceProjection(settlement.PetExperience);
    }

    private void ApplyPetMonsterExperienceProjection(
        PetMonsterExperienceResult? result)
    {
        if (result is not { HasPetProjection: true } projection ||
            _characterLoadSnapshot is not { } snapshot)
        {
            return;
        }

        var matched = false;
        var pets = snapshot.Pets.Select(pet =>
        {
            if (pet.PetId != projection.PetId!.Value)
            {
                return pet;
            }
            matched = true;
            return pet with
            {
                Experience = projection.TotalExperience!.Value,
                Revision = projection.PetRevision!.Value,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }).ToArray();
        if (matched)
        {
            _characterLoadSnapshot = snapshot with { Pets = pets };
        }
    }
}
