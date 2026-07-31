namespace Godswar.Server.Infrastructure.Redis;

internal static class RedisGameTicketScripts
{
    public const string BeginGeneration =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local expiresAt = now + tonumber(ARGV[7])
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
        redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', now)

        local existed = redis.call('ZSCORE', KEYS[2], KEYS[1]) ~= false
        if not existed and redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[5]) then
            return {
                0,
                redis.call('ZCARD', KEYS[2]),
                redis.call('ZCARD', KEYS[3])
            }
        end

        local oldTicket = redis.call('HGET', KEYS[1], 'ticket_key')
        if oldTicket then
            redis.call('DEL', oldTicket)
            redis.call('ZREM', KEYS[3], oldTicket)
        end
        local oldGrant = redis.call('HGET', KEYS[1], 'grant_key')
        if oldGrant then
            redis.call('DEL', oldGrant)
        end

        redis.call(
            'HSET', KEYS[1],
            'v', '1',
            'authority', ARGV[1],
            'generation', ARGV[2],
            'account', ARGV[3],
            'username', ARGV[4])
        redis.call(
            'HDEL', KEYS[1],
            'ticket_key', 'grant_key', 'grant')
        redis.call('PEXPIRE', KEYS[1], ARGV[6])
        redis.call('ZADD', KEYS[2], expiresAt, KEYS[1])

        return {
            1,
            redis.call('ZCARD', KEYS[2]),
            redis.call('ZCARD', KEYS[3])
        }
        """;

    public const string Issue =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local expiresAt = now + tonumber(ARGV[18])
        redis.call('ZREMRANGEBYSCORE', KEYS[4], '-inf', now)
        redis.call('ZREMRANGEBYSCORE', KEYS[5], '-inf', now)

        if redis.call('EXISTS', KEYS[2]) == 1 or
           redis.call('EXISTS', KEYS[3]) == 1 then
            return {
                -2,
                redis.call('ZCARD', KEYS[4]),
                redis.call('ZCARD', KEYS[5]),
                0
            }
        end
        if redis.call('HGET', KEYS[1], 'authority') ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'generation') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'account') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'username') ~= ARGV[4] or
           redis.call('ZSCORE', KEYS[4], KEYS[1]) == false then
            return {
                0,
                redis.call('ZCARD', KEYS[4]),
                redis.call('ZCARD', KEYS[5]),
                0
            }
        end

        local oldTicket = redis.call('HGET', KEYS[1], 'ticket_key')
        local oldGrant = redis.call('HGET', KEYS[1], 'grant_key')
        local oldOutstanding = false
        if oldTicket then
            oldOutstanding =
                redis.call('EXISTS', oldTicket) == 1 and
                redis.call('ZSCORE', KEYS[5], oldTicket) ~= false
            if not oldOutstanding then
                redis.call('DEL', oldTicket)
                redis.call('ZREM', KEYS[5], oldTicket)
                if oldGrant then
                    redis.call('DEL', oldGrant)
                end
                redis.call(
                    'HDEL', KEYS[1],
                    'ticket_key', 'grant_key', 'grant')
                oldTicket = false
                oldGrant = false
            end
        end
        local outstanding = redis.call('ZCARD', KEYS[5])
        if not oldOutstanding and outstanding >= tonumber(ARGV[20]) then
            return {
                -1,
                redis.call('ZCARD', KEYS[4]),
                outstanding,
                0
            }
        end
        if oldTicket then
            redis.call('DEL', oldTicket)
            redis.call('ZREM', KEYS[5], oldTicket)
        end
        if oldGrant then
            redis.call('DEL', oldGrant)
        end

        redis.call(
            'HSET', KEYS[2],
            'v', '1',
            'account_key', KEYS[1],
            'grant_key', KEYS[3],
            'authority', ARGV[1],
            'generation', ARGV[2],
            'account', ARGV[3],
            'username', ARGV[4],
            'grant', ARGV[5],
            'ticket_hash', ARGV[6],
            'committed', '0',
            'protocol_major', ARGV[7],
            'protocol_minor', ARGV[8],
            'client_instance', ARGV[9],
            'origin', ARGV[10],
            'route_host', ARGV[11],
            'tls_host', ARGV[12],
            'audience', ARGV[13],
            'route_port', ARGV[14],
            'tls_port', ARGV[15],
            'server', ARGV[16],
            'permissions', ARGV[17],
            'expires_at', expiresAt)
        redis.call('PEXPIRE', KEYS[2], ARGV[19])
        redis.call(
            'HSET', KEYS[1],
            'ticket_key', KEYS[2],
            'grant_key', KEYS[3],
            'grant', ARGV[5])
        redis.call('PEXPIRE', KEYS[1], ARGV[19])
        redis.call(
            'HSET', KEYS[3],
            'v', '1',
            'account_key', KEYS[1],
            'ticket_key', KEYS[2],
            'authority', ARGV[1],
            'generation', ARGV[2],
            'grant', ARGV[5])
        redis.call('PEXPIRE', KEYS[3], ARGV[19])
        redis.call('ZADD', KEYS[4], expiresAt, KEYS[1])
        redis.call('ZADD', KEYS[5], expiresAt, KEYS[2])

        return {
            1,
            redis.call('ZCARD', KEYS[4]),
            redis.call('ZCARD', KEYS[5]),
            expiresAt
        }
        """;

    public const string Activate =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local accountKey = redis.call('HGET', KEYS[1], 'account_key')
        local ticketKey = redis.call('HGET', KEYS[1], 'ticket_key')
        if not accountKey or not ticketKey or
           redis.call('HGET', KEYS[1], 'authority') ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'generation') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'grant') ~= ARGV[3] or
           redis.call('HGET', accountKey, 'authority') ~= ARGV[1] or
           redis.call('HGET', accountKey, 'generation') ~= ARGV[2] or
           redis.call('HGET', accountKey, 'ticket_key') ~= ticketKey or
           redis.call('HGET', accountKey, 'grant_key') ~= KEYS[1] or
           redis.call('HGET', ticketKey, 'authority') ~= ARGV[1] or
           redis.call('HGET', ticketKey, 'generation') ~= ARGV[2] or
           redis.call('HGET', ticketKey, 'grant') ~= ARGV[3] then
            return 0
        end
        if tonumber(redis.call('HGET', ticketKey, 'expires_at')) <=
                now then
            return 0
        end

        redis.call('HSET', ticketKey, 'committed', '1')
        return 1
        """;

    public const string Consume =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
        redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', now)

        if redis.call('EXISTS', KEYS[1]) == 0 then
            return {
                2, 0, '', 0, '',
                redis.call('ZCARD', KEYS[2]),
                redis.call('ZCARD', KEYS[3])
            }
        end

        local accountKey = redis.call('HGET', KEYS[1], 'account_key')
        local ticketKey = redis.call('HGET', KEYS[1], 'ticket_key')
        if not accountKey or not ticketKey or
           redis.call('EXISTS', ticketKey) == 0 then
            redis.call('DEL', KEYS[1])
            return {
                2, 0, '', 0, '',
                redis.call('ZCARD', KEYS[2]),
                redis.call('ZCARD', KEYS[3])
            }
        end

        local authority = redis.call('HGET', ticketKey, 'authority')
        local generation = redis.call('HGET', ticketKey, 'generation')
        local account = redis.call('HGET', ticketKey, 'account')
        local username = redis.call('HGET', ticketKey, 'username')
        local permissions = redis.call('HGET', ticketKey, 'permissions')
        local expiresAt = tonumber(
            redis.call('HGET', ticketKey, 'expires_at') or '0')
        local generationCurrent =
            redis.call('HGET', accountKey, 'authority') == authority and
            redis.call('HGET', accountKey, 'generation') == generation and
            redis.call('HGET', accountKey, 'ticket_key') == ticketKey and
            redis.call('HGET', accountKey, 'grant_key') == KEYS[1] and
            redis.call('ZSCORE', KEYS[2], accountKey) ~= false

        local ticketMatches =
            redis.call('HGET', ticketKey, 'ticket_hash') == ARGV[1] and
            redis.call('HGET', ticketKey, 'grant') == ARGV[2] and
            redis.call('HGET', KEYS[1], 'grant') == ARGV[2]
        local scopeMatches =
            redis.call('HGET', ticketKey, 'protocol_major') == ARGV[3] and
            redis.call('HGET', ticketKey, 'protocol_minor') == ARGV[4] and
            redis.call('HGET', ticketKey, 'client_instance') == ARGV[5] and
            redis.call('HGET', ticketKey, 'origin') == ARGV[6] and
            redis.call('HGET', ticketKey, 'route_host') == ARGV[7] and
            redis.call('HGET', ticketKey, 'tls_host') == ARGV[8] and
            redis.call('HGET', ticketKey, 'audience') == ARGV[9] and
            redis.call('HGET', ticketKey, 'route_port') == ARGV[10] and
            redis.call('HGET', ticketKey, 'tls_port') == ARGV[11] and
            redis.call('HGET', ticketKey, 'server') == ARGV[12] and
            redis.call('HGET', ticketKey, 'permissions') == ARGV[13]
        local expired = expiresAt <= now
        local committed =
            redis.call('HGET', ticketKey, 'committed') == '1'

        if not committed and not expired and ticketMatches and
           scopeMatches and generationCurrent then
            return {
                5, 0, '', 0, '',
                redis.call('ZCARD', KEYS[2]),
                redis.call('ZCARD', KEYS[3])
            }
        end

        redis.call('DEL', ticketKey)
        redis.call('DEL', KEYS[1])
        redis.call('ZREM', KEYS[3], ticketKey)
        if generationCurrent then
            redis.call('DEL', accountKey)
            redis.call('ZREM', KEYS[2], accountKey)
        end

        local status = 2
        if expired then
            status = 3
        elseif ticketMatches and not scopeMatches then
            status = 4
        elseif committed and ticketMatches and scopeMatches and
                generationCurrent then
            status = 1
        end

        if status == 1 then
            return {
                status, account, username, permissions, generation,
                redis.call('ZCARD', KEYS[2]),
                redis.call('ZCARD', KEYS[3])
            }
        end
        return {
            status, 0, '', 0, '',
            redis.call('ZCARD', KEYS[2]),
            redis.call('ZCARD', KEYS[3])
        }
        """;

    public const string RevokeGeneration =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
        redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', now)

        if redis.call('HGET', KEYS[1], 'authority') == ARGV[1] and
           redis.call('HGET', KEYS[1], 'generation') == ARGV[2] then
            local ticket = redis.call('HGET', KEYS[1], 'ticket_key')
            if ticket then
                redis.call('DEL', ticket)
                redis.call('ZREM', KEYS[3], ticket)
            end
            local grant = redis.call('HGET', KEYS[1], 'grant_key')
            if grant then
                redis.call('DEL', grant)
            end
            redis.call('DEL', KEYS[1])
            redis.call('ZREM', KEYS[2], KEYS[1])
        end
        return {
            redis.call('ZCARD', KEYS[2]),
            redis.call('ZCARD', KEYS[3])
        }
        """;

    public const string RevokeGrant =
        """
        local accountKey = redis.call('HGET', KEYS[1], 'account_key')
        local ticketKey = redis.call('HGET', KEYS[1], 'ticket_key')
        if accountKey and ticketKey and
           redis.call('HGET', KEYS[1], 'authority') == ARGV[1] and
           redis.call('HGET', KEYS[1], 'generation') == ARGV[2] and
           redis.call('HGET', KEYS[1], 'grant') == ARGV[3] and
           redis.call('HGET', accountKey, 'authority') == ARGV[1] and
           redis.call('HGET', accountKey, 'generation') == ARGV[2] and
           redis.call('HGET', accountKey, 'grant_key') == KEYS[1] and
           redis.call('HGET', accountKey, 'ticket_key') == ticketKey and
           redis.call('HGET', ticketKey, 'authority') == ARGV[1] and
           redis.call('HGET', ticketKey, 'generation') == ARGV[2] and
           redis.call('HGET', ticketKey, 'grant') == ARGV[3] then
            redis.call('DEL', ticketKey)
            redis.call('ZREM', KEYS[2], ticketKey)
            redis.call(
                'HDEL', accountKey,
                'ticket_key', 'grant_key', 'grant')
            redis.call('DEL', KEYS[1])
        end
        return redis.call('ZCARD', KEYS[2])
        """;
}
