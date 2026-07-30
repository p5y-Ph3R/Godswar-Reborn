using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Talents;
using Godswar.Server.Application.World;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    public GameClientHandler(
        ClientSession session,
        IGameStore store,
        GameSessionRegistry registry,
        ICharacterSnapshotReader characterSnapshots,
        IWorldContentReader worldContent,
        DeveloperCommandOptions? developerCommands = null,
        SecurePhase4AcceptanceFaults?
            phase4AcceptanceFaults = null,
        TimeSpan? mapTransitionReadyTimeout = null,
        TimeSpan? backhaulSkillCastTime = null,
        LegacyAuthenticationAccess?
            legacyAuthenticationAccess = null,
        ITalentUpgradeCommandExecutor?
            talentUpgradeCommands = null,
        IDeveloperItemGrantCommandExecutor?
            developerItemGrantCommands = null,
        IDeveloperBagClearCommandExecutor?
            developerBagClearCommands = null,
        IMakeAttributeStoneCommandExecutor?
            makeAttributeStoneCommands = null,
        IGearMentorMaterialConversionCommandExecutor?
            gearMentorMaterialConversionCommands = null,
        IGearMentorDecomposeGearCommandExecutor?
            gearMentorDecomposeGearCommands = null,
        IGearEnhancementCommandExecutor?
            gearEnhancementCommands = null,
        IEquipmentForgeCommandExecutor?
            equipmentForgeCommands = null,
        IKitBagItemDeleteCommandExecutor?
            kitBagItemDeleteCommands = null,
        IKitBagItemMoveCommandExecutor?
            kitBagItemMoveCommands = null,
        IEquipmentBagTransferCommandExecutor?
            equipmentBagTransferCommands = null,
        IHolyStoneCommandExecutor?
            holyStoneCommands = null,
        IZodiacSkillGridActivationCommandExecutor?
            zodiacSkillGridActivationCommands = null,
        IZodiacSkillGridUpgradeCommandExecutor?
            zodiacSkillGridUpgradeCommands = null,
        IZodiacSkillGridSelectionCommandExecutor?
            zodiacSkillGridSelectionCommands = null,
        ICharacterCheckpointCoordinator?
            characterCheckpoints = null)
    {
        if (backhaulSkillCastTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(backhaulSkillCastTime));
        }

        _session = session;
        _store = store;
        _registry = registry;
        _characterSnapshots =
            characterSnapshots ?? throw new ArgumentNullException(
                nameof(characterSnapshots));
        _worldContent =
            worldContent ?? throw new ArgumentNullException(
                nameof(worldContent));
        _talentUpgradeCommands = talentUpgradeCommands;
        _developerItemGrantCommands = developerItemGrantCommands;
        _developerBagClearCommands = developerBagClearCommands;
        _makeAttributeStoneCommands = makeAttributeStoneCommands;
        _gearMentorMaterialConversionCommands =
            gearMentorMaterialConversionCommands;
        _gearMentorDecomposeGearCommands =
            gearMentorDecomposeGearCommands;
        _gearEnhancementCommands = gearEnhancementCommands;
        _equipmentForgeCommands = equipmentForgeCommands;
        _kitBagItemDeleteCommands = kitBagItemDeleteCommands;
        _kitBagItemMoveCommands = kitBagItemMoveCommands;
        _equipmentBagTransferCommands = equipmentBagTransferCommands;
        _holyStoneCommands = holyStoneCommands;
        _zodiacSkillGridActivationCommands =
            zodiacSkillGridActivationCommands;
        _zodiacSkillGridUpgradeCommands =
            zodiacSkillGridUpgradeCommands;
        _zodiacSkillGridSelectionCommands =
            zodiacSkillGridSelectionCommands;
        _characterCheckpoints = characterCheckpoints;
        _developerCommands =
            developerCommands ?? new DeveloperCommandOptions();
        _legacyAuthenticationAccess = legacyAuthenticationAccess;
        _phase4AcceptanceFaults = phase4AcceptanceFaults;
        _mapTransitionReadyTimeout =
            mapTransitionReadyTimeout ??
            DefaultMapTransitionReadyTimeout;
        _backhaulSkillCastTime = backhaulSkillCastTime;
    }
}
