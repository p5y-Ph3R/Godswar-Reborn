using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Talents;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Talents;
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
        PostgresOutboxDispatcherOptions outboxOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(outboxOptions);
        outboxOptions.Validate();

        _dataSource = NpgsqlDataSource.Create(connectionString);
        CharacterSnapshots =
            new PostgresCharacterSnapshotReader(_dataSource);
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
        _outboxDispatcher = new PostgresOutboxDispatcher(
            _dataSource,
            [
                new TalentUpgradeOutboxConsumer(),
                new CharacterInventoryOutboxConsumer()
            ],
            outboxOptions);
        OutboxEnabled = outboxOptions.Enabled;
    }

    public ICharacterSnapshotReader CharacterSnapshots { get; }

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

    public bool OutboxEnabled { get; }

    public Task RunOutboxAsync(
        CancellationToken cancellationToken = default) =>
        _outboxDispatcher.RunAsync(cancellationToken);

    public ValueTask DisposeAsync() =>
        _dataSource.DisposeAsync();
}
