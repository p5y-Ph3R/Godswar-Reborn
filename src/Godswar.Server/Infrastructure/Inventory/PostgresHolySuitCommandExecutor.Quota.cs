using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor
{
    public async Task<HolySuitStoreQuotaSnapshot> ReadStoreQuotaAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        if (subject.AccountId <= 0 ||
            subject.CharacterId <= 0 ||
            !ownership.IsValid)
        {
            throw new ArgumentException(
                "A Holy Suit quota read requires a valid owned character.");
        }

        var policy = _itemContent.HolySuit.OperationPolicy ??
            throw new InvalidOperationException(
                "The pinned Holy Suit operation policy is unavailable.");
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                cb.fighter_job_lv,
                realm_clock.usage_day,
                COALESCE(usage.stored_exp, 0),
                EXISTS (
                    SELECT 1
                    FROM public.account_entitlements entitlement
                    WHERE entitlement.account_id = cb.account_id
                      AND entitlement.entitlement_key = @entitlementKey
                      AND entitlement.starts_at <= now()
                      AND entitlement.revoked_at IS NULL
                      AND (entitlement.expires_at IS NULL OR
                           entitlement.expires_at > now())
                )
            FROM public.character_base cb
            CROSS JOIN LATERAL (
                SELECT
                    (CURRENT_TIMESTAMP AT TIME ZONE
                        @realmDayTimeZone)::date AS usage_day
            ) realm_clock
            LEFT JOIN public.holy_suit_daily_exp_storage usage
              ON usage.account_id = cb.account_id
             AND usage.realm_id = cb.server_id
             AND usage.usage_day = realm_clock.usage_day
            WHERE cb.account_id = @accountId
              AND cb.id = @characterId
              AND cb.lifecycle_state = 'active'
              AND cb.checkpoint_owner_id = @ownerId
              AND cb.checkpoint_owner_generation = @ownerGeneration
            LIMIT 1;
            """,
            connection)
        {
            CommandTimeout = _commandTimeoutSeconds
        };
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue("characterId", subject.CharacterId);
        command.Parameters.AddWithValue("ownerId", ownership.OwnerId);
        command.Parameters.AddWithValue(
            "ownerGeneration",
            ownership.Generation);
        command.Parameters.AddWithValue(
            "realmDayTimeZone",
            policy.RealmDayTimeZone);
        command.Parameters.AddWithValue(
            "entitlementKey",
            policy.DailyQuotaBypassEntitlement);

        (int Level, DateOnly Day, long Used, bool Exempt)? row = null;
        await using (var reader =
            await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                row = (
                    reader.GetInt32(0),
                    reader.GetFieldValue<DateOnly>(1),
                    reader.GetInt64(2),
                    reader.GetBoolean(3));
            }
        }

        (await _ownershipGuard.ValidateCurrentAsync(
            subject,
            ownership,
            cancellationToken)).RequireCurrent();
        if (!row.HasValue)
        {
            throw new InvalidDataException(
                "The current Holy Suit quota row was not readable.");
        }

        var dailyCredit = policy.ResolveDailyExperienceLimit(row.Value.Level);
        return new HolySuitStoreQuotaSnapshot(
            subject.CharacterId,
            row.Value.Level,
            row.Value.Day,
            row.Value.Used,
            dailyCredit,
            row.Value.Exempt);
    }
}
