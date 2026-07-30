using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Application.Talents;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.Infrastructure.Rewards;
using Godswar.Server.Infrastructure.Talents;
using Godswar.Server.Infrastructure.Zodiac;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure;

/// <summary>
/// Owns one shared PostgreSQL pool for extracted application data paths.
/// The legacy broad store retains its existing pool until later backlog
/// slices migrate its remaining operations.
/// </summary>
internal sealed class PostgresApplicationDataRuntime :
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresOutboxDispatcher _outboxDispatcher;

    public PostgresApplicationDataRuntime(
        string connectionString,
        PostgresOutboxDispatcherOptions outboxOptions,
        ZodiacEnergyPolicy zodiacEnergyPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(outboxOptions);
        outboxOptions.Validate();

        _dataSource = NpgsqlDataSource.Create(connectionString);
        CharacterSnapshots =
            new PostgresCharacterSnapshotReader(_dataSource);
        CharacterCheckpoints =
            new PostgresCharacterCheckpointStore(_dataSource);
        CharacterLifecycleCommands =
            new PostgresCharacterLifecycleCommandExecutor(
                _dataSource,
                outboxOptions);
        TalentUpgradeCommands =
            new PostgresTalentUpgradeCommandExecutor(
                _dataSource,
                outboxOptions);
        DeveloperItemGrantCommands =
            new PostgresDeveloperItemGrantCommandExecutor(
                _dataSource,
                outboxOptions);
        DeveloperBagClearCommands =
            new PostgresDeveloperBagClearCommandExecutor(
                _dataSource,
                outboxOptions);
        MakeAttributeStoneCommands =
            new PostgresMakeAttributeStoneCommandExecutor(
                _dataSource,
                outboxOptions);
        MaterialConversionCommands =
            new PostgresGearMentorMaterialConversionCommandExecutor(
                _dataSource,
                outboxOptions);
        DecomposeGearCommands =
            new PostgresGearMentorDecomposeCommandExecutor(
                _dataSource,
                outboxOptions);
        GearEnhancementCommands =
            new PostgresGearEnhancementCommandExecutor(
                _dataSource,
                outboxOptions);
        EquipmentForgeCommands =
            new PostgresEquipmentForgeCommandExecutor(
                _dataSource,
                outboxOptions);
        KitBagItemDeleteCommands =
            new PostgresKitBagItemDeleteCommandExecutor(
                _dataSource,
                outboxOptions);
        KitBagItemMoveCommands =
            new PostgresKitBagItemMoveCommandExecutor(
                _dataSource,
                outboxOptions);
        EquipmentBagTransferCommands =
            new PostgresEquipmentBagTransferCommandExecutor(
                _dataSource,
                outboxOptions);
        HolyStoneCommands =
            new PostgresHolyStoneCommandExecutor(
                _dataSource,
                outboxOptions);
        ZodiacSkillGridActivationCommands =
            new PostgresZodiacSkillGridActivationCommandExecutor(
                _dataSource,
                outboxOptions);
        ZodiacSkillGridUpgradeCommands =
            new PostgresZodiacSkillGridUpgradeCommandExecutor(
                _dataSource,
                outboxOptions);
        ZodiacSkillGridSelectionCommands =
            new PostgresZodiacSkillGridSelectionCommandExecutor(
                _dataSource,
                outboxOptions);
        MonsterDeathRewardCommands =
            new PostgresMonsterDeathRewardCommandExecutor(
                _dataSource,
                outboxOptions);
        ProgressionIntervalSettlementCommands =
            new PostgresProgressionIntervalSettlementCommandExecutor(
                _dataSource,
                outboxOptions,
                zodiacEnergyPolicy);
        PetDurableCommands =
            new PostgresPetDurableCommandExecutor(
                _dataSource,
                outboxOptions);
        _outboxDispatcher = new PostgresOutboxDispatcher(
            _dataSource,
            [
                new CharacterLifecycleOutboxConsumer(),
                new TalentUpgradeOutboxConsumer(),
                new CharacterInventoryOutboxConsumer(),
                new ZodiacSkillGridActivationOutboxConsumer(),
                new ZodiacSkillGridUpgradeOutboxConsumer(),
                new ZodiacSkillGridSelectionOutboxConsumer(),
                new MonsterDeathRewardOutboxConsumer(),
                new ProgressionIntervalSettlementOutboxConsumer(),
                new PetDurableOutboxConsumer()
            ],
            outboxOptions);
        OutboxEnabled = outboxOptions.Enabled;
    }

    public ICharacterSnapshotReader CharacterSnapshots { get; }

    public ICharacterCheckpointStore CharacterCheckpoints { get; }

    public ICharacterLifecycleCommandExecutor
        CharacterLifecycleCommands
    { get; }

    public ITalentUpgradeCommandExecutor TalentUpgradeCommands { get; }

    public IDeveloperItemGrantCommandExecutor
        DeveloperItemGrantCommands
    { get; }

    public IDeveloperBagClearCommandExecutor
        DeveloperBagClearCommands
    { get; }

    public IMakeAttributeStoneCommandExecutor
        MakeAttributeStoneCommands
    { get; }

    public IGearMentorMaterialConversionCommandExecutor
        MaterialConversionCommands
    { get; }

    public IGearMentorDecomposeGearCommandExecutor
        DecomposeGearCommands
    { get; }

    public IGearEnhancementCommandExecutor GearEnhancementCommands
    { get; }

    public IEquipmentForgeCommandExecutor EquipmentForgeCommands
    { get; }

    public IKitBagItemDeleteCommandExecutor KitBagItemDeleteCommands
    { get; }

    public IKitBagItemMoveCommandExecutor KitBagItemMoveCommands
    { get; }

    public IEquipmentBagTransferCommandExecutor
        EquipmentBagTransferCommands
    { get; }

    public IHolyStoneCommandExecutor HolyStoneCommands { get; }

    public IZodiacSkillGridActivationCommandExecutor
        ZodiacSkillGridActivationCommands
    { get; }

    public IZodiacSkillGridUpgradeCommandExecutor
        ZodiacSkillGridUpgradeCommands
    { get; }

    public IZodiacSkillGridSelectionCommandExecutor
        ZodiacSkillGridSelectionCommands
    { get; }

    public IMonsterDeathRewardCommandExecutor
        MonsterDeathRewardCommands
    { get; }

    public IProgressionIntervalSettlementCommandExecutor
        ProgressionIntervalSettlementCommands
    { get; }

    public IPetDurableCommandExecutor PetDurableCommands { get; }

    public bool OutboxEnabled { get; }

    public Task RunOutboxAsync(
        CancellationToken cancellationToken = default) =>
        _outboxDispatcher.RunAsync(cancellationToken);

    public ValueTask DisposeAsync() =>
        _dataSource.DisposeAsync();
}
