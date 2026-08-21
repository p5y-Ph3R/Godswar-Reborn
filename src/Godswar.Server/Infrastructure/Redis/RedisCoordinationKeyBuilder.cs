using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed class RedisCoordinationKeyBuilder
{
    private const int HashCharacters = 32;
    private readonly string _prefix;

    public RedisCoordinationKeyBuilder(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        if (environment.Length > 32 ||
            environment.Any(character =>
                character is not (
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or
                    '-' or '_')))
        {
            throw new ArgumentException(
                "Redis coordination environment must be a bounded token.",
                nameof(environment));
        }

        _prefix = $"godswar:{environment}:v1";
    }

    public string Worker(ServerNodeId nodeId)
    {
        if (!nodeId.IsValid)
        {
            throw new ArgumentException(
                "A valid server node ID is required.",
                nameof(nodeId));
        }

        return Build("server", HashUtf8("node", nodeId.ToString()));
    }

    public string RealmContent(RealmId realmId)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentException(
                "A valid realm ID is required.",
                nameof(realmId));
        }

        Span<byte> value = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, realmId.Value);
        return Build("realm-content", HashBytes("realm", value));
    }

    public string Route(WorldInstanceId instanceId)
    {
        if (!instanceId.IsValid)
        {
            throw new ArgumentException(
                "A valid world-instance ID is required.",
                nameof(instanceId));
        }

        Span<byte> value = stackalloc byte[16];
        instanceId.Value.TryWriteBytes(value);
        return Build("route", HashBytes("world", value));
    }

    public string Player(int characterId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        Span<byte> value = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, characterId);
        return Build("player", HashBytes("character", value));
    }

    public string PlayerAccount(int accountId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        Span<byte> value = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, accountId);
        return Build("player-account", HashBytes("account", value));
    }

    public string Ticket(ReadOnlySpan<byte> ticketHash)
    {
        if (ticketHash.Length != 32)
        {
            throw new ArgumentException(
                "A ticket hash must contain exactly 32 bytes.",
                nameof(ticketHash));
        }

        return Build("ticket", Convert.ToHexString(ticketHash)[..HashCharacters]);
    }

    public string TicketGrant(Guid grantId)
    {
        if (grantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A nonzero ticket-grant ID is required.",
                nameof(grantId));
        }

        Span<byte> value = stackalloc byte[16];
        grantId.TryWriteBytes(value);
        return Build("ticket-grant", HashBytes("grant", value));
    }

    public string TicketGenerationRegistry() =>
        Build(
            "ticket-generations",
            HashUtf8("registry", "secure-game-ticket-generations"));

    public string OutstandingTicketRegistry() =>
        Build(
            "outstanding-tickets",
            HashUtf8("registry", "secure-game-outstanding-tickets"));

    public string LoginAccount(int accountId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        Span<byte> value = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, accountId);
        return Build("login-account", HashBytes("account", value));
    }

    public string LoginName(string canonicalUsername)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalUsername);
        return Build(
            "login-name",
            HashUtf8(
                "username",
                canonicalUsername.ToLowerInvariant()));
    }

    public string LoginConnection(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A nonzero gateway connection ID is required.",
                nameof(connectionId));
        }

        Span<byte> value = stackalloc byte[16];
        connectionId.TryWriteBytes(value);
        return Build(
            "login-connection",
            HashBytes("gateway-connection", value));
    }

    public string Admission(Guid admissionId)
    {
        if (admissionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A nonzero admission ID is required.",
                nameof(admissionId));
        }

        Span<byte> value = stackalloc byte[16];
        admissionId.TryWriteBytes(value);
        return Build("admission", HashBytes("admission", value));
    }

    public string GatewayCounters() =>
        Build("gateway-counters", "state");

    public string GatewayExpiry() =>
        Build("gateway-expiry", "state");

    public string GatewayRouteCounterField(
        WorldInstanceId instanceId)
    {
        if (!instanceId.IsValid)
        {
            throw new ArgumentException(
                "A valid world-instance ID is required.",
                nameof(instanceId));
        }

        Span<byte> value = stackalloc byte[16];
        instanceId.Value.TryWriteBytes(value);
        return "route-" + HashBytes("gateway-route-counter", value);
    }

    public string GatewayWorkerCounterField(ServerNodeId nodeId)
    {
        if (!nodeId.IsValid)
        {
            throw new ArgumentException(
                "A valid server node ID is required.",
                nameof(nodeId));
        }

        return "worker-" +
            HashUtf8("gateway-worker-counter", nodeId.ToString());
    }

    private string Build(string family, string opaqueId) =>
        $"{_prefix}:{family}:{opaqueId}";

    private static string HashUtf8(string domain, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return HashBytes(domain, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string HashBytes(
        string domain,
        ReadOnlySpan<byte> value)
    {
        var domainBytes = Encoding.ASCII.GetBytes(domain);
        Span<byte> hash = stackalloc byte[32];
        try
        {
            using var incremental =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            incremental.AppendData(domainBytes);
            incremental.AppendData([(byte)0]);
            incremental.AppendData(value);
            if (!incremental.TryGetHashAndReset(hash, out var written) ||
                written != hash.Length)
            {
                throw new CryptographicException(
                    "Could not hash a coordination key identity.");
            }

            return Convert.ToHexString(hash)[..HashCharacters];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domainBytes);
            CryptographicOperations.ZeroMemory(hash);
        }
    }
}
