using System.Security.Cryptography;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureGameTicketStoreChecks
{
    private static readonly byte[] DefaultInstanceId =
        Enumerable.Range(1, SecureProtocolConstants.ClientInstanceIdBytes)
            .Select(static value => checked((byte)value))
            .ToArray();

    private static readonly byte[] DefaultBuildHash =
        Convert.FromHexString(
            SecureNetworkOptions.PredecessorOriginSha256);

    private static readonly SecureGameTarget DefaultTarget = new(
        "127.1.1.110",
        "game.reborn.test",
        "reborn-game",
        routePort: 7000,
        tlsPort: 7443,
        serverId: 100);

    private static SecureTicketOperationDeadline Deadline =>
        SecureTicketOperationDeadline.Default;

    public static async Task RunAsync()
    {
        await CheckBoundsAndRolesAsync();
        await CheckHashOnlyStorageAndZeroingAsync();
        await CheckForgeryBitFlipAndReplayAsync();
        await CheckGenerationAndOutstandingTicketReplacementAsync();
        await CheckCapacityAndRevocationAsync();
        await CheckMonotonicExpiryBoundariesAsync();
        await CheckUnknownGrantDoesNotSweepTicketRegistryAsync();
        await CheckEveryTicketScopeAsync();
        await CheckPendingAndRestartRejectionAsync();
        await CheckOperationCancellationAndDeadlineAsync();
        await CheckFailedRevocationZeroesLocalGrantAsync();
        await CheckAtomicConcurrentConsumeAsync();
    }

    private static async Task CheckBoundsAndRolesAsync()
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new InMemoryGameTicketStore(capacity: 0),
            "zero ticket capacity is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new InMemoryGameTicketStore(
                ticketTtl: TimeSpan.FromMilliseconds(999)),
            "subsecond ticket TTL is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new InMemoryGameTicketStore(
                ticketTtl: TimeSpan.FromMinutes(6)),
            "unbounded ticket TTL is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new SecureTicketOperationDeadline(TimeSpan.Zero),
            "zero operation deadline is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new SecureTicketOperationDeadline(
                TimeSpan.FromSeconds(31)),
            "unbounded operation deadline is rejected");
        Check.Throws<ArgumentException>(
            () => _ = CreateContext(
                SecureEndpointRole.Login,
                instanceId: new byte[16]),
            "zero client-instance ID is rejected");
        Check.Throws<ArgumentException>(
            () => _ = new SecureGameTarget(
                "route",
                "game.reborn.test",
                "invalid/audience",
                7000,
                7443,
                100),
            "invalid audience is rejected");

        using var store = new InMemoryGameTicketStore();
        var generation = await StartAsync(store, 7, "test2");
        await ExpectThrowsAsync<ArgumentException>(
            () => store.IssueAsync(
                generation,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget,
                Deadline).AsTask(),
            "game connection cannot issue a ticket");

        await using var lease = await IssueAsync(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        using var bind = CreateBind(lease.Grant);
        await ExpectThrowsAsync<ArgumentException>(
            () => store.ConsumeAsync(
                bind,
                CreateContext(SecureEndpointRole.Login),
                DefaultTarget,
                Deadline).AsTask(),
            "login connection cannot consume a ticket");
    }

    private static async Task CheckHashOnlyStorageAndZeroingAsync()
    {
        using var store = new InMemoryGameTicketStore();
        var generation = await StartAsync(store, 7, "test2");
        var lease = await IssueAsync(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        var (grantId, ticket) = CopyGrantSecrets(lease.Grant);
        var storedHash = GetOnlyStoredTicketHash(store);
        var expectedHash = SHA256.HashData(ticket);
        Check.True(
            storedHash.SequenceEqual(expectedHash),
            "ticket authority stores the SHA-256 digest");
        Check.True(
            !storedHash.SequenceEqual(ticket),
            "ticket authority does not store the raw ticket");
        Check.True(
            !ContainsRawTicket(store, ticket),
            "ticket authority object graph excludes raw ticket bytes");

        await lease.DisposeAsync();
        Check.True(
            storedHash.All(static value => value == 0),
            "pending lease revocation zeroes the stored digest");
        Check.Equal(
            0,
            store.GetCachedSnapshot().OutstandingTickets,
            "pending lease disposal revokes its ticket");
        Check.True(
            !await lease.CommitAsync(Deadline),
            "disposed lease cannot commit");

        CryptographicOperations.ZeroMemory(grantId);
        CryptographicOperations.ZeroMemory(ticket);
        CryptographicOperations.ZeroMemory(expectedHash);
    }

    private static async Task CheckForgeryBitFlipAndReplayAsync()
    {
        using var store = new InMemoryGameTicketStore();
        var generation = await StartAsync(store, 7, "test2");
        await using var lease = await IssueAsync(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await lease.CommitAsync(Deadline),
            "forgery fixture commits");
        var secrets = CopyGrantSecrets(lease.Grant);
        var storedHash = GetOnlyStoredTicketHash(store);
        try
        {
            secrets.Ticket[0] ^= 0x80;
            using var bitFlipped = new SecureGameBind(
                secrets.GrantId,
                secrets.Ticket);
            CheckStatus(
                SecureTicketConsumeStatus.Rejected,
                await store.ConsumeAsync(
                    bitFlipped,
                    CreateContext(SecureEndpointRole.Game),
                    DefaultTarget,
                    Deadline),
                "one-bit ticket forgery is rejected");
            Check.True(
                storedHash.All(static value => value == 0),
                "failed atomic consume zeroes the removed stored digest");

            secrets.Ticket[0] ^= 0x80;
            using var originalAfterFailure = new SecureGameBind(
                secrets.GrantId,
                secrets.Ticket);
            CheckStatus(
                SecureTicketConsumeStatus.Rejected,
                await store.ConsumeAsync(
                    originalAfterFailure,
                    CreateContext(SecureEndpointRole.Game),
                    DefaultTarget,
                    Deadline),
                "failed presentation burns the ticket against replay");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secrets.GrantId);
            CryptographicOperations.ZeroMemory(secrets.Ticket);
        }
    }

    private static async Task
        CheckGenerationAndOutstandingTicketReplacementAsync()
    {
        using var store = new InMemoryGameTicketStore();
        var firstGeneration = await StartAsync(store, 7, "test2");
        await using var firstLease = await IssueAsync(
            store,
            firstGeneration,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await firstLease.CommitAsync(Deadline),
            "first ticket commit succeeds");
        using var firstBind = CreateBind(firstLease.Grant);

        await using var secondLease = await IssueAsync(
            store,
            firstGeneration,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await secondLease.CommitAsync(Deadline),
            "same-generation reissue succeeds");
        using var secondBind = CreateBind(secondLease.Grant);
        CheckStatus(
            SecureTicketConsumeStatus.Rejected,
            await store.ConsumeAsync(
                firstBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget,
                Deadline),
            "same-generation reissue invalidates the older ticket");

        CheckStatus(
            SecureTicketConsumeStatus.Accepted,
            await store.ConsumeAsync(
                secondBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget,
                Deadline),
            "latest same-generation ticket consumes");

        var replacedGeneration = await StartAsync(store, 7, "test2");
        Check.True(
            replacedGeneration.GenerationId !=
                firstGeneration.GenerationId,
            "successful login creates a distinct generation");
        Check.Equal(
            1,
            store.GetCachedSnapshot().ActiveGenerations,
            "one active generation exists per account");

        using var oldAuthority = new InMemoryGameTicketStore();
        var foreignGeneration = await StartAsync(
            oldAuthority,
            13,
            "fighter");
        var foreignIssue = await store.IssueAsync(
            foreignGeneration,
            CreateContext(SecureEndpointRole.Login),
            DefaultTarget,
            Deadline);
        Check.Equal(
            (int)SecureTicketIssueStatus.GenerationRejected,
            (int)foreignIssue.Status,
            "generation from another authority is rejected");
    }

    private static async Task CheckCapacityAndRevocationAsync()
    {
        using var store = new InMemoryGameTicketStore(capacity: 2);
        var first = await StartAsync(store, 1, "one");
        var second = await StartAsync(store, 2, "two");
        var thirdResult = await store.BeginLoginAsync(
            3,
            "three",
            Deadline);
        Check.Equal(
            (int)SecureLoginGenerationStatus.CapacityExceeded,
            (int)thirdResult.Status,
            "active generation registry is capacity bounded");

        await using var firstLease = await IssueAsync(
            store,
            first,
            CreateContext(SecureEndpointRole.Login));
        await using var secondLease = await IssueAsync(
            store,
            second,
            CreateContext(SecureEndpointRole.Login));
        Check.Equal(
            2,
            store.GetCachedSnapshot().OutstandingTickets,
            "ticket registry reaches but cannot exceed capacity");

        await store.RevokeGenerationAsync(
            first,
            Deadline);
        Check.Equal(
            1,
            store.GetCachedSnapshot().OutstandingTickets,
            "generation revocation removes its pending ticket");
        Check.Equal(
            1,
            store.GetCachedSnapshot().ActiveGenerations,
            "generation revocation releases capacity");

        var third = await StartAsync(store, 3, "three");
        await using var thirdLease = await IssueAsync(
            store,
            third,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await thirdLease.CommitAsync(Deadline),
            "replacement capacity is reusable");
        Check.Equal(
            2,
            store.GetCachedSnapshot().OutstandingTickets,
            "reused capacity remains bounded");
    }

    private static async Task CheckMonotonicExpiryBoundariesAsync()
    {
        var beforeBoundaryTime = new ManualTimeProvider();
        using (var store = new InMemoryGameTicketStore(
                   ticketTtl: TimeSpan.FromSeconds(60),
                   timeProvider: beforeBoundaryTime))
        {
            var generation = await StartAsync(store, 7, "test2");
            await using var lease = await IssueAsync(
                store,
                generation,
                CreateContext(SecureEndpointRole.Login));
            Check.True(
                await lease.CommitAsync(Deadline),
                "pre-boundary ticket commits");
            using var bind = CreateBind(lease.Grant);
            beforeBoundaryTime.Advance(
                TimeSpan.FromMilliseconds(59_999));
            CheckStatus(
                SecureTicketConsumeStatus.Accepted,
                await store.ConsumeAsync(
                    bind,
                    CreateContext(SecureEndpointRole.Game),
                    DefaultTarget,
                    Deadline),
                "ticket is valid immediately before monotonic expiry");
        }

        var boundaryTime = new ManualTimeProvider();
        using var boundaryStore = new InMemoryGameTicketStore(
            ticketTtl: TimeSpan.FromSeconds(60),
            timeProvider: boundaryTime);
        var boundaryGeneration = await StartAsync(
            boundaryStore,
            13,
            "fighter");
        await using var boundaryLease = await IssueAsync(
            boundaryStore,
            boundaryGeneration,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await boundaryLease.CommitAsync(Deadline),
            "boundary ticket commits");
        using var boundaryBind = CreateBind(boundaryLease.Grant);
        boundaryTime.Advance(TimeSpan.FromSeconds(60));
        CheckStatus(
            SecureTicketConsumeStatus.Expired,
            await boundaryStore.ConsumeAsync(
                boundaryBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget,
                Deadline),
            "ticket expires exactly at the monotonic TTL");
        Check.Equal(
            0,
            boundaryStore.GetCachedSnapshot().ActiveGenerations,
            "expired committed ticket releases its generation");
    }

    private static async Task CheckEveryTicketScopeAsync()
    {
        await CheckScopeRejectedAsync(
            gameContext: CreateContext(
                SecureEndpointRole.Game,
                instanceId: Enumerable.Repeat((byte)0xA5, 16).ToArray()),
            target: DefaultTarget,
            "wrong client instance");
        await CheckScopeRejectedAsync(
            gameContext: CreateContext(
                SecureEndpointRole.Game,
                buildHash: Enumerable.Repeat((byte)0x5A, 32).ToArray()),
            target: DefaultTarget,
            "wrong client build");
        await CheckScopeRejectedAsync(
            gameContext: CreateContext(
                SecureEndpointRole.Game,
                protocolMinor: 1),
            target: DefaultTarget,
            "wrong protocol version");
        await CheckScopeRejectedAsync(
            gameContext: CreateContext(SecureEndpointRole.Game),
            target: new SecureGameTarget(
                DefaultTarget.RouteHost,
                DefaultTarget.TlsHost,
                DefaultTarget.Audience,
                DefaultTarget.RoutePort,
                DefaultTarget.TlsPort,
                serverId: 101),
            "wrong target server");
        await CheckScopeRejectedAsync(
            gameContext: CreateContext(SecureEndpointRole.Game),
            target: new SecureGameTarget(
                DefaultTarget.RouteHost,
                DefaultTarget.TlsHost,
                "other-game",
                DefaultTarget.RoutePort,
                DefaultTarget.TlsPort,
                DefaultTarget.ServerId),
            "wrong audience");
        await CheckScopeRejectedAsync(
            gameContext: CreateContext(SecureEndpointRole.Game),
            target: new SecureGameTarget(
                "other-route",
                DefaultTarget.TlsHost,
                DefaultTarget.Audience,
                DefaultTarget.RoutePort,
                DefaultTarget.TlsPort,
                DefaultTarget.ServerId),
            "wrong route");

        using var store = new InMemoryGameTicketStore();
        var generation = await StartAsync(store, 347, "viewer");
        await using var lease = await IssueAsync(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await lease.CommitAsync(Deadline),
            "principal ticket commits");
        using var bind = CreateBind(lease.Grant);
        var result = await store.ConsumeAsync(
            bind,
            CreateContext(SecureEndpointRole.Game),
            DefaultTarget,
            Deadline);
        CheckStatus(
            SecureTicketConsumeStatus.Accepted,
            result,
            "fully matching scope is accepted");
        Check.Equal(
            347,
            result.Principal!.AccountId,
            "consumed ticket supplies authoritative account ID");
        Check.Equal(
            "viewer",
            result.Principal.Username,
            "consumed ticket supplies canonical username");
        Check.Equal(
            (int)SecureGamePermissions.EnterWorld,
            (int)result.Principal.Permissions,
            "consumed ticket supplies bounded permissions");
    }

    private static async Task
        CheckUnknownGrantDoesNotSweepTicketRegistryAsync()
    {
        var time = new ManualTimeProvider();
        using var store = new InMemoryGameTicketStore(
            ticketTtl: TimeSpan.FromSeconds(60),
            timeProvider: time);
        var generation = await StartAsync(store, 7, "test2");
        await using var lease = await IssueAsync(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(
            await lease.CommitAsync(Deadline),
            "forged-bind fixture commits");
        using var expiredBind = CreateBind(lease.Grant);
        time.Advance(TimeSpan.FromSeconds(60));

        var unknownGrant = RandomNumberGenerator.GetBytes(
            SecureProtocolConstants.GrantIdBytes);
        var unknownTicket = RandomNumberGenerator.GetBytes(
            SecureProtocolConstants.TicketBytes);
        try
        {
            using var unknownBind = new SecureGameBind(
                unknownGrant,
                unknownTicket);
            CheckStatus(
                SecureTicketConsumeStatus.Rejected,
                await store.ConsumeAsync(
                    unknownBind,
                    CreateContext(SecureEndpointRole.Game),
                    DefaultTarget,
                    Deadline),
                "unknown grant rejects without registry cleanup");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unknownGrant);
            CryptographicOperations.ZeroMemory(unknownTicket);
        }

        CheckStatus(
            SecureTicketConsumeStatus.Expired,
            await store.ConsumeAsync(
                expiredBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget,
                Deadline),
            "unknown grant did not scan or remove unrelated expired tickets");
    }

}
