using Godswar.Server.Application.Characters;
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
    GameSessionRegistry registry,
    ICharacterSnapshotReader characterSnapshots,
    IWorldContentReader worldContent,
    DeveloperCommandOptions developerCommands,
    ICharacterCheckpointCoordinator characterCheckpoints,
    PostgresApplicationDataRuntime? postgresRuntime)
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
                postgresRuntime?.CharacterLifecycleCommands);
}
