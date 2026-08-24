using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.Infrastructure.Rewards;
using Godswar.Server.Infrastructure.Talents;
using Godswar.Server.Infrastructure.Zodiac;
using Godswar.Server.Infrastructure.Warehouse;

namespace Godswar.Server.Infrastructure.Messaging;

internal static class PostgresOutboxConsumerCatalog
{
    public static IReadOnlyList<IOutboxEventConsumer> Create() =>
    [
        new CharacterLifecycleOutboxConsumer(),
        new TalentUpgradeOutboxConsumer(),
        new CharacterInventoryOutboxConsumer(),
        new ZodiacSkillGridActivationOutboxConsumer(),
        new ZodiacSkillGridUpgradeOutboxConsumer(),
        new ZodiacSkillGridSelectionOutboxConsumer(),
        new MonsterDeathRewardOutboxConsumer(),
        new ProgressionIntervalSettlementOutboxConsumer(),
        new PetDurableOutboxConsumer(),
        new WarehouseProjectionOutboxConsumer()
    ];
}
