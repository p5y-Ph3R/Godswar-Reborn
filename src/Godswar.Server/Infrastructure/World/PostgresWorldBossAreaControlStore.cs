using System.Data;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Database;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.World;

internal sealed class PostgresWorldBossAreaControlStore :
    IWorldBossAreaControlStore,
    IWorldBossRespawnReader
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RealmId _realmId;
    private readonly string? _gameplayContentRevision;

    public PostgresWorldBossAreaControlStore(
        NpgsqlDataSource dataSource,
        string? gameplayContentRevision = null) :
        this(dataSource, RealmId.Tempest, gameplayContentRevision)
    {
    }

    public PostgresWorldBossAreaControlStore(
        NpgsqlDataSource dataSource,
        RealmId realmId,
        string? gameplayContentRevision = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }

        _realmId = realmId;
        _gameplayContentRevision =
            PostgresGameplayContentBinding.ValidateOptional(
                gameplayContentRevision);
    }

    public async Task<WorldBossAreaActivationResult> ActivateAsync(
        WorldBossAreaActivation activation,
        CancellationToken cancellationToken = default)
    {
        if (!WorldBossPersistenceContract.IsValid(activation))
        {
            return WorldBossAreaActivationResult.Invalid();
        }

        activation = activation with
        {
            KilledAtUtc = CanonicalizeUtc(activation.KilledAtUtc)
        };

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        // Every activation of a configured map first locks its content row.
        // This gives one writer ownership of the map even before a control row
        // exists, so delayed death events cannot replace a newer activation.
        var configured = await LockConfiguredAreaAsync(
            connection,
            transaction,
            activation,
            cancellationToken);
        if (configured is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return WorldBossAreaActivationResult.NotConfigured();
        }

        var current = await ReadCurrentControlAsync(
            connection,
            transaction,
            activation.MapId,
            cancellationToken);
        if (current is not null)
        {
            if (string.Equals(
                    current.DeathToken,
                    activation.DeathToken,
                    StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                return WorldBossAreaActivationResult.Duplicate(current);
            }

            if (current.ActivatedAtUtc >= activation.KilledAtUtc)
            {
                await transaction.CommitAsync(cancellationToken);
                return WorldBossAreaActivationResult.Stale(current);
            }
        }

        try
        {
            var committed = await UpsertControlAsync(
                connection,
                transaction,
                activation,
                cancellationToken);
            if (committed is null)
            {
                current = await ReadCurrentControlAsync(
                    connection,
                    transaction,
                    activation.MapId,
                    cancellationToken) ??
                    throw new InvalidDataException(
                        "The world-boss activation returned no durable control row.");
                await transaction.CommitAsync(cancellationToken);
                return string.Equals(
                    current.DeathToken,
                    activation.DeathToken,
                    StringComparison.Ordinal)
                    ? WorldBossAreaActivationResult.Duplicate(current)
                    : WorldBossAreaActivationResult.Stale(current);
            }

            await transaction.CommitAsync(cancellationToken);
            return WorldBossAreaActivationResult.Committed(committed);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // A death token is global. Reusing it for another map is invalid,
            // not another successful world-boss activation.
            await transaction.RollbackAsync(CancellationToken.None);
            return WorldBossAreaActivationResult.Invalid();
        }
    }

    public async Task<WorldBossRespawnSnapshot?> ReadActiveAsync(
        WorldBossRespawnReadRequest request,
        CancellationToken cancellationToken = default)
    {
        WorldBossPersistenceContract.Validate(request);
        request = request with
        {
            ReadAtUtc = CanonicalizeUtc(request.ReadAtUtc)
        };
        await using var command = _dataSource.CreateCommand(
            ActiveRespawnQuery);
        command.Parameters.AddWithValue("mapId", request.MapId);
        command.Parameters.AddWithValue("realmId", _realmId.Value);
        command.Parameters.Add(
            "readAt",
            NpgsqlDbType.TimestampTz).Value =
            request.ReadAtUtc.UtcDateTime;
        PostgresGameplayContentBinding.AddParameter(
            command,
            _gameplayContentRevision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var respawn = new WorldBossRespawnSnapshot(
            reader.GetInt16(0),
            reader.GetString(1),
            AsUtc(reader.GetDateTime(2)));
        WorldBossPersistenceContract.Validate(respawn);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "A map returned more than one active world-boss control row.");
        }

        return respawn;
    }

    private async Task<ConfiguredArea?> LockConfiguredAreaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldBossAreaActivation activation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT bonus_basis_points, respawn_interval_seconds
            FROM public.gameplay_world_boss_definitions
            WHERE map_id = @mapId
              AND template_key = @bossTemplateKey
              AND revision = COALESCE(
                  @gameplayContentRevision,
                  (
                      SELECT publication.revision
                      FROM public.gameplay_content_publication publication
                      WHERE publication.family = 'gameplay'
                  )
              )
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("mapId", activation.MapId);
        command.Parameters.AddWithValue(
            "bossTemplateKey",
            activation.BossTemplateKey);
        PostgresGameplayContentBinding.AddParameter(
            command,
            _gameplayContentRevision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var configured = new ConfiguredArea(
            reader.GetInt32(0),
            reader.GetInt32(1));
        if (configured.BonusBasisPoints < 0 ||
            configured.RespawnIntervalSeconds <= 0)
        {
            throw new InvalidDataException(
                "The configured world-boss area has invalid policy values.");
        }

        return configured;
    }

    private async Task<WorldBossAreaControlSnapshot?>
        ReadCurrentControlAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            short mapId,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                map_id,
                controlling_camp,
                boss_template_key,
                death_token,
                bonus_basis_points,
                activated_at,
                expires_at
            FROM public.faction_area_experience_control
            WHERE realm_id = @realmId
              AND map_id = @mapId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("mapId", mapId);
        command.Parameters.AddWithValue("realmId", _realmId.Value);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadControl(reader)
            : null;
    }

    private async Task<WorldBossAreaControlSnapshot?> UpsertControlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldBossAreaActivation activation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            UpsertControlQuery,
            connection,
            transaction);
        command.Parameters.AddWithValue("mapId", activation.MapId);
        command.Parameters.AddWithValue("realmId", _realmId.Value);
        command.Parameters.AddWithValue(
            "bossTemplateKey",
            activation.BossTemplateKey);
        command.Parameters.AddWithValue(
            "controllingCamp",
            (short)activation.ControllingCamp);
        command.Parameters.Add(
            "killedAt",
            NpgsqlDbType.TimestampTz).Value =
            activation.KilledAtUtc.UtcDateTime;
        command.Parameters.AddWithValue(
            "deathToken",
            activation.DeathToken);
        PostgresGameplayContentBinding.AddParameter(
            command,
            _gameplayContentRevision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadControl(reader)
            : null;
    }

    private static WorldBossAreaControlSnapshot ReadControl(
        NpgsqlDataReader reader)
    {
        var control = new WorldBossAreaControlSnapshot(
            reader.GetInt16(0),
            checked((byte)reader.GetInt16(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            AsUtc(reader.GetDateTime(5)),
            AsUtc(reader.GetDateTime(6)));
        WorldBossPersistenceContract.Validate(control);
        return control;
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset CanonicalizeUtc(DateTimeOffset value)
    {
        var ticks = value.UtcDateTime.Ticks;
        return new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
    }

    private sealed record ConfiguredArea(
        int BonusBasisPoints,
        int RespawnIntervalSeconds);

    private const string UpsertControlQuery =
        """
        INSERT INTO public.faction_area_experience_control (
            realm_id,
            map_id,
            controlling_camp,
            boss_template_key,
            bonus_basis_points,
            activated_at,
            expires_at,
            death_token
        )
        SELECT
            @realmId,
            area.map_id,
            @controllingCamp,
            area.template_key,
            area.bonus_basis_points,
            @killedAt,
            @killedAt +
                (area.respawn_interval_seconds * interval '1 second'),
            @deathToken
        FROM public.gameplay_world_boss_definitions area
        WHERE area.map_id = @mapId
          AND area.template_key = @bossTemplateKey
          AND area.revision = COALESCE(
              @gameplayContentRevision,
              (
                  SELECT publication.revision
                  FROM public.gameplay_content_publication publication
                  WHERE publication.family = 'gameplay'
              )
          )
        ON CONFLICT (realm_id, map_id) DO UPDATE
        SET controlling_camp = EXCLUDED.controlling_camp,
            boss_template_key = EXCLUDED.boss_template_key,
            bonus_basis_points = EXCLUDED.bonus_basis_points,
            activated_at = EXCLUDED.activated_at,
            expires_at = EXCLUDED.expires_at,
            death_token = EXCLUDED.death_token
        WHERE faction_area_experience_control.death_token <>
                  EXCLUDED.death_token
          AND faction_area_experience_control.activated_at <
                  EXCLUDED.activated_at
        RETURNING
            map_id,
            controlling_camp,
            boss_template_key,
            death_token,
            bonus_basis_points,
            activated_at,
            expires_at;
        """;

    private const string ActiveRespawnQuery =
        """
        SELECT
            control.map_id,
            control.boss_template_key,
            control.expires_at
        FROM public.faction_area_experience_control control
        JOIN public.gameplay_world_boss_definitions area
          ON area.map_id = control.map_id
         AND area.template_key = control.boss_template_key
         AND area.revision = COALESCE(
             @gameplayContentRevision,
             (
                 SELECT publication.revision
                 FROM public.gameplay_content_publication publication
                 WHERE publication.family = 'gameplay'
             )
         )
        WHERE control.map_id = @mapId
          AND control.realm_id = @realmId
          AND control.activated_at <= @readAt
          AND control.expires_at > @readAt;
        """;
}
