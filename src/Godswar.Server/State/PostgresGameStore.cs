using System.Collections.Concurrent;
using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore : IGameStore
{
    private const string AccountColumns = "id, username, password, last_login_time, vip_tier, vip_expires_at";
    private const short ItemLocationEquipment = 0;
    private const short ItemLocationKitBag = 1;
    private const int EquipmentProjectionSlots = 24;
    private const int KitBagProjectionSlots = 96;
    private const string CharacterColumns = """
        cb.id, cb.account_id, cb.name, cb.gender, cb.camp, cb.profession, cb.hair_style,
        COALESCE(cb.face_shap, 0), cb.belief, cb."Map", cb.fighter_job_lv, cb."MaxHP",
        cb."MaxMP", cb."curHP", cb."curMP", cb."Pos_X", cb."Pos_Z", cb."Register_time",
        COALESCE(ck.equip, ''), COALESCE(ck.kitbag_1, ''), cb."SkillPoint", cb."SkillExp",
        COALESCE(cb.holy_suit_points, 0),
        COALESCE((SELECT cr.weapon_rank FROM character_rank_summary cr WHERE cr.user_id = cb.id), 0::smallint),
        COALESCE((SELECT cr.weapon_aura_effect FROM character_rank_summary cr WHERE cr.user_id = cb.id), 0),
        COALESCE((SELECT cr.armor_rank FROM character_rank_summary cr WHERE cr.user_id = cb.id), 0::smallint),
        COALESCE((SELECT cr.armor_aura_effect FROM character_rank_summary cr WHERE cr.user_id = cb.id), 0),
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
        )
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSchemaMigrationRunner _schemaMigrationRunner;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _vitalsPersistenceLocks = [];

    public PostgresGameStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("PostgreSQL storage requires a connection string.");
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _schemaMigrationRunner = new PostgresSchemaMigrationRunner(_dataSource);
    }

    public async Task EnsureSeedDataAsync(CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 30;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _schemaMigrationRunner.InitializeAsync(
                    LegacySchemaBootstrap.LoadAsync,
                    PostgresSchemaMigrationCatalog.All,
                    cancellationToken);
                await SeedItemTemplatesAsync(cancellationToken);
                await ApplyServerCompatibilityTemplateOverridesAsync(cancellationToken);
                await SeedItemAttributeTemplatesAsync(cancellationToken);
                await SeedSkillTalentTemplatesAsync(cancellationToken);
                await SeedNpcTemplatesAsync(cancellationToken);
                await SeedMapTemplatesAsync(cancellationToken);
                await SeedMonsterTemplatesAsync(cancellationToken);
                await SeedWorldBossAreasAsync(cancellationToken);
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

    public async Task MarkAccountOfflineAsync(int accountId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE accounts
            SET login_status = 0,
                last_logout_time = now(),
                total_online_time = total_online_time + GREATEST(0, EXTRACT(EPOCH FROM (now() - last_login_time))::bigint)
            WHERE id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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
