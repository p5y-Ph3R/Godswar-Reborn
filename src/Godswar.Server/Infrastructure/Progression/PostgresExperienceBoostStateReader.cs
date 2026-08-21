using System.Collections.Immutable;
using System.Data;
using Godswar.Server.Application.Progression;
using Godswar.Server.Infrastructure.Database;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed class PostgresExperienceBoostStateReader :
    IExperienceBoostStateReader
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string? _gameplayContentRevision;

    public PostgresExperienceBoostStateReader(
        NpgsqlDataSource dataSource,
        string? gameplayContentRevision = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _gameplayContentRevision =
            PostgresGameplayContentBinding.ValidateOptional(
                gameplayContentRevision);
    }

    public async Task<ExperienceBoostSnapshot> ReadAsync(
        ExperienceBoostReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ExperienceBoostContract.ValidateRequest(request);
        request = request with
        {
            ReadAtUtc = CanonicalizeUtc(request.ReadAtUtc)
        };
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
        await SetReadOnlyAsync(
            connection,
            transaction,
            cancellationToken);

        var boosts = await ReadPersonalAndAreaBoostsAsync(
            connection,
            transaction,
            request,
            cancellationToken);
        await AppendVipBoostAsync(
            connection,
            transaction,
            request,
            boosts,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var snapshot = new ExperienceBoostSnapshot(
            boosts
                .OrderBy(static boost => boost.Kind)
                .ToImmutableArray());
        ExperienceBoostContract.ValidateSnapshot(
            snapshot,
            request.ReadAtUtc);
        return snapshot;
    }

    private async Task<List<ExperienceBoostEntry>>
        ReadPersonalAndAreaBoostsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            ExperienceBoostReadRequest request,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            PersonalAndAreaBoostsQuery,
            connection,
            transaction);
        AddReadParameters(command, request);
        PostgresGameplayContentBinding.AddParameter(
            command,
            _gameplayContentRevision);
        var boosts = new List<ExperienceBoostEntry>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (boosts.Count >=
                ExperienceBoostContract.MaximumActiveBoosts)
            {
                throw new InvalidDataException(
                    "The PostgreSQL experience-boost projection exceeded its row bound.");
            }

            var expiresAt = reader.IsDBNull(4)
                ? (DateTimeOffset?)null
                : AddTicksChecked(
                    request.ReadAtUtc,
                    reader.GetInt64(4));
            boosts.Add(new ExperienceBoostEntry(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                expiresAt,
                reader.GetString(5)));
        }

        return boosts;
    }

    private static async Task AppendVipBoostAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExperienceBoostReadRequest request,
        List<ExperienceBoostEntry> boosts,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            VipBoostQuery,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "accountId",
            request.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            request.CharacterId);
        command.Parameters.Add(
            "readAt",
            NpgsqlDbType.TimestampTz).Value =
            request.ReadAtUtc.UtcDateTime;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        if (boosts.Count >=
            ExperienceBoostContract.MaximumActiveBoosts)
        {
            throw new InvalidDataException(
                "The PostgreSQL experience-boost projection exceeded its row bound.");
        }

        var tier = (VipTier)reader.GetInt16(0);
        var expiresAt = reader.IsDBNull(1)
            ? (DateTimeOffset?)null
            : AsUtc(reader.GetDateTime(1));
        boosts.Add(new ExperienceBoostEntry(
            VipExperienceBoosts.StatusId(tier),
            ExperienceBoostKinds.Vip,
            VipExperienceBoosts.BonusBasisPoints(tier),
            (int)tier,
            expiresAt,
            $"vip:{tier.ToString().ToLowerInvariant()}"));
    }

    private static void AddReadParameters(
        NpgsqlCommand command,
        ExperienceBoostReadRequest request)
    {
        command.Parameters.AddWithValue("accountId", request.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            request.CharacterId);
        command.Parameters.AddWithValue("camp", (short)request.Camp);
        command.Parameters.AddWithValue("mapId", request.MapId);
        command.Parameters.Add(
            "readAt",
            NpgsqlDbType.TimestampTz).Value =
            request.ReadAtUtc.UtcDateTime;
    }

    private static async Task SetReadOnlyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY;",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset AddTicksChecked(
        DateTimeOffset readAtUtc,
        long remainingTicks)
    {
        if (remainingTicks <= 0)
        {
            throw new InvalidDataException(
                "An active experience boost has no remaining duration.");
        }

        try
        {
            return readAtUtc.AddTicks(remainingTicks);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException(
                "An experience-boost duration exceeds the timestamp range.",
                ex);
        }
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset CanonicalizeUtc(DateTimeOffset value)
    {
        var ticks = value.UtcDateTime.Ticks;
        return new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
    }

    private const string PersonalAndAreaBoostsQuery =
        """
        WITH requested_character AS (
            SELECT character.id, character.server_id
            FROM public.character_base character
            WHERE character.account_id = @accountId
              AND character.id = @characterId
              AND character.lifecycle_state = 'active'
        ),
        personal AS (
            SELECT DISTINCT ON (modifier.kind)
                modifier.status_id,
                modifier.kind,
                modifier.bonus_basis_points,
                modifier.priority,
                COALESCE(
                    modifier.remaining_online_ticks,
                    CASE
                        WHEN modifier.expires_at IS NULL THEN NULL
                        ELSE GREATEST(
                            0,
                            ROUND(EXTRACT(EPOCH FROM (
                                modifier.expires_at -
                                modifier.activated_at
                            )) * 10000000)::bigint
                        )
                    END
                ) AS remaining_online_ticks,
                modifier.source
            FROM public.character_experience_modifiers modifier
            JOIN requested_character character
              ON character.id = modifier.character_id
            WHERE modifier.activated_at <= @readAt
              AND (
                  modifier.expires_at IS NULL
                  AND modifier.remaining_online_ticks IS NULL
                  OR COALESCE(
                      modifier.remaining_online_ticks,
                      GREATEST(
                          0,
                          ROUND(EXTRACT(EPOCH FROM (
                              modifier.expires_at -
                              modifier.activated_at
                          )) * 10000000)::bigint
                      )
                  ) > 0
              )
            ORDER BY
                modifier.kind,
                modifier.priority DESC,
                modifier.bonus_basis_points DESC,
                modifier.status_id
        )
        SELECT
            status_id,
            kind,
            bonus_basis_points,
            priority,
            remaining_online_ticks,
            source
        FROM personal
        UNION ALL
        SELECT
            1504,
            1009,
            control.bonus_basis_points,
            1,
            ROUND(EXTRACT(EPOCH FROM (
                control.expires_at - @readAt
            )) * 10000000)::bigint,
            'world-boss:' || control.boss_template_key
        FROM requested_character character
        CROSS JOIN public.faction_area_experience_control control
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
          AND control.realm_id = character.server_id
          AND control.controlling_camp = @camp
          AND control.activated_at <= @readAt
          AND control.expires_at > @readAt
        ORDER BY kind
        LIMIT 67;
        """;

    private const string VipBoostQuery =
        """
        SELECT account.vip_tier, account.vip_expires_at
        FROM public.accounts account
        JOIN public.character_base character
          ON character.account_id = account.id
         AND character.id = @characterId
         AND character.lifecycle_state = 'active'
        WHERE account.id = @accountId
          AND account.vip_tier BETWEEN 1 AND 4
          AND (
              account.vip_expires_at IS NULL
              OR account.vip_expires_at > @readAt
          );
        """;
}
