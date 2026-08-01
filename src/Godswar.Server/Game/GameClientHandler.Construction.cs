using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Rewards;
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
        IGameStore gameStore,
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
            characterCheckpoints = null,
        ICharacterLifecycleCommandExecutor?
            characterLifecycleCommands = null,
        IMonsterDeathRewardCommandExecutor?
            monsterDeathRewardCommands = null,
        IPetDurableCommandExecutor?
            petDurableCommands = null,
        ICharacterRuntimeProjectionReader?
            characterRuntimeProjections = null,
        IOwnedPetSnapshotReader?
            ownedPetSnapshots = null,
        IWorldBossAreaControlStore?
            worldBossAreaControl = null,
        IWorldBossRespawnReader?
            worldBossRespawns = null,
        IPlayerCoordinationLeaseIssuer?
            playerCoordination = null,
        IAccountDirectory? accountDirectory = null,
        IAccountPresenceWriter? accountPresence = null,
        bool requiresDurableMonsterRewardCommands = false,
        bool requiresDurablePlayerCommands = false,
        GameplayRuntimeCatalogs? gameplayCatalogs = null,
        GameplayItemContent? itemContent = null,
        IPetContentCatalog? petContent = null)
    {
        if (backhaulSkillCastTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(backhaulSkillCastTime));
        }

        _session = session;
        _store = gameStore;
        _accountDirectory = accountDirectory ??
            gameStore as IAccountDirectory ??
            throw new ArgumentException(
                "An account directory is required.",
                nameof(accountDirectory));
        _accountPresence = accountPresence ??
            gameStore as IAccountPresenceWriter ??
            throw new ArgumentException(
                "An account presence writer is required.",
                nameof(accountPresence));
        _registry = registry;
        _characterSnapshots =
            characterSnapshots ?? throw new ArgumentNullException(
                nameof(characterSnapshots));
        _worldContent =
            worldContent ?? throw new ArgumentNullException(
                nameof(worldContent));
        _gameplayCatalogs = gameplayCatalogs ??
            GameplayRuntimeCatalogs.Create(worldContent.Gameplay);
        _itemContent = itemContent;
        _petContent = petContent;
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
        _characterLifecycleCommands =
            characterLifecycleCommands;
        _monsterDeathRewardCommands =
            monsterDeathRewardCommands;
        _requiresDurableMonsterRewardCommands =
            requiresDurableMonsterRewardCommands;
        _requiresDurablePlayerCommands =
            requiresDurablePlayerCommands;
        _petDurableCommands = petDurableCommands;
        _characterRuntimeProjections =
            characterRuntimeProjections ??
            gameStore as ICharacterRuntimeProjectionReader ??
            throw new ArgumentException(
                "A character runtime projection reader is required.",
                nameof(characterRuntimeProjections));
        _ownedPetSnapshots =
            ownedPetSnapshots ??
            gameStore as IOwnedPetSnapshotReader ??
            throw new ArgumentException(
                "An owned-pet snapshot reader is required.",
                nameof(ownedPetSnapshots));
        _worldBossAreaControl =
            worldBossAreaControl ??
            gameStore as IWorldBossAreaControlStore ??
            throw new ArgumentException(
                "A world-boss area-control store is required.",
                nameof(worldBossAreaControl));
        _worldBossRespawns =
            worldBossRespawns ??
            gameStore as IWorldBossRespawnReader ??
            throw new ArgumentException(
                "A world-boss respawn reader is required.",
                nameof(worldBossRespawns));
        _playerCoordination = playerCoordination;
        if (_requiresDurablePlayerCommands &&
            new object?[]
            {
                _talentUpgradeCommands,
                _developerItemGrantCommands,
                _developerBagClearCommands,
                _makeAttributeStoneCommands,
                _gearMentorMaterialConversionCommands,
                _gearMentorDecomposeGearCommands,
                _gearEnhancementCommands,
                _equipmentForgeCommands,
                _kitBagItemDeleteCommands,
                _kitBagItemMoveCommands,
                _equipmentBagTransferCommands,
                _holyStoneCommands,
                _zodiacSkillGridActivationCommands,
                _zodiacSkillGridUpgradeCommands,
                _zodiacSkillGridSelectionCommands,
                _characterLifecycleCommands,
                _petDurableCommands,
                _characterCheckpoints
            }.Any(static provider => provider is null))
        {
            throw new InvalidOperationException(
                "Production player mutation composition requires every " +
                "extracted durable command executor and the character " +
                "checkpoint coordinator.");
        }

        _developerCommands =
            developerCommands ?? new DeveloperCommandOptions();
        _legacyAuthenticationAccess = legacyAuthenticationAccess;
        _phase4AcceptanceFaults = phase4AcceptanceFaults;
        _mapTransitionReadyTimeout =
            mapTransitionReadyTimeout ??
            DefaultMapTransitionReadyTimeout;
        _backhaulSkillCastTime = backhaulSkillCastTime;
    }

    private GameplayItemContent RequireItemContent() =>
        _itemContent ?? throw new InvalidOperationException(
            "This gameplay operation requires a pinned item-content revision.");

    private IPetContentCatalog RequirePetContent() =>
        _petContent ?? throw new InvalidOperationException(
            "This gameplay operation requires a pinned pet-content revision.");

}
