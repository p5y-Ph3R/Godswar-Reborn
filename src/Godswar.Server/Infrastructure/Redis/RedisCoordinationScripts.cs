namespace Godswar.Server.Infrastructure.Redis;

internal static class RedisCoordinationScripts
{
    public const string RegisterWorker =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local leaseExpires = now + tonumber(ARGV[8])
        local current = redis.call('HGET', KEYS[1], 'boot')
        if current and current ~= ARGV[1] then
            return {-1, 0, 0}
        end
        local workerContent = redis.call('HGET', KEYS[1], 'content')
        if workerContent and workerContent ~= ARGV[4] then
            return {-2, 0, 0}
        end
        local realmContent = redis.call('GET', KEYS[2])
        if realmContent and realmContent ~= ARGV[4] then
            return {-2, 0, 0}
        end
        local revision = tonumber(redis.call('HGET', KEYS[1], 'revision'))
        local status = 2
        if not revision then
            revision = 1
            status = 1
        end
        redis.call(
            'HSET', KEYS[1],
            'v', '1',
            'boot', ARGV[1],
            'node', ARGV[2],
            'build', ARGV[3],
            'content', ARGV[4],
            'state', ARGV[5],
            'capabilities', ARGV[6],
            'realm', ARGV[7],
            'revision', tostring(revision),
            'until', leaseExpires)
        redis.call('PEXPIRE', KEYS[1], ARGV[8])
        if not realmContent then
            redis.call('SET', KEYS[2], ARGV[4], 'PX', ARGV[8])
        else
            local realmTtl = redis.call('PTTL', KEYS[2])
            if realmTtl >= 0 and realmTtl < tonumber(ARGV[8]) then
                redis.call('PEXPIRE', KEYS[2], ARGV[8])
            end
        end
        return {status, revision, leaseExpires}
        """;

    public const string RegisterRoute =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local leaseExpires = now + tonumber(ARGV[7])
        local nodeBoot = redis.call('HGET', KEYS[2], 'boot')
        local nodeRevision = redis.call('HGET', KEYS[2], 'revision')
        if nodeBoot ~= ARGV[5] or nodeRevision ~= ARGV[6] then
            return {-1, 0}
        end
        local currentNode = redis.call('HGET', KEYS[1], 'node')
        if currentNode and currentNode ~= ARGV[4] then
            return {-1, 0}
        end
        redis.call(
            'HSET', KEYS[1],
            'v', '1',
            'realm', ARGV[1],
            'map', ARGV[2],
            'world', ARGV[3],
            'node', ARGV[4],
            'boot', ARGV[5],
            'revision', ARGV[6],
            'until', leaseExpires)
        redis.call('PEXPIRE', KEYS[1], ARGV[7])
        return {1, leaseExpires}
        """;

    public const string RenewWorker =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local leaseExpires = now + tonumber(ARGV[5])
        local boot = redis.call('HGET', KEYS[1], 'boot')
        local revision = redis.call('HGET', KEYS[1], 'revision')
        if not boot then
            return {0, 0}
        end
        if boot ~= ARGV[1] or revision ~= ARGV[2] then
            return {-1, 0}
        end
        local workerContent = redis.call('HGET', KEYS[1], 'content')
        local workerRealm = redis.call('HGET', KEYS[1], 'realm')
        if workerContent ~= ARGV[4] or workerRealm ~= ARGV[6] then
            return {-1, 0}
        end
        local realmContent = redis.call('GET', KEYS[2])
        if realmContent and realmContent ~= ARGV[4] then
            return {-1, 0}
        end
        redis.call(
            'HSET', KEYS[1],
            'state', ARGV[3],
            'until', leaseExpires)
        redis.call('PEXPIRE', KEYS[1], ARGV[5])
        if not realmContent then
            redis.call('SET', KEYS[2], ARGV[4], 'PX', ARGV[5])
        else
            local realmTtl = redis.call('PTTL', KEYS[2])
            if realmTtl >= 0 and realmTtl < tonumber(ARGV[5]) then
                redis.call('PEXPIRE', KEYS[2], ARGV[5])
            end
        end
        return {1, leaseExpires}
        """;

    public const string RenewRoute =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local leaseExpires = now + tonumber(ARGV[4])
        local node = redis.call('HGET', KEYS[1], 'node')
        local boot = redis.call('HGET', KEYS[1], 'boot')
        local revision = redis.call('HGET', KEYS[1], 'revision')
        if not node then
            return {0, 0}
        end
        if node ~= ARGV[1] or
           boot ~= ARGV[2] or
           revision ~= ARGV[3] then
            return {-1, 0}
        end
        redis.call('HSET', KEYS[1], 'until', leaseExpires)
        redis.call('PEXPIRE', KEYS[1], ARGV[4])
        return {1, leaseExpires}
        """;

    public const string ReleaseExact =
        """
        local boot = redis.call('HGET', KEYS[1], ARGV[1])
        local revision = redis.call('HGET', KEYS[1], ARGV[2])
        if not boot then
            return 0
        end
        if boot ~= ARGV[3] or revision ~= ARGV[4] then
            return -1
        end
        redis.call('DEL', KEYS[1])
        return 1
        """;

