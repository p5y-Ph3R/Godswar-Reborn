using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<PetSkillMenuResolution>
        ResolvePetSkillUnlearnMenuAsync(
            CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return PetSkillMenuResolution.Invalid;
        }

        IReadOnlyList<CharacterPetSnapshot> pets;
        try
        {
            pets = await _ownedPetSnapshots.ReadOwnedPetsAsync(
                _account.Id,
                _character.Id,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[pet-manager] skill menu suppressed " +
                $"reason=projection_unavailable error={error.GetType().Name}");
            return PetSkillMenuResolution.Invalid;
        }

        var activePets = pets
            .Where(static pet => pet.IsCarried && pet.IsSummoned)
            .Take(2)
            .ToArray();
        if (activePets.Length == 0)
        {
            return PetSkillMenuResolution.NoActivePet;
        }
        if (activePets.Length != 1)
        {
            return PetSkillMenuResolution.Invalid;
        }

        var pet = activePets[0];
        if (pet.AccountId != _account.Id ||
            pet.OwnerCharacterId != _character.Id ||
            !string.Equals(
                pet.ActivityState,
                "owned",
                StringComparison.Ordinal))
        {
            return PetSkillMenuResolution.Invalid;
        }
        var activeSkills = pet.Skills
            .Where(static skill => skill.IsActive)
            .ToArray();
        if (activeSkills.Length > PetManagerProtocol.MaximumSkillSlots)
        {
            return PetSkillMenuResolution.Invalid;
        }

        var slotState = new PetSkillSlotState(
            checked((short)activeSkills.Length),
            pet.OpenedSkillSlots,
            pet.AvailableSkillSlots);
        if (!PetSkillSlotPolicy.IsValid(slotState))
        {
            return PetSkillMenuResolution.Invalid;
        }

        var slots = activeSkills
            .Select(static skill => checked((int)skill.SlotIndex))
            .Order()
            .ToArray();
        if (slots.Any(slot => slot < 0 || slot >= pet.OpenedSkillSlots) ||
            slots.Distinct().Count() != slots.Length)
        {
            return PetSkillMenuResolution.Invalid;
        }
        if (slots.Length == 0)
        {
            return PetSkillMenuResolution.NoLearnedSkill;
        }
        if (!PetManagerProtocol.TryBuildSkillUnlearnPage(
                slots,
                out var responseSubIds))
        {
            return PetSkillMenuResolution.Invalid;
        }

        return new(PetSkillMenuStatus.Available, responseSubIds);
    }

    private readonly record struct PetSkillMenuResolution(
        PetSkillMenuStatus Status,
        int[] ResponseSubIds)
    {
        public static PetSkillMenuResolution Invalid { get; } =
            new(PetSkillMenuStatus.Invalid, []);

        public static PetSkillMenuResolution NoActivePet { get; } =
            new(PetSkillMenuStatus.NoActivePet, []);

        public static PetSkillMenuResolution NoLearnedSkill { get; } =
            new(PetSkillMenuStatus.NoLearnedSkill, []);
    }

    private enum PetSkillMenuStatus
    {
        Invalid,
        Available,
        NoActivePet,
        NoLearnedSkill
    }
}
