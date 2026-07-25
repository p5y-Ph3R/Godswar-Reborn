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

    public static async Task RunAsync()
    {
        CheckBoundsAndRoles();
        CheckHashOnlyStorageAndZeroing();
        CheckForgeryBitFlipAndReplay();
        CheckGenerationAndOutstandingTicketReplacement();
        CheckCapacityAndRevocation();
        CheckMonotonicExpiryBoundaries();
        CheckUnknownGrantDoesNotSweepTicketRegistry();
        CheckEveryTicketScope();
        CheckPendingAndRestartRejection();
        await CheckAtomicConcurrentConsumeAsync();
    }

    private static void CheckBoundsAndRoles()
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
        var generation = Start(store, 7, "test2");
        Check.Throws<ArgumentException>(
            () => store.Issue(
                generation,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget),
            "game connection cannot issue a ticket");

        using var lease = Issue(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        using var bind = CreateBind(lease.Grant);
        Check.Throws<ArgumentException>(
            () => store.Consume(
                bind,
                CreateContext(SecureEndpointRole.Login),
                DefaultTarget),
            "login connection cannot consume a ticket");
    }

    private static void CheckHashOnlyStorageAndZeroing()
    {
        using var store = new InMemoryGameTicketStore();
        var generation = Start(store, 7, "test2");
        var lease = Issue(
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

        lease.Dispose();
        Check.True(
            storedHash.All(static value => value == 0),
            "pending lease revocation zeroes the stored digest");
        Check.Equal(
            0,
            store.GetSnapshot().OutstandingTickets,
            "pending lease disposal revokes its ticket");
        Check.True(
            !lease.Commit(),
            "disposed lease cannot commit");

        CryptographicOperations.ZeroMemory(grantId);
        CryptographicOperations.ZeroMemory(ticket);
        CryptographicOperations.ZeroMemory(expectedHash);
    }

    private static void CheckForgeryBitFlipAndReplay()
    {
        using var store = new InMemoryGameTicketStore();
        var generation = Start(store, 7, "test2");
        using var lease = Issue(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(lease.Commit(), "forgery fixture commits");
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
                store.Consume(
                    bitFlipped,
                    CreateContext(SecureEndpointRole.Game),
                    DefaultTarget),
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
                store.Consume(
                    originalAfterFailure,
                    CreateContext(SecureEndpointRole.Game),
                    DefaultTarget),
                "failed presentation burns the ticket against replay");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secrets.GrantId);
            CryptographicOperations.ZeroMemory(secrets.Ticket);
        }
    }

    private static void CheckGenerationAndOutstandingTicketReplacement()
    {
        using var store = new InMemoryGameTicketStore();
        var firstGeneration = Start(store, 7, "test2");
        using var firstLease = Issue(
            store,
            firstGeneration,
            CreateContext(SecureEndpointRole.Login));
        Check.True(firstLease.Commit(), "first ticket commit succeeds");
        using var firstBind = CreateBind(firstLease.Grant);

        using var secondLease = Issue(
            store,
            firstGeneration,
            CreateContext(SecureEndpointRole.Login));
        Check.True(secondLease.Commit(), "same-generation reissue succeeds");
        using var secondBind = CreateBind(secondLease.Grant);
        CheckStatus(
            SecureTicketConsumeStatus.Rejected,
            store.Consume(
                firstBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget),
            "same-generation reissue invalidates the older ticket");

        CheckStatus(
            SecureTicketConsumeStatus.Accepted,
            store.Consume(
                secondBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget),
            "latest same-generation ticket consumes");

        var replacedGeneration = Start(store, 7, "test2");
        Check.True(
            replacedGeneration.GenerationId !=
                firstGeneration.GenerationId,
            "successful login creates a distinct generation");
        Check.Equal(
            1,
            store.GetSnapshot().ActiveGenerations,
            "one active generation exists per account");

        using var oldAuthority = new InMemoryGameTicketStore();
        var foreignGeneration = Start(oldAuthority, 13, "fighter");
        var foreignIssue = store.Issue(
            foreignGeneration,
            CreateContext(SecureEndpointRole.Login),
            DefaultTarget);
        Check.Equal(
            (int)SecureTicketIssueStatus.GenerationRejected,
            (int)foreignIssue.Status,
            "generation from another authority is rejected");
    }

    private static void CheckCapacityAndRevocation()
    {
        using var store = new InMemoryGameTicketStore(capacity: 2);
        var first = Start(store, 1, "one");
        var second = Start(store, 2, "two");
        var thirdResult = store.BeginLogin(3, "three");
        Check.Equal(
            (int)SecureLoginGenerationStatus.CapacityExceeded,
            (int)thirdResult.Status,
            "active generation registry is capacity bounded");

        using var firstLease = Issue(
            store,
            first,
            CreateContext(SecureEndpointRole.Login));
        using var secondLease = Issue(
            store,
            second,
            CreateContext(SecureEndpointRole.Login));
        Check.Equal(
            2,
            store.GetSnapshot().OutstandingTickets,
            "ticket registry reaches but cannot exceed capacity");

        store.RevokeGeneration(first);
        Check.Equal(
            1,
            store.GetSnapshot().OutstandingTickets,
            "generation revocation removes its pending ticket");
        Check.Equal(
            1,
            store.GetSnapshot().ActiveGenerations,
            "generation revocation releases capacity");

        var third = Start(store, 3, "three");
        using var thirdLease = Issue(
            store,
            third,
            CreateContext(SecureEndpointRole.Login));
        Check.True(thirdLease.Commit(), "replacement capacity is reusable");
        Check.Equal(
            2,
            store.GetSnapshot().OutstandingTickets,
            "reused capacity remains bounded");
    }

    private static void CheckMonotonicExpiryBoundaries()
    {
        var beforeBoundaryTime = new ManualTimeProvider();
        using (var store = new InMemoryGameTicketStore(
                   ticketTtl: TimeSpan.FromSeconds(60),
                   timeProvider: beforeBoundaryTime))
        {
            var generation = Start(store, 7, "test2");
            using var lease = Issue(
                store,
                generation,
                CreateContext(SecureEndpointRole.Login));
            Check.True(lease.Commit(), "pre-boundary ticket commits");
            using var bind = CreateBind(lease.Grant);
            beforeBoundaryTime.Advance(
                TimeSpan.FromMilliseconds(59_999));
            CheckStatus(
                SecureTicketConsumeStatus.Accepted,
                store.Consume(
                    bind,
                    CreateContext(SecureEndpointRole.Game),
                    DefaultTarget),
                "ticket is valid immediately before monotonic expiry");
        }

        var boundaryTime = new ManualTimeProvider();
        using var boundaryStore = new InMemoryGameTicketStore(
            ticketTtl: TimeSpan.FromSeconds(60),
            timeProvider: boundaryTime);
        var boundaryGeneration = Start(
            boundaryStore,
            13,
            "fighter");
        using var boundaryLease = Issue(
            boundaryStore,
            boundaryGeneration,
            CreateContext(SecureEndpointRole.Login));
        Check.True(boundaryLease.Commit(), "boundary ticket commits");
        using var boundaryBind = CreateBind(boundaryLease.Grant);
        boundaryTime.Advance(TimeSpan.FromSeconds(60));
        CheckStatus(
            SecureTicketConsumeStatus.Expired,
            boundaryStore.Consume(
                boundaryBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget),
            "ticket expires exactly at the monotonic TTL");
        Check.Equal(
            0,
            boundaryStore.GetSnapshot().ActiveGenerations,
            "expired committed ticket releases its generation");
    }

    private static void CheckEveryTicketScope()
    {
        CheckScopeRejected(
            gameContext: CreateContext(
                SecureEndpointRole.Game,
                instanceId: Enumerable.Repeat((byte)0xA5, 16).ToArray()),
            target: DefaultTarget,
            "wrong client instance");
        CheckScopeRejected(
            gameContext: CreateContext(
                SecureEndpointRole.Game,
                buildHash: Enumerable.Repeat((byte)0x5A, 32).ToArray()),
            target: DefaultTarget,
            "wrong client build");
        CheckScopeRejected(
            gameContext: CreateContext(
                SecureEndpointRole.Game,
                protocolMinor: 1),
            target: DefaultTarget,
            "wrong protocol version");
        CheckScopeRejected(
            gameContext: CreateContext(SecureEndpointRole.Game),
            target: new SecureGameTarget(
                DefaultTarget.RouteHost,
                DefaultTarget.TlsHost,
                DefaultTarget.Audience,
                DefaultTarget.RoutePort,
                DefaultTarget.TlsPort,
                serverId: 101),
            "wrong target server");
        CheckScopeRejected(
            gameContext: CreateContext(SecureEndpointRole.Game),
            target: new SecureGameTarget(
                DefaultTarget.RouteHost,
                DefaultTarget.TlsHost,
                "other-game",
                DefaultTarget.RoutePort,
                DefaultTarget.TlsPort,
                DefaultTarget.ServerId),
            "wrong audience");
        CheckScopeRejected(
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
        var generation = Start(store, 347, "viewer");
        using var lease = Issue(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(lease.Commit(), "principal ticket commits");
        using var bind = CreateBind(lease.Grant);
        var result = store.Consume(
            bind,
            CreateContext(SecureEndpointRole.Game),
            DefaultTarget);
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

    private static void CheckUnknownGrantDoesNotSweepTicketRegistry()
    {
        var time = new ManualTimeProvider();
        using var store = new InMemoryGameTicketStore(
            ticketTtl: TimeSpan.FromSeconds(60),
            timeProvider: time);
        var generation = Start(store, 7, "test2");
        using var lease = Issue(
            store,
            generation,
            CreateContext(SecureEndpointRole.Login));
        Check.True(lease.Commit(), "forged-bind fixture commits");
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
                store.Consume(
                    unknownBind,
                    CreateContext(SecureEndpointRole.Game),
                    DefaultTarget),
                "unknown grant rejects without registry cleanup");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unknownGrant);
            CryptographicOperations.ZeroMemory(unknownTicket);
        }

        CheckStatus(
            SecureTicketConsumeStatus.Expired,
            store.Consume(
                expiredBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget),
            "unknown grant did not scan or remove unrelated expired tickets");
    }

    private static void CheckPendingAndRestartRejection()
    {
        using var issuer = new InMemoryGameTicketStore();
        var generation = Start(issuer, 7, "test2");
        using var pendingLease = Issue(
            issuer,
            generation,
            CreateContext(SecureEndpointRole.Login));
        using var pendingBind = CreateBind(pendingLease.Grant);
        CheckStatus(
            SecureTicketConsumeStatus.Rejected,
            issuer.Consume(
                pendingBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget),
            "ticket cannot bind before physical grant activation");
        Check.True(
            !pendingLease.Commit(),
            "consumed pending record cannot later commit");

        var restartGeneration = Start(issuer, 13, "fighter");
        using var restartLease = Issue(
            issuer,
            restartGeneration,
            CreateContext(SecureEndpointRole.Login));
        Check.True(restartLease.Commit(), "restart ticket commits");
        using var restartBind = CreateBind(restartLease.Grant);
        using var restartedAuthority = new InMemoryGameTicketStore();
        CheckStatus(
            SecureTicketConsumeStatus.Rejected,
            restartedAuthority.Consume(
                restartBind,
                CreateContext(SecureEndpointRole.Game),
                DefaultTarget),
            "in-memory ticket cannot survive authority restart");
    }

}
