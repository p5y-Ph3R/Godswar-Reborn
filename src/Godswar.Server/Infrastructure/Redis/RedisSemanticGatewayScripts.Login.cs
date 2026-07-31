namespace Godswar.Server.Infrastructure.Redis;

internal static partial class RedisSemanticGatewayScripts
{
    public const string StartLogin =
        """
        local function decrement(field)
            local value = redis.call('HINCRBY', KEYS[4], field, -1)
            if value < 0 then
                redis.call('HSET', KEYS[4], field, '0')
            end
        end

        local function removeAdmission(accountKey)
            local admissionKey =
                redis.call('HGET', accountKey, 'admissionKey')
            if not admissionKey then
                return 0
            end
            local state = redis.call('HGET', admissionKey, 'state')
            if state then
                decrement('admissions')
                if state == '1' then
                    decrement('reserved')
                else
                    decrement('committed')
                end
                local routeField =
                    redis.call('HGET', admissionKey, 'routeField')
                local workerField =
                    redis.call('HGET', admissionKey, 'workerField')
                if routeField then decrement(routeField) end
                if workerField then decrement(workerField) end
                local connectionKey =
                    redis.call('HGET', admissionKey, 'connectionKey')
                if connectionKey then redis.call('DEL', connectionKey) end
                redis.call('DEL', admissionKey)
            end
            redis.call('ZREM', KEYS[5], 'a|' .. accountKey)
            redis.call(
                'HDEL', accountKey,
                'admissionKey', 'admission', 'admissionExpires')
            return state and 1 or 0
        end

        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local expires = now + tonumber(ARGV[6])
        local nameAccount = redis.call('HGET', KEYS[2], 'account')
        if nameAccount and nameAccount ~= ARGV[2] then
            local nameExpires =
                tonumber(redis.call(
                    'HGET', KEYS[2], 'generationExpires') or '0')
            if nameExpires > now then
                return {20, '', 0, 0, 0}
            end
            redis.call('DEL', KEYS[2])
        end

        local previousGeneration =
            redis.call('HGET', KEYS[1], 'generation')
        local current = previousGeneration ~= false
        if current then
            local generationExpires =
                tonumber(redis.call(
                    'HGET', KEYS[1], 'generationExpires') or '0')
            if generationExpires <= now then
                removeAdmission(KEYS[1])
                local oldNameKey =
                    redis.call('HGET', KEYS[1], 'nameKey')
                local oldConnectionKey =
                    redis.call('HGET', KEYS[1], 'loginConnectionKey')
                if oldNameKey then redis.call('DEL', oldNameKey) end
                if oldConnectionKey then
                    redis.call('DEL', oldConnectionKey)
                end
                decrement('generations')
                redis.call('DEL', KEYS[1])
                redis.call('ZREM', KEYS[5], 'g|' .. KEYS[1])
                current = false
                previousGeneration = false
            elseif redis.call(
                    'HGET', KEYS[1], 'username') ~= ARGV[3] then
                return {20, '', 0, 0, 0}
            end
        end

        local connectionGeneration =
            redis.call('HGET', KEYS[3], 'generation')
        if connectionGeneration then
            local connectionExpires =
                tonumber(redis.call(
                    'HGET', KEYS[3], 'expires') or '0')
            local samePrevious =
                current and
                connectionGeneration == previousGeneration and
                redis.call('HGET', KEYS[3], 'account') == ARGV[2] and
                redis.call('HGET', KEYS[3], 'kind') == 'login'
            if connectionExpires > now and not samePrevious then
                return {21, '', 0, 0, 0}
            end
            if connectionExpires <= now then
                redis.call('DEL', KEYS[3])
            end
        end

        local generations =
            tonumber(redis.call('HGET', KEYS[4], 'generations') or '0')
        if not current and generations >= tonumber(ARGV[8]) then
            return {22, '', 0, 0, 0}
        end

        local invalidated = 0
        if current then
            invalidated = removeAdmission(KEYS[1])
            local oldConnectionKey =
                redis.call('HGET', KEYS[1], 'loginConnectionKey')
            if oldConnectionKey and oldConnectionKey ~= KEYS[3] then
                redis.call('DEL', oldConnectionKey)
            end
        else
            redis.call('HINCRBY', KEYS[4], 'generations', 1)
        end

        local sequence =
            redis.call('HINCRBY', KEYS[4], 'login-sequence', 1)
        redis.call(
            'HSET', KEYS[1],
            'v', '1',
            'generation', ARGV[1],
            'sequence', tostring(sequence),
            'account', ARGV[2],
            'username', ARGV[3],
            'nameKey', KEYS[2],
            'loginConnection', ARGV[4],
            'loginAddress', ARGV[5],
            'loginConnectionKey', KEYS[3],
            'state', '1',
            'generationExpires', expires,
            'admissionsIssued', '0')
        redis.call('PEXPIRE', KEYS[1], ARGV[7])
        redis.call(
            'HSET', KEYS[2],
            'v', '1',
            'generation', ARGV[1],
            'sequence', tostring(sequence),
            'account', ARGV[2],
            'username', ARGV[3],
            'accountKey', KEYS[1],
            'loginConnection', ARGV[4],
            'loginAddress', ARGV[5],
            'state', '1',
            'generationExpires', expires)
        redis.call('PEXPIRE', KEYS[2], ARGV[7])
        redis.call(
            'HSET', KEYS[3],
            'v', '1',
            'kind', 'login',
            'account', ARGV[2],
            'generation', ARGV[1],
            'address', ARGV[5],
            'expires', expires)
        redis.call('PEXPIRE', KEYS[3], ARGV[7])
        redis.call(
            'ZADD', KEYS[5], expires, 'g|' .. KEYS[1])
        redis.call('PEXPIRE', KEYS[4], ARGV[7])
        redis.call('PEXPIRE', KEYS[5], ARGV[7])
        return {1, ARGV[1], sequence, invalidated, expires}
        """;

