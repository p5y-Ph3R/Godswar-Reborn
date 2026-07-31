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
        var generation = await StartAsync(store, 7, "test2");
        await using var lease = await IssueAsync(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await lease.CommitAsync(Deadline),
            "concurrent ticket commits");
        var secrets = CopyGrantSecrets(lease.Grant);
        try
        {
            var attempts = Enumerable.Range(0, 64)
                .Select(_ => Task.Run(async () =>
                {
                    using var bind = new SecureGameBind(
                        secrets.GrantId,
                        secrets.Ticket);
                    return await store.ConsumeAsync(
                        bind,
                        CreateContext(SecureEndpointRole.Game),
                        DefaultTarget,
                        Deadline);
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
                store.GetCachedSnapshot().OutstandingTickets,
                "atomic redemption removes the ticket exactly once");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secrets.GrantId);
            CryptographicOperations.ZeroMemory(secrets.Ticket);
        }
    }

    private static async Task CheckScopeRejectedAsync(
        SecureConnectionContext gameContext,
        SecureGameTarget target,
        string description)
    {
        using var store = new InMemoryGameTicketStore();
        var generation = await StartAsync(store, 7, "test2");
        await using var lease = await IssueAsync(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await lease.CommitAsync(Deadline),
            $"{description} fixture commits");
        using var bind = CreateBind(lease.Grant);
        CheckStatus(
            SecureTicketConsumeStatus.ScopeRejected,
            await store.ConsumeAsync(
                bind,
                gameContext,
                target,
                Deadline),
            $"{description} is rejected");
    }

    private static async ValueTask<SecureLoginGeneration> StartAsync(
        InMemoryGameTicketStore store,
        int accountId,
        string username)
    {
        var result = await store.BeginLoginAsync(
            accountId,
            username,
            Deadline);
        Check.True(result.IsStarted, $"login generation starts for {username}");
        return result.Generation!;
    }

    private static async ValueTask<SecureGameGrantLease> IssueAsync(
        InMemoryGameTicketStore store,
        SecureLoginGeneration generation,
        SecureConnectionContext loginContext)
    {
        var result = await store.IssueAsync(
            generation,
            loginContext,
            DefaultTarget,
            Deadline);
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

    private static async Task CheckPendingAndRestartRejectionAsync()
    {
        using var issuer = new InMemoryGameTicketStore();
        var generation = await StartAsync(issuer, 7, "test2");
        await using var pendingLease = await IssueAsync(
            issuer,
            generation,
            CreateContext(SecureEndpointRole.Login));
        using var pendingBind = CreateBind(pendingLease.Grant);
        CheckStatus(
            SecureTicketConsumeStatus.NotReady,
            await issuer.ConsumeAsync(
                pendingBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget,
                Deadline),
            "ticket reports a bounded activation race before redirect commit");
        Check.True(
            await pendingLease.CommitAsync(Deadline),
            "pending presentation remains available for post-redirect commit");
        using var committedBind = CreateBind(pendingLease.Grant);
        CheckStatus(
            SecureTicketConsumeStatus.Accepted,
            await issuer.ConsumeAsync(
                committedBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget,
                Deadline),
            "post-redirect commit makes the pending ticket redeemable");

        var restartGeneration = await StartAsync(
            issuer,
            13,
            "fighter");
        await using var restartLease = await IssueAsync(
            issuer,
            restartGeneration,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await restartLease.CommitAsync(Deadline),
            "restart ticket commits");
        using var restartBind = CreateBind(restartLease.Grant);
        using var restartedAuthority = new InMemoryGameTicketStore();
        CheckStatus(
            SecureTicketConsumeStatus.Rejected,
            await restartedAuthority.ConsumeAsync(
                restartBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget,
                Deadline),
            "in-memory ticket cannot survive authority restart");
    }

    private static async Task CheckOperationCancellationAndDeadlineAsync()
    {
        using var store = new InMemoryGameTicketStore();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectThrowsAsync<OperationCanceledException>(
            () => store.BeginLoginAsync(
                7,
                "test2",
                Deadline,
                cancelled.Token).AsTask(),
            "ticket operations honor caller cancellation");
        Check.Equal(
            0,
            store.GetCachedSnapshot().ActiveGenerations,
            "cancelled ticket operation leaves no generation");

        var authority = new BlockingLeaseAuthority();
        var grantId = Enumerable.Repeat(
                (byte)0x41,
                SecureProtocolConstants.GrantIdBytes)
            .ToArray();
        var ticket = Enumerable.Repeat(
                (byte)0x52,
                SecureProtocolConstants.TicketBytes)
            .ToArray();
        var grant = new SecureGameGrant(
            DefaultTarget.RouteHost,
            DefaultTarget.TlsHost,
            DefaultTarget.Audience,
            DefaultTarget.RoutePort,
            DefaultTarget.TlsPort,
            DefaultTarget.ServerId,
            checked((ulong)DateTimeOffset.UtcNow
                .AddMinutes(1)
                .ToUnixTimeMilliseconds()),
            grantId,
            ticket);
        await using var lease = new SecureGameGrantLease(
            authority,
            Guid.NewGuid(),
            Guid.NewGuid(),
            grant);
        await ExpectThrowsAsync<OperationCanceledException>(
            () => lease.ActivateAsync(
                new SecureTicketOperationDeadline(
                    TimeSpan.FromMilliseconds(10))).AsTask(),
            "grant activation cannot exceed its operation deadline");
        Check.True(
            !lease.IsDisposed,
            "uncertain timed-out activation remains revocable");
        await lease.RevokeAsync(Deadline);
        Check.Equal(
            1,
            authority.Revocations,
            "timed-out activation can be explicitly revoked");
        CryptographicOperations.ZeroMemory(grantId);
        CryptographicOperations.ZeroMemory(ticket);
    }

    private static async Task ExpectThrowsAsync<TException>(
        Func<Task> operation,
        string description)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Check failed: {description}. Expected {typeof(TException).Name}.");
    }

    private static async Task CheckFailedRevocationZeroesLocalGrantAsync()
    {
        var grantId = Enumerable.Repeat(
                (byte)0x63,
                SecureProtocolConstants.GrantIdBytes)
            .ToArray();
        var ticket = Enumerable.Repeat(
                (byte)0x74,
                SecureProtocolConstants.TicketBytes)
            .ToArray();
        var grant = new SecureGameGrant(
            DefaultTarget.RouteHost,
            DefaultTarget.TlsHost,
            DefaultTarget.Audience,
            DefaultTarget.RoutePort,
            DefaultTarget.TlsPort,
            DefaultTarget.ServerId,
            checked((ulong)DateTimeOffset.UtcNow
                .AddMinutes(1)
                .ToUnixTimeMilliseconds()),
            grantId,
            ticket);
        await using var lease = new SecureGameGrantLease(
            new FailingRevocationAuthority(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            grant);
        try
        {
            await ExpectThrowsAsync<InvalidOperationException>(
                () => lease.RevokeAsync(Deadline).AsTask(),
                "failed remote revocation is surfaced");
            Check.True(
                lease.IsDisposed,
                "failed remote revocation still disposes the local lease");
            Check.True(
                !grant.TryCopySecrets(
                    new byte[SecureProtocolConstants.GrantIdBytes],
                    new byte[SecureProtocolConstants.TicketBytes]),
                "failed remote revocation still zeroes local grant secrets");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantId);
            CryptographicOperations.ZeroMemory(ticket);
        }
    }

    private sealed class BlockingLeaseAuthority :
        ISecureGameGrantLeaseAuthority
    {
        public int Revocations { get; private set; }

        public async ValueTask<bool> TryActivateGrantAsync(
            Guid generationId,
            Guid grantId,
            SecureTicketOperationDeadline deadline,
            CancellationToken cancellationToken)
        {
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            return false;
        }

        public ValueTask RevokeGrantAsync(
            Guid generationId,
            Guid grantId,
            SecureTicketOperationDeadline deadline,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Revocations++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingRevocationAuthority :
        ISecureGameGrantLeaseAuthority
    {
        public ValueTask<bool> TryActivateGrantAsync(
            Guid generationId,
            Guid grantId,
            SecureTicketOperationDeadline deadline,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask RevokeGrantAsync(
            Guid generationId,
            Guid grantId,
            SecureTicketOperationDeadline deadline,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(
                new InvalidOperationException(
                    "Synthetic revocation failure."));
    }
}
