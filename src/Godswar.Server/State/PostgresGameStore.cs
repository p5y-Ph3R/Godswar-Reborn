using Godswar.Server.Application.Accounts;
using Godswar.Server.Infrastructure.Accounts;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.Infrastructure.World;
using Godswar.Server.Infrastructure.Zodiac;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Game;
using Godswar.Server.Application.Pets;
using System.Data;
using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore :
    IGameStore,
    IAccountCredentialStore,
    IAccountDirectory,
    IAccountPresenceWriter,
    ILegacyAccountLoginStore,
    IGameplayItemContentProvider
{
    private const short ItemLocationEquipment = 0;
    private const short ItemLocationKitBag = 1;
    private const int EquipmentProjectionSlots = 24;
    private const int KitBagProjectionSlots = 96;
    private const string CharacterColumns = """
        cb.id, cb.account_id, cb.name, cb.gender, cb.camp, cb.profession, cb.hair_style,
        COALESCE(cb.face_shap, 0), cb.belief, cb."Map", cb.fighter_job_lv, cb."MaxHP",
        cb."MaxMP", cb."curHP", cb."curMP", cb."Pos_X", cb."Pos_Z", cb."Register_time",
        COALESCE(equipment_projection.equip, ''),
        COALESCE(kitbag_projection.kitbag_1, ''),
        cb."SkillPoint", cb."SkillExp",
        COALESCE(cb.holy_suit_points, 0),
        item_rank_projection.weapon_rank,
        item_rank_projection.weapon_aura_effect,
        item_rank_projection.armor_rank,
        item_rank_projection.armor_aura_effect,
        cb.fighter_job_exp, cb.vitals_revision,
        cb.zodiac_type, cb.zodiac_lucky_status, cb.zodiac_lucky_expires_at,
        cb.zodiac_level, cb.zodiac_energy, cb.zodiac_accumulated_exp_x100,
        cb.zodiac_accumulated_talent_exp_x100, cb.zodiac_energy_remainder_x100,
        cb.zodiac_online_day, cb.zodiac_online_duration_ticks, cb.zodiac_last_online_at,
        cb.zodiac_last_compensation_day, cb."Money", cb."Stone",
        ARRAY(
            SELECT COALESCE(grid.level, 0)::integer
            FROM generate_series(0, 15) AS requested_grid(grid_index)
            LEFT JOIN character_zodiac_skill_grids grid
              ON grid.user_id = cb.id
             AND grid.grid_index = requested_grid.grid_index
            ORDER BY requested_grid.grid_index
        ),
        ARRAY(
            SELECT COALESCE(grid.selected_skill_id, -1)
            FROM generate_series(0, 15) AS requested_grid(grid_index)
            LEFT JOIN character_zodiac_skill_grids grid
              ON grid.user_id = cb.id
             AND grid.grid_index = requested_grid.grid_index
            ORDER BY requested_grid.grid_index
        ),
        cb.position_revision,
        cb.character_slot, cb.lifecycle_state, cb.lifecycle_version,
        cb.deleted_at, cb.restore_until, cb.purge_after,
        cb.fighter_level_sealed
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresAccountStore _accountStore;
    private readonly PostgresExperienceBoostStateReader
        _experienceBoostStateReader;
    private readonly PostgresWorldBossAreaControlStore
        _worldBossAreaControlStore;
    private readonly PostgresZodiacLevelStore _zodiacLevelStore;
    private GameplayItemContent? _itemContent;
    private IPetContentCatalog? _petContent;
    private readonly string? _gameplayContentRevision;

    public PostgresGameStore(
        string connectionString,
        GameplayItemContent? itemContent = null,
        string? gameplayContentRevision = null,
        IPetContentCatalog? petContent = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("PostgreSQL storage requires a connection string.");
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _gameplayContentRevision =
            PostgresGameplayContentBinding.ValidateOptional(
                gameplayContentRevision);
        _accountStore = new PostgresAccountStore(_dataSource);
        _experienceBoostStateReader =
            new PostgresExperienceBoostStateReader(
                _dataSource,
                _gameplayContentRevision);
        _worldBossAreaControlStore =
            new PostgresWorldBossAreaControlStore(
                _dataSource,
                _gameplayContentRevision);
        _zodiacLevelStore = new PostgresZodiacLevelStore(_dataSource);
        _itemContent = itemContent;
        _petContent = petContent;
    }

    public GameplayItemContent ItemContent =>
        _itemContent ?? throw new InvalidOperationException(
            "Item content must be pinned before gameplay operations run.");

    public IPetContentCatalog PetContent =>
        _petContent ?? throw new InvalidOperationException(
            "Pet content must be pinned before gameplay operations run.");

    private void AddItemContentRevisionParameter(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var parameter = command.CreateParameter();
        parameter.ParameterName = "itemContentRevision";
        parameter.DbType = DbType.String;
        parameter.Value = ItemContent.Templates.Revision.Sha256;
        command.Parameters.Add(parameter);
    }

    private void AddGameplayContentRevisionParameter(
        DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var parameter = command.CreateParameter();
        parameter.ParameterName =
            PostgresGameplayContentBinding.ParameterName;
        parameter.DbType = DbType.String;
        parameter.Value = _gameplayContentRevision is null
            ? DBNull.Value
            : _gameplayContentRevision;
        command.Parameters.Add(parameter);
    }

    public async Task EnsureSeedDataAsync(CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 30;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _itemContent ??= new GameplayItemContent(
                    await PostgresItemTemplateContentBootstrapper.LoadAsync(
                        _dataSource,
                        cancellationToken));
                _petContent ??=
                    await PostgresPetContentBootstrapper.LoadAsync(
                        _dataSource,
                        ItemContent.Templates,
                        cancellationToken);
                await ValidatePetGrowthPolicyAsync(cancellationToken);
                await ValidatePetGrowthStateAsync(cancellationToken);
                await ValidatePetInitialSavvyPolicyAsync(cancellationToken);
                await ValidatePetInitialSavvyStateAsync(cancellationToken);
                await ValidatePetAddedSavvyPolicyAsync(cancellationToken);
                await ValidatePetSavvyBaselineStateAsync(cancellationToken);
                await SyncCharacterEquipAsync(cancellationToken);
                await SyncCharacterStarterSkillsAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientStartupFailure(ex))
            {
                Console.WriteLine($"[db] waiting for PostgreSQL ({attempt}/{maxAttempts}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    public Task MarkAccountOfflineAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        _accountStore.MarkAccountOfflineAsync(
            accountId,
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    private static string CleanUsername(string username)
    {
        username = username.Trim('\0', ' ', '\t', '\r', '\n');
        return string.IsNullOrWhiteSpace(username) ? "player" : username;
    }

    private static string CleanCharacterName(string name)
    {
        name = name.Trim('\0', ' ', '\t', '\r', '\n');
        return string.IsNullOrWhiteSpace(name) ? $"Hero{Random.Shared.Next(1000, 9999)}" : name;
    }

    private static bool IsTransientStartupFailure(Exception ex)
    {
        return ex is NpgsqlException or TimeoutException or IOException
            || ex.InnerException is not null && IsTransientStartupFailure(ex.InnerException);
    }
}
