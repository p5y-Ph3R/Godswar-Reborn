using System.Security.Cryptography;
using Godswar.Server.Infrastructure.Redis;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisGameTicketStoreIntegrationChecks
{
    private static async Task CheckForgeryAndUnknownGrantAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        HashSet<string> cleanup)
    {
        await using var store =
            new RedisGameTicketStore(executor, keys, capacity: 64);
        var generation =
            await StartAsync(store, 700_010, "redis_ten", cleanup, keys);
        await using var lease =
            await IssueAsync(store, generation, cleanup, keys);
        Check.True(
            await lease.CommitAsync(Deadline),
            "forgery Redis ticket activates");
        using var secrets = CopySecrets(lease.Grant);

        var unknownGrant =
            new byte[SecureProtocolConstants.GrantIdBytes];
        RandomNumberGenerator.Fill(unknownGrant);
        try
        {
            using var unknownBind =
                new SecureGameBind(unknownGrant, secrets.Ticket);
            Check.Equal(
                (int)SecureTicketConsumeStatus.Rejected,
                (int)(await store.ConsumeAsync(
                    unknownBind,
                    CreateContext(SecureEndpointRole.Game),
                    Target,
                    Deadline)).Status,
                "unknown grant does not locate a Redis ticket");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unknownGrant);
        }

        var forgedTicket = secrets.Ticket.ToArray();
        forgedTicket[0] ^= 0x01;
        try
        {
            using var forgedBind =
                new SecureGameBind(secrets.GrantId, forgedTicket);
            Check.Equal(
                (int)SecureTicketConsumeStatus.Rejected,
                (int)(await store.ConsumeAsync(
                    forgedBind,
                    CreateContext(SecureEndpointRole.Game),
                    Target,
                    Deadline)).Status,
                "ticket forgery is rejected by its stored digest");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(forgedTicket);
        }

        using var original = secrets.CreateBind();
        Check.Equal(
            (int)SecureTicketConsumeStatus.Rejected,
            (int)(await store.ConsumeAsync(
                original,
                CreateContext(SecureEndpointRole.Game),
                Target,
                Deadline)).Status,
            "known-grant forgery burns the ticket against replay");
    }
}
