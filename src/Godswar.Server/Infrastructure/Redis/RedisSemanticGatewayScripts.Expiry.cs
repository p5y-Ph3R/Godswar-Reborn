namespace Godswar.Server.Infrastructure.Redis;

internal static partial class RedisSemanticGatewayScripts
{
    public const string SweepExpired =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)

        local function decrement(field)
            local value = redis.call('HINCRBY', KEYS[2], field, -1)
            if value < 0 then redis.call('HSET', KEYS[2], field, '0') end
        end

        local function removeAdmission(accountKey, admissionKey)
            local state = redis.call('HGET', admissionKey, 'state')
            if not state then
                redis.call('DEL', admissionKey)
                redis.call(
                    'HDEL', accountKey,
                    'admissionKey', 'admission', 'admissionExpires')
                return 0
            end
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
            redis.call(
                'HDEL', accountKey,
                'admissionKey', 'admission', 'admissionExpires')
            return 1
        end

        local members = redis.call(
            'ZRANGEBYSCORE',
            KEYS[1],
            '-inf',
            now,
            'LIMIT',
            0,
            ARGV[1])
        local processed = 0
        local expiredAdmissions = 0
        local expiredGenerations = 0
        for _, member in ipairs(members) do
            processed = processed + 1
            local kind = string.sub(member, 1, 1)
            local accountKey = string.sub(member, 3)
            if kind == 'a' then
                local admissionKey =
                    redis.call('HGET', accountKey, 'admissionKey')
                if not admissionKey then
                    redis.call('ZREM', KEYS[1], member)
                else
                    local expires =
                        tonumber(redis.call(
                            'HGET', admissionKey, 'expires') or '0')
                    if expires <= now then
                        expiredAdmissions =
                            expiredAdmissions +
                            removeAdmission(accountKey, admissionKey)
                        redis.call('ZREM', KEYS[1], member)
                    else
                        redis.call(
                            'ZADD', KEYS[1], expires, member)
                    end
                end
            elseif kind == 'g' then
                local generation =
                    redis.call('HGET', accountKey, 'generation')
                if not generation then
                    redis.call('ZREM', KEYS[1], member)
                else
                    local expires =
                        tonumber(redis.call(
                            'HGET',
                            accountKey,
                            'generationExpires') or '0')
                    if expires <= now then
                        local admissionKey =
                            redis.call(
                                'HGET', accountKey, 'admissionKey')
                        if admissionKey then
                            expiredAdmissions =
                                expiredAdmissions +
                                removeAdmission(accountKey, admissionKey)
                        end
                        local nameKey =
                            redis.call('HGET', accountKey, 'nameKey')
                        local connectionKey =
                            redis.call(
                                'HGET',
                                accountKey,
                                'loginConnectionKey')
                        if nameKey and
                           redis.call(
                               'HGET', nameKey, 'generation') == generation then
                            redis.call('DEL', nameKey)
                        end
                        if connectionKey and
                           redis.call(
                               'HGET',
                               connectionKey,
                               'generation') == generation then
                            redis.call('DEL', connectionKey)
                        end
                        redis.call('DEL', accountKey)
                        decrement('generations')
                        expiredGenerations = expiredGenerations + 1
                        redis.call('ZREM', KEYS[1], member)
                        redis.call(
                            'ZREM', KEYS[1], 'a|' .. accountKey)
                    else
                        redis.call(
                            'ZADD', KEYS[1], expires, member)
                    end
                end
            else
                redis.call('ZREM', KEYS[1], member)
            end
        end
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        redis.call('PEXPIRE', KEYS[2], ARGV[2])
        return {
            processed,
            expiredAdmissions,
            expiredGenerations,
            now
        }
        """;
}
