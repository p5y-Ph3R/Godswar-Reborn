namespace Godswar.Server.Infrastructure.Redis;

internal static partial class RedisSemanticGatewayScripts
{
    public const string ReserveAdmission =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local expires = now + tonumber(ARGV[12])

        local function validateRoute()
            local routeNode = redis.call('HGET', KEYS[7], 'node')
            if not routeNode then return 20 end
            if routeNode ~= ARGV[9] or
               redis.call('HGET', KEYS[7], 'boot') ~= ARGV[21] or
               redis.call('HGET', KEYS[7], 'revision') ~= ARGV[22] or
               redis.call('HGET', KEYS[7], 'realm') ~= ARGV[6] or
               redis.call('HGET', KEYS[7], 'map') ~= ARGV[7] or
               redis.call('HGET', KEYS[7], 'world') ~= ARGV[8] then
                return 21
            end
            if tonumber(
                    redis.call('HGET', KEYS[7], 'until') or '0') <=
                    now then
                return 24
            end
            local workerBoot = redis.call('HGET', KEYS[8], 'boot')
            if not workerBoot then return 22 end
            if workerBoot ~= ARGV[21] or
               redis.call('HGET', KEYS[8], 'revision') ~= ARGV[22] then
                return 21
            end
            if tonumber(
                    redis.call('HGET', KEYS[8], 'until') or '0') <=
                    now then
                return 24
            end
            local workerState = redis.call('HGET', KEYS[8], 'state')
            if workerState == '2' then return 23 end
            if workerState ~= '1' then return 24 end
            return 1
        end

