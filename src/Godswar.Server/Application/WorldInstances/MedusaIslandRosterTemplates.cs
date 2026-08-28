using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

internal static class MedusaIslandRosterTemplateAliases
{
    public const string Stheno = "boss-stheno";
    public const string Euryale = "boss-euryale";
    public const string Chrysaor = "boss-chrysaor";
    public const string Medusa = "boss-medusa";
    public const string EliteArcher = "elite-gorgon-archer";
    public const string EliteCrazyAxemanA = "elite-crazy-axeman-a";
    public const string EliteShamanSix = "elite-gorgon-shaman-006";
    public const string EliteShamanEight = "elite-gorgon-shaman-008";
    public const string EliteMudCrocodile = "elite-mud-crocodile";
    public const string EliteGorgonDemon = "elite-gorgon-demon";
    public const string EliteJungleWizardC5 = "elite-jungle-wizard-c5";
    public const string EliteJungleWizardC6 = "elite-jungle-wizard-c6";
    public const string EliteDarkShaman = "elite-dark-gorgon-shaman";
    public const string EliteDarkPriest = "elite-dark-gorgon-priest";
    public const string EliteAstrologer = "elite-gorgon-astrologer";
    public const string EliteGuardianA = "elite-gorgon-guardian-a";
    public const string EliteAxeman = "elite-gorgon-axeman";
    public const string EliteHammerSoldier = "elite-gorgon-hammer-soldier";
    public const string EliteCrazyAxemanC = "elite-crazy-axeman-c";
    public const string EliteJungleWizardB = "elite-jungle-wizard-b";
    public const string EliteGuardianB = "elite-gorgon-guardian-b";
    public const string EliteGorgonWizard = "elite-gorgon-wizard";
    public const string EliteCyclopsSwordsman = "elite-cyclops-swordsman";
    public const string ElitePriestA12 = "elite-priest-a-012";
    public const string ElitePriestB12 = "elite-priest-b-012";
    public const string EliteShamanC9 = "elite-shaman-c-009";
    public const string EliteShamanC8 = "elite-shaman-c-008";
    public const string EliteGorgonPriestC14 = "elite-gorgon-priest-c-014";
    public const string EliteAstrologerB9 = "elite-astrologer-b-009";
    public const string EliteAstrologerA6 = "elite-astrologer-a-006";
    public const string PikemanB = "normal-gorgon-pikeman-b";
    public const string PikemanA = "normal-gorgon-pikeman-a";
    public const string Shaman = "normal-gorgon-shaman";
    public const string MudCrocodile = "normal-mud-crocodile";
    public const string JungleDeer = "normal-jungle-deer";
    public const string JungleWizard = "normal-gorgon-jungle-wizard";
    public const string GiantAxeman = "normal-giant-gorgon-axeman";
    public const string Astrologer = "normal-gorgon-astrologer";
    public const string AxemanA = "normal-gorgon-axeman-a";
    public const string AxemanB = "normal-gorgon-axeman-b";
}