    public const string InstallPlayer =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local leaseExpires = now + tonumber(ARGV[12])
        local routeNode = redis.call('HGET', KEYS[2], 'node')
        if not routeNode then
            return {0, 0, 0}
        end
        local routeRevision = redis.call('HGET', KEYS[2], 'revision')
        if routeNode ~= ARGV[6] or
           redis.call('HGET', KEYS[2], 'boot') ~= ARGV[7] or
           redis.call('HGET', KEYS[2], 'realm') ~= ARGV[8] or
           redis.call('HGET', KEYS[2], 'map') ~= ARGV[9] or
           redis.call('HGET', KEYS[2], 'world') ~= ARGV[10] or
           tonumber(redis.call('HGET', KEYS[2], 'until') or '0') <=
               now then
            return {-1, 0, 0}
        end
        local workerBoot = redis.call('HGET', KEYS[3], 'boot')
        if not workerBoot then
            return {0, 0, 0}
        end
        if workerBoot ~= ARGV[7] or
           redis.call('HGET', KEYS[3], 'revision') ~= routeRevision or
           redis.call('HGET', KEYS[3], 'state') ~= '1' or
           tonumber(redis.call('HGET', KEYS[3], 'until') or '0') <=
               now then
            return {-1, 0, 0}
        end

        local generation =
            tonumber(redis.call('HGET', KEYS[1], 'generation'))
        local same = false
        if generation then
            local sameIdentity =
                redis.call('HGET', KEYS[1], 'account') == ARGV[1] and
                redis.call('HGET', KEYS[1], 'owner') == ARGV[3] and
                redis.call('HGET', KEYS[1], 'token') == ARGV[5] and
                redis.call('HGET', KEYS[1], 'node') == ARGV[6] and
                redis.call('HGET', KEYS[1], 'boot') == ARGV[7]
            local requestedGeneration = tonumber(ARGV[4])
            same =
                sameIdentity and requestedGeneration == generation
            if not same and requestedGeneration <= generation then
                return {-1, 0, 0}
            end
        end
        local version =
            (tonumber(redis.call('HGET', KEYS[1], 'version')) or 0) + 1
        redis.call(
            'HSET', KEYS[1],
            'v', '1',
            'account', ARGV[1],
            'character', ARGV[2],
            'owner', ARGV[3],
            'generation', ARGV[4],
            'token', ARGV[5],
            'node', ARGV[6],
            'boot', ARGV[7],
            'realm', ARGV[8],
            'map', ARGV[9],
            'world', ARGV[10],
            'presence', ARGV[11],
            'version', tostring(version),
            'until', leaseExpires)
        redis.call('PEXPIRE', KEYS[1], ARGV[12])
        if same then
            return {2, version, leaseExpires}
        end
        return {1, version, leaseExpires}
        """;

    public const string RenewPlayer =
        """
        local serverTime = redis.call('TIME')
        local now =
            (tonumber(serverTime[1]) * 1000) +
            math.floor(tonumber(serverTime[2]) / 1000)
        local leaseExpires = now + tonumber(ARGV[10])
        local routeNode = redis.call('HGET', KEYS[2], 'node')
        if not routeNode then
            return {0, 0, 0}
        end
        local routeRevision = redis.call('HGET', KEYS[2], 'revision')
        if routeNode ~= ARGV[4] or
           redis.call('HGET', KEYS[2], 'boot') ~= ARGV[5] or
           redis.call('HGET', KEYS[2], 'realm') ~= ARGV[6] or
           redis.call('HGET', KEYS[2], 'map') ~= ARGV[7] or
           redis.call('HGET', KEYS[2], 'world') ~= ARGV[8] or
           tonumber(redis.call('HGET', KEYS[2], 'until') or '0') <=
               now then
            return {-1, 0, 0}
        end
        local workerBoot = redis.call('HGET', KEYS[3], 'boot')
        if not workerBoot then
            return {0, 0, 0}
        end
        local workerState = redis.call('HGET', KEYS[3], 'state')
        if workerBoot ~= ARGV[5] or
           redis.call('HGET', KEYS[3], 'revision') ~= routeRevision or
           (workerState ~= '1' and
               not (workerState == '2' and ARGV[9] == '3')) or
           tonumber(redis.call('HGET', KEYS[3], 'until') or '0') <=
               now then
            return {-1, 0, 0}
        end

        local owner = redis.call('HGET', KEYS[1], 'owner')
        if not owner then
            return {0, 0, 0}
        end
        if owner ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'generation') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'token') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'node') ~= ARGV[4] or
           redis.call('HGET', KEYS[1], 'boot') ~= ARGV[5] then
            return {-1, 0, 0}
        end
        local version =
            (tonumber(redis.call('HGET', KEYS[1], 'version')) or 0) + 1
        redis.call(
            'HSET', KEYS[1],
            'realm', ARGV[6],
            'map', ARGV[7],
            'world', ARGV[8],
            'presence', ARGV[9],
            'version', tostring(version),
            'until', leaseExpires)
        redis.call('PEXPIRE', KEYS[1], ARGV[10])
        return {1, version, leaseExpires}
        """;

    public const string ReleasePlayer =
        """
        local owner = redis.call('HGET', KEYS[1], 'owner')
        if not owner then
            return 0
        end
        if owner ~= ARGV[1] or
           redis.call('HGET', KEYS[1], 'generation') ~= ARGV[2] or
           redis.call('HGET', KEYS[1], 'token') ~= ARGV[3] or
           redis.call('HGET', KEYS[1], 'node') ~= ARGV[4] or
           redis.call('HGET', KEYS[1], 'boot') ~= ARGV[5] then
            return -1
        end
        redis.call('DEL', KEYS[1])
        return 1
        """;
}
