using Godswar.Server.Application.Commands;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetDurablePersistenceCodec
{
    public static string FamilyCode(CommandFamily family) =>
        family switch
        {
            CommandFamily.BagItemActivation => "bag_item_activation",
            CommandFamily.PetLevelUpgrade => "pet_level_upgrade",
            CommandFamily.PetPresenceTransition =>
                "pet_presence_transition",
            CommandFamily.PetSkillUnlearn => "pet_skill_unlearn",
            CommandFamily.PetGrowthReset => "pet_growth_reset",
            CommandFamily.PetBasicSavvyReset =>
                "pet_basic_savvy_reset",
            CommandFamily.PetOwnerMergeToggle =>
                "pet_owner_merge_toggle",
            CommandFamily.PetToPetMerge => "pet_to_pet_merge",
            CommandFamily.PetRebirth => "pet_rebirth",
            CommandFamily.PetAppearanceChange => "pet_appearance_change",
            CommandFamily.PetBind => "pet_bind",
            CommandFamily.PetSoulContract => "pet_soul_contract",
            CommandFamily.PetManagerUtility => "pet_manager_utility",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string EventType(CommandFamily family) =>
        family switch
        {
            CommandFamily.BagItemActivation => "pet.bag_item_activated",
            CommandFamily.PetLevelUpgrade => "pet.level_upgraded",
            CommandFamily.PetPresenceTransition => "pet.presence_changed",
            CommandFamily.PetSkillUnlearn => "pet.skill_unlearned",
            CommandFamily.PetGrowthReset => "pet.growth_reset",
            CommandFamily.PetBasicSavvyReset => "pet.basic_savvy_reset",
            CommandFamily.PetOwnerMergeToggle => "pet.owner_merge_toggled",
            CommandFamily.PetToPetMerge => "pet.merged",
            CommandFamily.PetRebirth => "pet.reborn",
            CommandFamily.PetAppearanceChange => "pet.appearance_changed",
            CommandFamily.PetBind => "pet.bound",
            CommandFamily.PetSoulContract => "pet.soul_contract_signed",
            CommandFamily.PetManagerUtility => "pet.manager_utility",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
}