    public const string ActivateLogin =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local generation = redis.call('HGET', KEYS[1], 'generation')
        if not generation or
           generation ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'sequence') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'account') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'username') ~= ARGV[4] or
           redis.call('HGET', KEYS[1], 'loginConnection') ~= ARGV[5] or
           redis.call('HGET', KEYS[1], 'loginAddress') ~= ARGV[6] then
            return 0
        end
        local expires =
            tonumber(redis.call(
                'HGET', KEYS[1], 'generationExpires') or '0')
        if expires <= now or
           redis.call('HGET', KEYS[1], 'state') ~= '1' then
            return 0
        end
        if redis.call('HGET', KEYS[2], 'generation') ~= ARGV[1] or
           redis.call('HGET', KEYS[2], 'account') ~= ARGV[3] then
            return 0
        end
        redis.call('HSET', KEYS[1], 'state', '2')
        redis.call('HSET', KEYS[2], 'state', '2')
        return 1
        """;

    public const string FindActivatedLogin =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local generation = redis.call('HGET', KEYS[1], 'generation')
        if not generation then
            return {20, '', 0, 0, '', '', '', 0}
        end
        local expires =
            tonumber(redis.call(
                'HGET', KEYS[1], 'generationExpires') or '0')
        if expires <= now then
            return {21, '', 0, 0, '', '', '', 0}
        end
        local address = redis.call('HGET', KEYS[1], 'loginAddress')
        if address ~= ARGV[1] then
            return {22, '', 0, 0, '', '', '', 0}
        end
        if redis.call('HGET', KEYS[1], 'state') ~= '2' then
            return {23, '', 0, 0, '', '', '', 0}
        end
        return {
            1,
            generation,
            redis.call('HGET', KEYS[1], 'sequence'),
            redis.call('HGET', KEYS[1], 'account'),
            redis.call('HGET', KEYS[1], 'username'),
            redis.call('HGET', KEYS[1], 'loginConnection'),
            address,
            expires
        }
        """;

    public const string CancelLogin =
        """
        local function decrement(field)
            local value = redis.call('HINCRBY', KEYS[4], field, -1)
            if value < 0 then
                redis.call('HSET', KEYS[4], field, '0')
            end
        end

        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local generation = redis.call('HGET', KEYS[1], 'generation')
        if not generation or
           generation ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'sequence') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'account') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'username') ~= ARGV[4] or
           redis.call('HGET', KEYS[1], 'loginConnection') ~= ARGV[5] or
           redis.call('HGET', KEYS[1], 'loginAddress') ~= ARGV[6] then
            return {0, 0}
        end
        local expires =
            tonumber(redis.call(
                'HGET', KEYS[1], 'generationExpires') or '0')
        if expires <= now then
            return {0, 0}
        end

        local invalidated = 0
        local admissionKey =
            redis.call('HGET', KEYS[1], 'admissionKey')
        if admissionKey then
            local state = redis.call('HGET', admissionKey, 'state')
            if state then
                invalidated = 1
                decrement('admissions')
                if state == '1' then
                    decrement('reserved')
                else
                    decrement('committed')
                end
                local routeField =
                    redis.call('HGET', admissionKey, 'routeField')
                local workerField =
                    redis.call('HGET', admissionKey, 'workerField')
                if routeField then decrement(routeField) end
                if workerField then decrement(workerField) end
                local connectionKey =
                    redis.call('HGET', admissionKey, 'connectionKey')
                if connectionKey then redis.call('DEL', connectionKey) end
                redis.call('DEL', admissionKey)
            end
        end
        decrement('generations')
        if redis.call('HGET', KEYS[2], 'generation') == ARGV[1] then
            redis.call('DEL', KEYS[2])
        end
        if redis.call('HGET', KEYS[3], 'generation') == ARGV[1] then
            redis.call('DEL', KEYS[3])
        end
        redis.call('DEL', KEYS[1])
        redis.call('ZREM', KEYS[5], 'g|' .. KEYS[1])
        redis.call('ZREM', KEYS[5], 'a|' .. KEYS[1])
        return {1, invalidated}
        """;
}
