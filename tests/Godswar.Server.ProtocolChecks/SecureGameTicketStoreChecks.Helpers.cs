using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureGameTicketStoreChecks
{
    private static async Task CheckAtomicConcurrentConsumeAsync()
    {
        using var store = new InMemoryGameTicketStore();
        var generation = Start(store, 7, "test2");
        using var lease = Issue(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(lease.Commit(), "concurrent ticket commits");
        var secrets = CopyGrantSecrets(lease.Grant);
        try
        {
            var attempts = Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() =>
                {
                    using var bind = new SecureGameBind(
                        secrets.GrantId,
                        secrets.Ticket);
                    return store.Consume(
                        bind,
                        CreateContext(SecureEndpointRole.Game),
                        DefaultTarget);
                }))
                .ToArray();
            var results = await Task.WhenAll(attempts);
            Check.Equal(
                1,
                results.Count(static result => result.IsAccepted),
                "concurrent redemption has exactly one winner");
            Check.Equal(
                63,
                results.Count(static result =>
                    result.Status == SecureTicketConsumeStatus.Rejected),
                "all concurrent replays fail generically");
            Check.Equal(
                0,
                store.GetSnapshot().OutstandingTickets,
                "atomic redemption removes the ticket exactly once");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secrets.GrantId);
            CryptographicOperations.ZeroMemory(secrets.Ticket);
        }
    }

    private static void CheckScopeRejected(
        SecureConnectionContext gameContext,
        SecureGameTarget target,
        string description)
    {
        using var store = new InMemoryGameTicketStore();
        var generation = Start(store, 7, "test2");
        using var lease = Issue(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(lease.Commit(), $"{description} fixture commits");
        using var bind = CreateBind(lease.Grant);
        CheckStatus(
            SecureTicketConsumeStatus.ScopeRejected,
            store.Consume(bind, gameContext, target),
            $"{description} is rejected");
    }

    private static SecureLoginGeneration Start(
        InMemoryGameTicketStore store,
        int accountId,
        string username)
    {
        var result = store.BeginLogin(accountId, username);
        Check.True(result.IsStarted, $"login generation starts for {username}");
        return result.Generation!;
    }

    private static SecureGameGrantLease Issue(
        InMemoryGameTicketStore store,
        SecureLoginGeneration generation,
        SecureConnectionContext loginContext)
    {
        var result = store.Issue(
            generation,
            loginContext,
            DefaultTarget);
        Check.True(
            result.IsIssued,
            $"ticket issues for account {generation.AccountId}");
        return result.Lease!;
    }

    private static SecureConnectionContext CreateContext(
        SecureEndpointRole role,
        byte[]? instanceId = null,
        byte[]? buildHash = null,
        ushort protocolMinor =
            SecureProtocolConstants.ProtocolMinor)
    {
        return new SecureConnectionContext(
            role,
            SecureProtocolConstants.ProtocolMajor,
            protocolMinor,
            instanceId ?? DefaultInstanceId,
            buildHash ?? DefaultBuildHash);
    }

    private static SecureGameBind CreateBind(SecureGameGrant grant)
    {
        var secrets = CopyGrantSecrets(grant);
        try
        {
            return new SecureGameBind(
                secrets.GrantId,
                secrets.Ticket);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secrets.GrantId);
            CryptographicOperations.ZeroMemory(secrets.Ticket);
        }
    }

    private static (byte[] GrantId, byte[] Ticket) CopyGrantSecrets(
        SecureGameGrant grant)
    {
        var grantId = new byte[SecureProtocolConstants.GrantIdBytes];
        var ticket = new byte[SecureProtocolConstants.TicketBytes];
        Check.True(
            grant.TryCopySecrets(grantId, ticket),
            "grant secrets copy while lease is active");
        return (grantId, ticket);
    }

    private static void CheckStatus(
        SecureTicketConsumeStatus expected,
        SecureTicketConsumeResult actual,
        string description)
    {
        Check.Equal((int)expected, (int)actual.Status, description);
        Check.True(
            expected == SecureTicketConsumeStatus.Accepted
                ? actual.Principal is not null
                : actual.Principal is null,
            $"{description} has canonical principal presence");
    }

    private static byte[] GetOnlyStoredTicketHash(
        InMemoryGameTicketStore store)
    {
        var records = GetStoredRecords(store);
        Check.Equal(1, records.Length, "hash fixture has one ticket record");
        return (byte[])(records[0].GetType()
            .GetProperty(
                "TicketHash",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            ?.GetValue(records[0])
            ?? throw new InvalidOperationException(
                "TicketHash test inspection failed."));
    }

    private static bool ContainsRawTicket(
        InMemoryGameTicketStore store,
        ReadOnlySpan<byte> ticket)
    {
        foreach (var record in GetStoredRecords(store))
        {
            foreach (var field in record.GetType().GetFields(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic))
            {
                if (field.GetValue(record) is byte[] bytes &&
                    bytes.AsSpan().SequenceEqual(ticket))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static object[] GetStoredRecords(
        InMemoryGameTicketStore store)
    {
        var dictionary = typeof(InMemoryGameTicketStore)
            .GetField(
                "_tickets",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(store) as IEnumerable
            ?? throw new InvalidOperationException(
                "Ticket dictionary test inspection failed.");
        return dictionary.Cast<object>()
            .Select(entry => entry.GetType()
                .GetProperty("Value")?
                .GetValue(entry)
                ?? throw new InvalidOperationException(
                    "Ticket record test inspection failed."))
            .ToArray();
    }
}
