using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Application.Talents;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.Accounts;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.Infrastructure.Rewards;
using Godswar.Server.Infrastructure.Reconciliation;
using Godswar.Server.Infrastructure.Talents;
using Godswar.Server.Infrastructure.Zodiac;
using Godswar.Server.Infrastructure.World;
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
    private readonly PostgresReconciliationWorker
        _reconciliationWorker;

    public PostgresApplicationDataRuntime(
        string connectionString,
        PostgresOutboxDispatcherOptions outboxOptions,
        ZodiacEnergyPolicy zodiacEnergyPolicy,
        GameplayItemContent itemContent,
        string gameplayContentRevision,
        IPetContentCatalog petContent,
        IPetOwnerMergeContentCatalog ownerMergeContent,
        IPetLearnedSkillContentCatalog learnedSkillContent,
        ReconciliationOptions? reconciliationOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(outboxOptions);
        ArgumentNullException.ThrowIfNull(itemContent);
        ArgumentNullException.ThrowIfNull(petContent);
        ArgumentNullException.ThrowIfNull(ownerMergeContent);
        ArgumentNullException.ThrowIfNull(learnedSkillContent);
        gameplayContentRevision =
            PostgresGameplayContentBinding.ValidateRequired(
                gameplayContentRevision);
        outboxOptions.Validate();

        _dataSource = NpgsqlDataSource.Create(connectionString);
        Accounts = new PostgresAccountStore(_dataSource);
        var characterReader =
            new PostgresCharacterSnapshotReader(
                _dataSource,
                itemContent.Templates,
                gameplayContentRevision,
                learnedSkillContent.Revision.Sha256);
        CharacterSnapshots = characterReader;
        CharacterRuntimeProjections = characterReader;
        OwnedPetSnapshots = characterReader;
        SealedPetSnapshots = characterReader;
        ExperienceBoosts =
            new PostgresExperienceBoostStateReader(
                _dataSource,
                gameplayContentRevision);
        var worldBossState =
            new PostgresWorldBossAreaControlStore(
                _dataSource,
                gameplayContentRevision);
        WorldBossAreaControl = worldBossState;
        WorldBossRespawns = worldBossState;
        ZodiacLevels = new PostgresZodiacLevelStore(_dataSource);
        CharacterCheckpoints =
            new PostgresCharacterCheckpointStore(_dataSource);
        CharacterLifecycleCommands =
            new PostgresCharacterLifecycleCommandExecutor(
                _dataSource,
                outboxOptions,
                gameplayContentRevision);
        TalentUpgradeCommands =
            new PostgresTalentUpgradeCommandExecutor(
                _dataSource,
                outboxOptions,
                gameplayContentRevision);
        DeveloperItemGrantCommands =
            new PostgresDeveloperItemGrantCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        DeveloperBagClearCommands =
            new PostgresDeveloperBagClearCommandExecutor(
                _dataSource,
                outboxOptions);
        MakeAttributeStoneCommands =
            new PostgresMakeAttributeStoneCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        var materialConversionCommands =
            new PostgresGearMentorMaterialConversionCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        MaterialConversionCommands = materialConversionCommands;
        ClassSuitCommands = materialConversionCommands;
        DecomposeGearCommands =
            new PostgresGearMentorDecomposeCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        GearEnhancementCommands =
            new PostgresGearEnhancementCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
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
                outboxOptions,
                itemContent);
        HolyStoneCommands =
            new PostgresHolyStoneCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        HolySuitCommands =
            new PostgresHolySuitCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
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
                outboxOptions,
                gameplayContentRevision);
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
                outboxOptions,
                itemContent,
                petContent,
                ownerMergeContent,
                learnedSkillContent);
        var outboxConsumers =
            PostgresOutboxConsumerCatalog.Create();
        _outboxDispatcher = new PostgresOutboxDispatcher(
            _dataSource,
            outboxConsumers,
            outboxOptions);
        OutboxEnabled = outboxOptions.Enabled;
        var effectiveReconciliationOptions =
            reconciliationOptions ?? new ReconciliationOptions();
        effectiveReconciliationOptions.Validate();
        var reconciliationMetrics = new ReconciliationMetrics();
        _reconciliationWorker = new PostgresReconciliationWorker(
            new ReconciliationRunner(
                new PostgresReconciliationReader(
                    _dataSource,
                    itemContent.Templates.Revision.Sha256,
                    outboxConsumers),
                effectiveReconciliationOptions,
                reconciliationMetrics),
            effectiveReconciliationOptions,
            reconciliationMetrics);
        ReconciliationRepair =
            new PostgresExpiredOutboxLeaseRepairer(
                _outboxDispatcher,
                reconciliationMetrics);
        ReconciliationEnabled =
            effectiveReconciliationOptions.Enabled;
    }

    public PostgresAccountStore Accounts { get; }

    public ICharacterSnapshotReader CharacterSnapshots { get; }

    public ICharacterRuntimeProjectionReader
        CharacterRuntimeProjections
    { get; }

    public IOwnedPetSnapshotReader OwnedPetSnapshots { get; }

    public ISealedPetSnapshotReader SealedPetSnapshots { get; }

    public IExperienceBoostStateReader ExperienceBoosts { get; }

    public IWorldBossAreaControlStore WorldBossAreaControl { get; }

    public IWorldBossRespawnReader WorldBossRespawns { get; }

    public IZodiacLevelStore ZodiacLevels { get; }

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

    public IClassSuitCommandExecutor ClassSuitCommands { get; }

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

    public IHolySuitCommandExecutor HolySuitCommands { get; }

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

    public bool ReconciliationEnabled { get; }

    public IReconciliationRepairer ReconciliationRepair { get; }

    public Task RunOutboxAsync(
        CancellationToken cancellationToken = default) =>
        _outboxDispatcher.RunAsync(cancellationToken);

    public Task RunReconciliationAsync(
        CancellationToken cancellationToken = default) =>
        _reconciliationWorker.RunAsync(cancellationToken);

    public ReconciliationWorkerSnapshot
        GetReconciliationSnapshot() =>
        _reconciliationWorker.GetSnapshot();

    public async Task<bool> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command =
            new NpgsqlCommand("SELECT 1;", connection)
            {
                CommandTimeout = 1
            };
        return await command.ExecuteScalarAsync(cancellationToken)
            is int and 1;
    }

    public ValueTask DisposeAsync() =>
        _dataSource.DisposeAsync();
}