internal static class MedusaIslandRosterTemplates
{
    public static readonly ImmutableArray<MedusaIslandRosterTemplatePair> All =
    [
        Boss(MedusaIslandRosterTemplateAliases.Stheno, "Stheno", 3,
            "B_bossB_mage_010", "B_bossBD_mage_010"),
        Boss(MedusaIslandRosterTemplateAliases.Euryale, "Euryale", 3,
            "B_bossA_mage_010", "B_bossAD_mage_010"),
        Boss(MedusaIslandRosterTemplateAliases.Chrysaor, "Chrysaor", 1,
            "B_bossA_skeleton_001", "B_bossAD_skeleton_001"),
        Boss(MedusaIslandRosterTemplateAliases.Medusa, "Medusa", 3,
            "B_bossA_medusa_008", "B_bossAD_medusa_008"),
        Elite(MedusaIslandRosterTemplateAliases.EliteArcher,
            "[Elite]Gorgon Archer", 1,
            "B_eliteA_centaur_002", "B_eliteAD_centaur_002"),
        Elite(MedusaIslandRosterTemplateAliases.EliteCrazyAxemanA,
            "[Elite]Crazy Gorgon Axeman", 1,
            "B_eliteA_centaur_004", "B_eliteAD_centaur_004"),
        Elite(MedusaIslandRosterTemplateAliases.EliteShamanSix,
            "[Elite]Gorgon Shaman", 2,
            "B_eliteA_centaur_006", "B_eliteAD_centaur_006"),
        Elite(MedusaIslandRosterTemplateAliases.EliteShamanEight,
            "[Elite]Gorgon Shaman", 2,
            "B_eliteA_centaur_008", "B_eliteAD_centaur_008"),
        Elite(MedusaIslandRosterTemplateAliases.EliteMudCrocodile,
            "[Elite]Mud Crocodile", 1,
            "A_eliteA_crocodilian_003", "A_eliteAD_crocodilian_003"),
        Elite(MedusaIslandRosterTemplateAliases.EliteGorgonDemon,
            "[Elite]Gorgon Demon", 1,
            "B_eliteA_cyclops_001", "B_eliteAD_cyclops_001"),
        Elite(MedusaIslandRosterTemplateAliases.EliteJungleWizardC5,
            "[Elite]Gorgon Jungle Wizard", 2,
            "B_eliteC_dryad_005", "B_eliteCD_dryad_005"),
        Elite(MedusaIslandRosterTemplateAliases.EliteJungleWizardC6,
            "[Elite]Gorgon Jungle Wizard", 2,
            "B_eliteC_dryad_006", "B_eliteCD_dryad_006"),
        Elite(MedusaIslandRosterTemplateAliases.EliteDarkShaman,
            "[Elite]Dark Gorgon Shaman", 2,
            "A_eliteB_mage_009", "A_eliteBD_mage_009"),
        Elite(MedusaIslandRosterTemplateAliases.EliteDarkPriest,
            "[Elite]Dark Gorgon Priest", 2,
            "B_eliteA_mage_011", "B_eliteAD_mage_011"),
        Elite(MedusaIslandRosterTemplateAliases.EliteAstrologer,
            "[Elite]Gorgon Astrologer", 2,
            "B_eliteA_satyr_005", "B_eliteAD_satyr_005"),
        Elite(MedusaIslandRosterTemplateAliases.EliteGuardianA,
            "[Elite]Gorgon Guardian", 1,
            "B_eliteA_skeleton_003", "B_eliteAD_skeleton_003"),
        Elite(MedusaIslandRosterTemplateAliases.EliteAxeman,
            "[Elite]Gorgon Axeman", 1,
            "B_eliteA_skeleton_005", "B_eliteAD_skeleton_005"),
        Elite(MedusaIslandRosterTemplateAliases.EliteHammerSoldier,
            "[Elite]Gorgon Hammer Soldier", 1,
            "B_eliteC_skeleton_007", "B_eliteCD_skeleton_007"),
        Elite(MedusaIslandRosterTemplateAliases.EliteCrazyAxemanC,
            "[Elite]Crazy Gorgon Axeman", 1,
            "B_eliteC_centaur_004", "B_eliteCD_centaur_004"),
        Elite(MedusaIslandRosterTemplateAliases.EliteJungleWizardB,
            "[Elite]Gorgon Jungle Wizard", 2,
            "B_eliteB_dryad_005", "B_eliteBD_dryad_005"),
        Elite(MedusaIslandRosterTemplateAliases.EliteGuardianB,
            "[Elite]Gorgon Guardian", 1,
            "B_eliteB_skeleton_003", "B_eliteBD_skeleton_003"),
        Elite(MedusaIslandRosterTemplateAliases.EliteGorgonWizard,
            "[Elite]Gorgon Wizard", 2,
            "B_eliteD_skeleton_011", "B_eliteDD_skeleton_011"),
        Elite(MedusaIslandRosterTemplateAliases.EliteCyclopsSwordsman,
            "[Elite] Cyclops Swordsman", 1,
            "B_eliteA_cyclops_002", "B_eliteAD_cyclops_002"),
        Elite(MedusaIslandRosterTemplateAliases.ElitePriestA12,
            "[Elite]Dark Gorgon Priest", 2,
            "A_eliteA_mage_012", "A_eliteAD_mage_012"),
        Elite(MedusaIslandRosterTemplateAliases.ElitePriestB12,
            "[Elite]Dark Gorgon Priest", 2,
            "B_eliteB_mage_012", "B_eliteBD_mage_012"),
        Elite(MedusaIslandRosterTemplateAliases.EliteShamanC9,
            "[Elite]Dark Gorgon Shaman", 2,
            "A_eliteC_mage_009", "A_eliteCD_mage_009"),
        Elite(MedusaIslandRosterTemplateAliases.EliteShamanC8,
            "[Elite]Gorgon Shaman", 2,
            "B_eliteC_centaur_008", "B_eliteCD_centaur_008"),
        Elite(MedusaIslandRosterTemplateAliases.EliteGorgonPriestC14,
            "[Elite]Gorgon Priest", 2,
            "B_eliteC_skeleton_014", "B_eliteCD_skeleton_014"),
        Elite(MedusaIslandRosterTemplateAliases.EliteAstrologerB9,
            "[Elite]Gorgon Astrologer", 2,
            "B_eliteB_satyr_009", "B_eliteBD_satyr_009"),
        Elite(MedusaIslandRosterTemplateAliases.EliteAstrologerA6,
            "[Elite]Gorgon Astrologer", 2,
            "B_eliteA_satyr_006", "B_eliteAD_satyr_006"),
        Normal(MedusaIslandRosterTemplateAliases.PikemanB, "Gorgon Pikeman", 1,
            "B_normalB_centaur_005", "B_normalBD_centaur_005"),
        Normal(MedusaIslandRosterTemplateAliases.PikemanA, "Gorgon Pikeman", 1,
            "B_normalA_centaur_005", "B_normalAD_centaur_005"),
        Normal(MedusaIslandRosterTemplateAliases.Shaman, "Gorgon Shaman", 2,
            "B_normalA_centaur_007", "B_normalAD_centaur_007"),
        Normal(MedusaIslandRosterTemplateAliases.MudCrocodile, "Mud Crocodile", 1,
            "A_normalA_crocodilian_003", "A_normalAD_crocodilian_003"),
        Normal(MedusaIslandRosterTemplateAliases.JungleDeer, "Jungle Deer", 1,
            "B_normalB_deer_007", "B_normalBD_deer_007"),
        Normal(MedusaIslandRosterTemplateAliases.JungleWizard,
            "Gorgon Jungle Wizard", 2,
            "A_normalA_dryad_005", "A_normalAD_dryad_005"),
        Normal(MedusaIslandRosterTemplateAliases.GiantAxeman,
            "Giant Gorgon Axeman", 1,
            "B_normalB_satyr_004", "B_normalBD_satyr_004"),
        Normal(MedusaIslandRosterTemplateAliases.Astrologer,
            "Gorgon Astrologer", 2,
            "B_normalA_satyr_007", "B_normalAD_satyr_007"),
        Normal(MedusaIslandRosterTemplateAliases.AxemanA, "Gorgon Axeman", 1,
            "B_normalA_skeleton_005", "B_normalAD_skeleton_005"),
        Normal(MedusaIslandRosterTemplateAliases.AxemanB, "Gorgon Axeman", 1,
            "B_normalB_skeleton_005", "B_normalBD_skeleton_005")
    ];

    private static MedusaIslandRosterTemplatePair Normal(
        string alias, string name, short attackType,
        string enhancedKey, string normalKey) =>
        new(alias, name, MedusaMonsterRank.Normal, attackType,
            enhancedKey, normalKey);

    private static MedusaIslandRosterTemplatePair Elite(
        string alias, string name, short attackType,
        string enhancedKey, string normalKey) =>
        new(alias, name, MedusaMonsterRank.Elite, attackType,
            enhancedKey, normalKey);

    private static MedusaIslandRosterTemplatePair Boss(
        string alias, string name, short attackType,
        string enhancedKey, string normalKey) =>
        new(alias, name, MedusaMonsterRank.Boss, attackType,
            enhancedKey, normalKey);
}