        local generation = redis.call('HGET', KEYS[1], 'generation')
        if not generation or generation ~= ARGV[1] then
            return {20, 0, 0, 0, 0}
        end
        local generationExpires =
            tonumber(redis.call(
                'HGET', KEYS[1], 'generationExpires') or '0')
        if generationExpires <= now then
            return {21, 0, 0, 0, 0}
        end
        if redis.call('HGET', KEYS[1], 'account') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'username') ~= ARGV[3] then
            return {22, 0, 0, 0, 0}
        end
        if redis.call('HGET', KEYS[1], 'realm') ~= ARGV[6] then
            return {26, 0, 0, 0, 21}
        end
        if redis.call('HGET', KEYS[1], 'state') ~= '2' then
            return {31, 0, 0, 0, 0}
        end

        local connectionGeneration =
            redis.call('HGET', KEYS[3], 'generation')
        if connectionGeneration then
            local connectionExpires =
                tonumber(redis.call(
                    'HGET', KEYS[3], 'expires') or '0')
            if connectionExpires > now then
                return {23, 0, 0, 0, 0}
            end
            redis.call('DEL', KEYS[3])
        end

        local admissions =
            tonumber(redis.call('HGET', KEYS[4], 'admissions') or '0')
        if admissions >= tonumber(ARGV[14]) then
            return {24, 0, 0, 0, 0}
        end
        local issued =
            tonumber(redis.call(
                'HGET', KEYS[1], 'admissionsIssued') or '0')
        if issued >= tonumber(ARGV[15]) or
           redis.call('HGET', KEYS[1], 'admissionKey') then
            return {25, 0, 0, 0, 0}
        end
        if ARGV[20] ~= '1' then
            return {26, 0, 0, 0, ARGV[20]}
        end
        local routeStatus = validateRoute()
        if routeStatus ~= 1 then
            return {26, 0, 0, 0, routeStatus}
        end
        if redis.call('EXISTS', KEYS[2]) == 1 then
            return {26, 0, 0, 0, 27}
        end

        local workerAdmissions =
            tonumber(redis.call('HGET', KEYS[4], ARGV[19]) or '0')
        if workerAdmissions >= tonumber(ARGV[16]) then
            return {26, 0, 0, 0, 25}
        end
        local routeAdmissions =
            tonumber(redis.call('HGET', KEYS[4], ARGV[18]) or '0')
        if routeAdmissions >= tonumber(ARGV[17]) then
            return {26, 0, 0, 0, 26}
        end

        redis.call('HINCRBY', KEYS[4], 'admissions', 1)
        redis.call('HINCRBY', KEYS[4], 'reserved', 1)
        redis.call('HINCRBY', KEYS[4], ARGV[18], 1)
        redis.call('HINCRBY', KEYS[4], ARGV[19], 1)
        redis.call(
            'HSET', KEYS[1],
            'admissionsIssued', tostring(issued + 1),
            'admissionKey', KEYS[2],
            'admission', ARGV[11],
            'admissionExpires', expires)
        redis.call(
            'HSET', KEYS[2],
            'v', '1',
            'admission', ARGV[11],
            'generation', ARGV[1],
            'account', ARGV[2],
            'username', ARGV[3],
            'sourceConnection', ARGV[4],
            'sourceAddress', ARGV[5],
            'realm', ARGV[6],
            'map', ARGV[7],
            'world', ARGV[8],
            'node', ARGV[9],
            'revision', ARGV[10],
            'state', '1',
            'reservedAt', now,
            'expires', expires,
            'accountKey', KEYS[1],
            'connectionKey', KEYS[3],
            'routeField', ARGV[18],
            'workerField', ARGV[19])
        redis.call('PEXPIRE', KEYS[2], ARGV[13])
        redis.call(
            'HSET', KEYS[3],
            'v', '1',
            'kind', 'admission',
            'account', ARGV[2],
            'generation', ARGV[1],
            'admission', ARGV[11],
            'address', ARGV[5],
            'expires', expires)
        redis.call('PEXPIRE', KEYS[3], ARGV[13])
        redis.call(
            'ZADD', KEYS[5], expires, 'a|' .. KEYS[1])
        redis.call('PEXPIRE', KEYS[4], ARGV[13])
        redis.call('PEXPIRE', KEYS[5], ARGV[13])
        return {1, now, expires, 1, 0}
        """;

    public const string CommitAdmission =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local committedExpires = now + tonumber(ARGV[12])

        local function validateRoute()
            local routeNode = redis.call('HGET', KEYS[7], 'node')
            if not routeNode then return 20 end
            if routeNode ~= ARGV[10] or
               redis.call('HGET', KEYS[7], 'boot') ~= ARGV[14] or
               redis.call('HGET', KEYS[7], 'revision') ~= ARGV[15] or
               redis.call('HGET', KEYS[7], 'realm') ~= ARGV[7] or
               redis.call('HGET', KEYS[7], 'map') ~= ARGV[8] or
               redis.call('HGET', KEYS[7], 'world') ~= ARGV[9] then
                return 21
            end
            if tonumber(
                    redis.call('HGET', KEYS[7], 'until') or '0') <=
                    now then
                return 24
            end
            local workerBoot = redis.call('HGET', KEYS[8], 'boot')
            if not workerBoot then return 22 end
            if workerBoot ~= ARGV[14] or
               redis.call('HGET', KEYS[8], 'revision') ~= ARGV[15] then
                return 21
            end
            if tonumber(
                    redis.call('HGET', KEYS[8], 'until') or '0') <=
                    now then
                return 24
            end
            local workerState = redis.call('HGET', KEYS[8], 'state')
            if workerState == '2' then return 23 end
            if workerState ~= '1' then return 24 end
            return 1
        end

        local function decrement(field)
            local value = redis.call('HINCRBY', KEYS[5], field, -1)
            if value < 0 then redis.call('HSET', KEYS[5], field, '0') end
        end
        local function removeAdmission()
            local routeField = redis.call('HGET', KEYS[1], 'routeField')
            local workerField = redis.call('HGET', KEYS[1], 'workerField')
            decrement('admissions')
            decrement('reserved')
            if routeField then decrement(routeField) end
            if workerField then decrement(workerField) end
            redis.call('DEL', KEYS[4])
            redis.call('DEL', KEYS[1])
            if redis.call('HGET', KEYS[2], 'admissionKey') == KEYS[1] then
                redis.call(
                    'HDEL', KEYS[2],
                    'admissionKey', 'admission', 'admissionExpires')
            end
            redis.call('ZREM', KEYS[6], 'a|' .. KEYS[2])
        end

        local admission = redis.call('HGET', KEYS[1], 'admission')
        if not admission then return {27, 0, 0, 0, 0} end
        local generationExpires =
            tonumber(redis.call(
                'HGET', KEYS[2], 'generationExpires') or '0')
        if generationExpires <= now then
            removeAdmission()
            return {21, 0, 0, 0, 0}
        end
        local expires =
            tonumber(redis.call('HGET', KEYS[1], 'expires') or '0')
        if expires <= now then
            removeAdmission()
            return {28, 0, 0, 0, 0}
        end
        if admission ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'generation') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'account') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'username') ~= ARGV[4] or
           redis.call('HGET', KEYS[1], 'sourceConnection') ~= ARGV[5] or
           redis.call('HGET', KEYS[1], 'sourceAddress') ~= ARGV[6] or
           redis.call('HGET', KEYS[1], 'realm') ~= ARGV[7] or
           redis.call('HGET', KEYS[1], 'map') ~= ARGV[8] or
           redis.call('HGET', KEYS[1], 'world') ~= ARGV[9] or
           redis.call('HGET', KEYS[1], 'node') ~= ARGV[10] or
           redis.call('HGET', KEYS[1], 'revision') ~= ARGV[11] then
            return {29, 0, 0, 0, 0}
        end
        local state = redis.call('HGET', KEYS[1], 'state')
        local reservedAt =
            redis.call('HGET', KEYS[1], 'reservedAt')
        if state ~= '1' then
            return {30, reservedAt, expires, state, 0}
        end
        local routeStatus = validateRoute()
        if routeStatus ~= 1 then
            return {26, 0, 0, 0, routeStatus}
        end

        redis.call('HINCRBY', KEYS[5], 'reserved', -1)
        redis.call('HINCRBY', KEYS[5], 'committed', 1)
        redis.call(
            'HSET', KEYS[1],
            'state', '2',
            'expires', committedExpires)
        redis.call('PEXPIRE', KEYS[1], ARGV[13])
        redis.call(
            'HSET', KEYS[2], 'admissionExpires', committedExpires)
        redis.call('HSET', KEYS[4], 'expires', committedExpires)
        redis.call('PEXPIRE', KEYS[4], ARGV[13])
        redis.call(
            'ZADD', KEYS[6], committedExpires, 'a|' .. KEYS[2])
        return {2, reservedAt, committedExpires, 2, 0}
        """;

    public const string ResolveAdmission =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local admission = redis.call('HGET', KEYS[1], 'admission')
        if not admission then return {27, 0, 0, 0} end
        local generationExpires =
            tonumber(redis.call(
                'HGET', KEYS[2], 'generationExpires') or '0')
        if generationExpires <= now then
            return {21, 0, 0, 0}
        end
        local expires =
            tonumber(redis.call('HGET', KEYS[1], 'expires') or '0')
        if expires <= now then return {28, 0, 0, 0} end
        if admission ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'generation') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'account') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'username') ~= ARGV[4] or
           redis.call('HGET', KEYS[1], 'sourceConnection') ~= ARGV[5] or
           redis.call('HGET', KEYS[1], 'sourceAddress') ~= ARGV[6] or
           redis.call('HGET', KEYS[1], 'realm') ~= ARGV[7] or
           redis.call('HGET', KEYS[1], 'map') ~= ARGV[8] or
           redis.call('HGET', KEYS[1], 'world') ~= ARGV[9] or
           redis.call('HGET', KEYS[1], 'node') ~= ARGV[10] or
           redis.call('HGET', KEYS[1], 'revision') ~= ARGV[11] then
            return {29, 0, 0, 0}
        end
        local state = redis.call('HGET', KEYS[1], 'state')
        local reservedAt =
            redis.call('HGET', KEYS[1], 'reservedAt')
        if state ~= '2' then
            return {30, reservedAt, expires, state}
        end
        return {2, reservedAt, expires, state}
        """;

    public const string RefreshAdmission =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local admissionExpires = now + tonumber(ARGV[12])
        local generationExpires = now + tonumber(ARGV[13])

        local function validateRoute()
            local routeNode = redis.call('HGET', KEYS[7], 'node')
            if not routeNode then return 20 end
            if routeNode ~= ARGV[10] or
               redis.call('HGET', KEYS[7], 'boot') ~= ARGV[15] or
               redis.call('HGET', KEYS[7], 'revision') ~= ARGV[16] or
               redis.call('HGET', KEYS[7], 'realm') ~= ARGV[7] or
               redis.call('HGET', KEYS[7], 'map') ~= ARGV[8] or
               redis.call('HGET', KEYS[7], 'world') ~= ARGV[9] then
                return 21
            end
            if tonumber(
                    redis.call('HGET', KEYS[7], 'until') or '0') <=
                    now then
                return 24
            end
            local workerBoot = redis.call('HGET', KEYS[8], 'boot')
            if not workerBoot then return 22 end
            if workerBoot ~= ARGV[15] or
               redis.call('HGET', KEYS[8], 'revision') ~= ARGV[16] then
                return 21
            end
            if tonumber(
                    redis.call('HGET', KEYS[8], 'until') or '0') <=
                    now then
                return 24
            end
            local workerState = redis.call('HGET', KEYS[8], 'state')
            if workerState ~= '1' and workerState ~= '2' then
                return 24
            end
            return 1
        end

        local admission = redis.call('HGET', KEYS[1], 'admission')
        if not admission then return {27, 0, 0, 0, 0, 0} end
        local currentGenerationExpires =
            tonumber(redis.call(
                'HGET', KEYS[2], 'generationExpires') or '0')
        if currentGenerationExpires <= now then
            return {21, 0, 0, 0, 0, 0}
        end
        local expires =
            tonumber(redis.call('HGET', KEYS[1], 'expires') or '0')
        if expires <= now then return {28, 0, 0, 0, 0, 0} end
        if admission ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'generation') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'account') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'username') ~= ARGV[4] or
           redis.call('HGET', KEYS[1], 'sourceConnection') ~= ARGV[5] or
           redis.call('HGET', KEYS[1], 'sourceAddress') ~= ARGV[6] or
           redis.call('HGET', KEYS[1], 'realm') ~= ARGV[7] or
           redis.call('HGET', KEYS[1], 'map') ~= ARGV[8] or
           redis.call('HGET', KEYS[1], 'world') ~= ARGV[9] or
           redis.call('HGET', KEYS[1], 'node') ~= ARGV[10] or
           redis.call('HGET', KEYS[1], 'revision') ~= ARGV[11] then
            return {29, 0, 0, 0, 0, 0}
        end
        local state = redis.call('HGET', KEYS[1], 'state')
        local reservedAt =
            redis.call('HGET', KEYS[1], 'reservedAt')
        if state ~= '2' then
            return {30, reservedAt, expires, state, 0, 0}
        end
        local routeStatus = validateRoute()
        if routeStatus ~= 1 then
            return {26, 0, 0, 0, routeStatus, 0}
        end

        redis.call('HSET', KEYS[1], 'expires', admissionExpires)
        redis.call('PEXPIRE', KEYS[1], ARGV[14])
        redis.call(
            'HSET', KEYS[2],
            'generationExpires', generationExpires,
            'admissionExpires', admissionExpires)
        redis.call('PEXPIRE', KEYS[2], ARGV[14])
        redis.call(
            'HSET', KEYS[3], 'generationExpires', generationExpires)
        redis.call('PEXPIRE', KEYS[3], ARGV[14])
        redis.call('HSET', KEYS[4], 'expires', admissionExpires)
        redis.call('PEXPIRE', KEYS[4], ARGV[14])
        redis.call(
            'ZADD', KEYS[6], admissionExpires, 'a|' .. KEYS[2])
        redis.call(
            'ZADD', KEYS[6], generationExpires, 'g|' .. KEYS[2])
        redis.call('PEXPIRE', KEYS[5], ARGV[14])
        redis.call('PEXPIRE', KEYS[6], ARGV[14])
        return {
            3,
            reservedAt,
            admissionExpires,
            2,
            0,
            generationExpires
        }
        """;

    public const string RemoveAdmission =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)

        local function decrement(field)
            local value = redis.call('HINCRBY', KEYS[5], field, -1)
            if value < 0 then redis.call('HSET', KEYS[5], field, '0') end
        end

        local admission = redis.call('HGET', KEYS[1], 'admission')
        if not admission then return {27, 0, 0, 0} end
        local expires =
            tonumber(redis.call('HGET', KEYS[1], 'expires') or '0')
        if expires <= now then return {28, 0, 0, 0} end
        if admission ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'generation') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'account') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'username') ~= ARGV[4] or
           redis.call('HGET', KEYS[1], 'sourceConnection') ~= ARGV[5] or
           redis.call('HGET', KEYS[1], 'sourceAddress') ~= ARGV[6] or
           redis.call('HGET', KEYS[1], 'realm') ~= ARGV[7] or
           redis.call('HGET', KEYS[1], 'map') ~= ARGV[8] or
           redis.call('HGET', KEYS[1], 'world') ~= ARGV[9] or
           redis.call('HGET', KEYS[1], 'node') ~= ARGV[10] or
           redis.call('HGET', KEYS[1], 'revision') ~= ARGV[11] then
            return {29, 0, 0, 0}
        end
        local state = redis.call('HGET', KEYS[1], 'state')
        local reservedAt =
            redis.call('HGET', KEYS[1], 'reservedAt')
        if state ~= ARGV[12] then
            return {30, reservedAt, expires, state}
        end

        local routeField = redis.call('HGET', KEYS[1], 'routeField')
        local workerField = redis.call('HGET', KEYS[1], 'workerField')
        decrement('admissions')
        if state == '1' then decrement('reserved') else decrement('committed') end
        if routeField then decrement(routeField) end
        if workerField then decrement(workerField) end
        redis.call('DEL', KEYS[4])
        redis.call('DEL', KEYS[1])
        if redis.call('HGET', KEYS[2], 'admissionKey') == KEYS[1] then
            redis.call(
                'HDEL', KEYS[2],
                'admissionKey', 'admission', 'admissionExpires')
        end
        redis.call('ZREM', KEYS[6], 'a|' .. KEYS[2])
        return {ARGV[13], reservedAt, expires, state}
        """;
}
