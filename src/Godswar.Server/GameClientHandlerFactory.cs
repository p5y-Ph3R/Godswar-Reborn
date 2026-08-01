using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;
using Godswar.Server.State;

namespace Godswar.Server;

internal sealed class GameClientHandlerFactory(
    IGameStore store,
    IAccountDirectory accountDirectory,
    IAccountPresenceWriter accountPresence,
    GameSessionRegistry registry,
    ICharacterSnapshotReader characterSnapshots,
    IWorldContentReader worldContent,
    DeveloperCommandOptions developerCommands,
    ICharacterCheckpointCoordinator characterCheckpoints,
    ServerGameplayPersistenceProviders gameplayPersistence,
    PostgresApplicationDataRuntime? postgresRuntime,
    IPlayerCoordinationLeaseIssuer? playerCoordination = null,
    GameplayRuntimeCatalogs? gameplayCatalogs = null,
    GameplayItemContent? itemContent = null,
    IPetContentCatalog? petContent = null)
{
    public GameClientHandler Create(
        ClientSession session,
        SecurePhase4AcceptanceFaults? phase4AcceptanceFaults = null,
        LegacyAuthenticationAccess? legacyAuthenticationAccess = null) =>
        new(
            session,
            store,
            registry,
            characterSnapshots,
            worldContent,
            developerCommands,
            phase4AcceptanceFaults,
            legacyAuthenticationAccess:
                legacyAuthenticationAccess,
            talentUpgradeCommands:
                postgresRuntime?.TalentUpgradeCommands,
            developerItemGrantCommands:
                postgresRuntime?.DeveloperItemGrantCommands,
            developerBagClearCommands:
                postgresRuntime?.DeveloperBagClearCommands,
            makeAttributeStoneCommands:
                postgresRuntime?.MakeAttributeStoneCommands,
            gearMentorMaterialConversionCommands:
                postgresRuntime?.MaterialConversionCommands,
            gearMentorDecomposeGearCommands:
                postgresRuntime?.DecomposeGearCommands,
            gearEnhancementCommands:
                postgresRuntime?.GearEnhancementCommands,
            equipmentForgeCommands:
                postgresRuntime?.EquipmentForgeCommands,
            kitBagItemDeleteCommands:
                postgresRuntime?.KitBagItemDeleteCommands,
            kitBagItemMoveCommands:
                postgresRuntime?.KitBagItemMoveCommands,
            equipmentBagTransferCommands:
                postgresRuntime?.EquipmentBagTransferCommands,
            holyStoneCommands:
                postgresRuntime?.HolyStoneCommands,
            zodiacSkillGridActivationCommands:
                postgresRuntime?.ZodiacSkillGridActivationCommands,
            zodiacSkillGridUpgradeCommands:
                postgresRuntime?.ZodiacSkillGridUpgradeCommands,
            zodiacSkillGridSelectionCommands:
                postgresRuntime?.ZodiacSkillGridSelectionCommands,
            characterCheckpoints:
                characterCheckpoints,
            characterLifecycleCommands:
                postgresRuntime?.CharacterLifecycleCommands,
            monsterDeathRewardCommands:
                postgresRuntime?.MonsterDeathRewardCommands,
            petDurableCommands:
                postgresRuntime?.PetDurableCommands,
            characterRuntimeProjections:
                gameplayPersistence.CharacterRuntime,
            ownedPetSnapshots:
                gameplayPersistence.OwnedPets,
            worldBossAreaControl:
                gameplayPersistence.WorldBossAreaControl,
            worldBossRespawns:
                gameplayPersistence.WorldBossRespawns,
            playerCoordination:
                playerCoordination,
            accountDirectory:
                accountDirectory,
            accountPresence:
                accountPresence,
            requiresDurableMonsterRewardCommands:
                postgresRuntime is not null,
            requiresDurablePlayerCommands:
                postgresRuntime is not null,
            gameplayCatalogs:
                gameplayCatalogs ??
                GameplayRuntimeCatalogs.Create(worldContent.Gameplay),
            itemContent:
                itemContent,
            petContent:
                petContent);
}
