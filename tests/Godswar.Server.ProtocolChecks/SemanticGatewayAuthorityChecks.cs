using Godswar.Server.Networking.SemanticGateway;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SemanticGatewayChecks
{
    private static void CheckAuthorityLifecycle()
    {
        var time = new ManualTimeProvider();
        var routes = CreateDirectory(
            workerACapacity: 2,
            spartaCapacity: 2);
        var authority = new SemanticGatewayAdmissionAuthority(
            routes,
            new SemanticGatewayAuthorityLimits(
                maximumLoginGenerations: 4,
                maximumAdmissions: 4,
                maximumAdmissionsPerGeneration: 1,
                loginGenerationTtl: TimeSpan.FromMinutes(10),
                reservationTtl: TimeSpan.FromMinutes(1),
                committedAdmissionTtl: TimeSpan.FromMinutes(5)),
            time);
        var principal = new SemanticGatewayPrincipal(7, "TEST");
        var loginSource = Source();
        var login = authority.BeginLogin(
            principal,
            loginSource,
            SemanticGatewayTestRealm.TempestGrant);
        Check.True(
            login.IsStarted,
            "authenticated login starts one gateway generation");
        Check.True(
            authority.TryFindLogin(
                "TEST",
                loginSource.Address!).Status ==
                SemanticGatewayLoginLookupStatus.NotActivated,
            "pending login is not game-admissible before redirect");
        Check.True(
            authority.Reserve(
                login.Generation!.GenerationId,
                principal,
                Source(),
                Target(Sparta, SpartaInstance)).Status ==
                SemanticGatewayAdmissionStatus.GenerationNotActivated,
            "pending login cannot reserve worker capacity");
        Check.True(
            !authority.ActivateLogin(
                login.Generation! with
                {
                    RealmGrant =
                        SemanticGatewayTestRealm.DwargonGrant
                }),
            "tampering with the selected realm grant cannot activate login");
        Check.True(
            authority.ActivateLogin(login.Generation),
            "redirect boundary activates the pending login");
        var lookup = authority.TryFindLogin(
            "TEST",
            loginSource.Address!);
        Check.True(
            lookup.IsFound &&
            lookup.Generation!.GenerationId ==
                login.Generation!.GenerationId &&
            lookup.Generation.Principal == principal &&
            lookup.Generation.RealmGrant ==
                SemanticGatewayTestRealm.TempestGrant,
            "username plus normalized address finds exact generation");
        Check.True(
            authority.TryFindLogin(
                "TEST",
                System.Net.IPAddress.Parse("192.0.2.99")).Status ==
                SemanticGatewayLoginLookupStatus.SourceAddressMismatch,
            "source IP is an exact defense-in-depth lookup binding");
        Check.True(
            authority.TryFindLogin(
                "OTHER",
                loginSource.Address!).Status ==
                SemanticGatewayLoginLookupStatus.NotFound,
            "gateway exposes no address-only generation lookup");

        var loginLease = login.Generation!;
        var gameSource = Source();
        var reserved = authority.Reserve(
            loginLease.GenerationId,
            principal,
            gameSource,
            Target(Sparta, SpartaInstance));
        Check.True(
            reserved.Status == SemanticGatewayAdmissionStatus.Reserved,
            "valid generation reserves its exact world route");
        Check.True(
            authority.Reserve(
                loginLease.GenerationId,
                principal,
                Source(),
                Target(Sparta, SpartaInstance)).Status ==
                SemanticGatewayAdmissionStatus
                    .GenerationCapacityExceeded,
            "per-generation admission bound is enforced");

        var wrongPrincipalClaim = Claim(reserved.Admission!) with
        {
            Principal = new SemanticGatewayPrincipal(8, "OTHER")
        };
        Check.True(
            authority.Commit(wrongPrincipalClaim).Status ==
                SemanticGatewayAdmissionStatus.BindingMismatch,
            "account and canonical username are part of admission identity");
        var wrongSourceClaim = Claim(reserved.Admission!) with
        {
            Source = Source("192.0.2.88")
        };
        Check.True(
            authority.Commit(wrongSourceClaim).Status ==
                SemanticGatewayAdmissionStatus.BindingMismatch,
            "connection ID and normalized source bind the admission");
        var wrongNodeClaim = Claim(reserved.Admission!) with
        {
            NodeId = NodeB
        };
        Check.True(
            authority.Commit(wrongNodeClaim).Status ==
                SemanticGatewayAdmissionStatus.BindingMismatch,
            "worker node is part of the admission claim");

        var committed = authority.Commit(Claim(reserved.Admission!));
        Check.True(
            committed.Status ==
                SemanticGatewayAdmissionStatus.Committed &&
            committed.Admission!.State ==
                SemanticGatewayAdmissionState.Committed,
            "exact reserved admission commits once");
        var committedLease = committed.Admission!;
        Check.True(
            authority.Commit(Claim(committedLease)).Status ==
                SemanticGatewayAdmissionStatus.StateConflict,
            "committed admission cannot commit twice");
        Check.True(
            authority.ResolveCommitted(
                Claim(committedLease)).Status ==
                SemanticGatewayAdmissionStatus.Committed,
            "committed admission resolves only by its full claim");
        Check.True(
            authority.RefreshCommitted(
                Claim(committedLease)).Status ==
                SemanticGatewayAdmissionStatus.Refreshed,
            "active committed admission receives a finite refresh");

        var replacement = authority.BeginLogin(
            principal,
            Source(),
            SemanticGatewayTestRealm.TempestGrant);
        Check.True(
            replacement.IsStarted &&
            replacement.InvalidatedAdmissions == 1,
            "duplicate login generation invalidates old admissions");
        Check.True(
            authority.ResolveCommitted(
                Claim(committedLease)).Status ==
                SemanticGatewayAdmissionStatus.AdmissionNotFound,
            "superseded admission cannot be replayed");
        var replacedSnapshot = authority.GetSnapshot();
        Check.Equal(
            1,
            replacedSnapshot.ActiveLoginGenerations,
            "duplicate login leaves one active generation");
        Check.Equal(
            0,
            replacedSnapshot.CommittedAdmissions,
            "duplicate login releases committed worker capacity");
        Check.True(
            authority.ActivateLogin(replacement.Generation!),
            "replacement redirect activates its login generation");

        Check.True(
            authority.BeginLogin(
                new SemanticGatewayPrincipal(8, "TEST"),
                Source(),
                SemanticGatewayTestRealm.TempestGrant).Status ==
                SemanticGatewayLoginStatus.IdentityConflict,
            "canonical username cannot bind to another account");
        Check.True(
            authority.BeginLogin(
                new SemanticGatewayPrincipal(7, "RENAMED"),
                Source(),
                SemanticGatewayTestRealm.TempestGrant).Status ==
                SemanticGatewayLoginStatus.IdentityConflict,
            "active account cannot bind another canonical username");

        var rollbackReservation = authority.Reserve(
            replacement.Generation!.GenerationId,
            principal,
            Source(),
            Target(Sparta, SpartaInstance));
        Check.True(
            authority.Rollback(
                Claim(rollbackReservation.Admission!)).Status ==
                SemanticGatewayAdmissionStatus.RolledBack,
            "reserved admission rollback frees route capacity");
        Check.True(
            authority.Reserve(
                replacement.Generation.GenerationId,
                principal,
                Source(),
                Target(Sparta, SpartaInstance)).Status ==
                SemanticGatewayAdmissionStatus
                    .GenerationCapacityExceeded,
            "rollback does not make a single-use generation reusable");
        var releaseGeneration = authority.BeginLogin(
            principal,
            Source(),
            SemanticGatewayTestRealm.TempestGrant);
        Check.True(
            releaseGeneration.IsStarted,
            "full login creates a generation for the next session");
        Check.True(
            authority.ActivateLogin(releaseGeneration.Generation!),
            "next full login activates at its redirect boundary");
        var releaseReservation = authority.Reserve(
            releaseGeneration.Generation!.GenerationId,
            principal,
            Source(),
            Target(Sparta, SpartaInstance));
        var releaseCommit = authority.Commit(
            Claim(releaseReservation.Admission!));
        Check.True(
            authority.Release(
                Claim(releaseCommit.Admission!)).Status ==
                SemanticGatewayAdmissionStatus.Released,
            "committed session release creates no reusable authority");
        Check.Equal(
            0,
            authority.GetSnapshot().Routes.ActiveReservations,
            "all rollback/release paths settle route accounting");
    }

    private static void CheckAuthorityExpiryAndBounds()
    {
        var time = new ManualTimeProvider();
        var authority = new SemanticGatewayAdmissionAuthority(
            CreateDirectory(),
            new SemanticGatewayAuthorityLimits(
                maximumLoginGenerations: 2,
                maximumAdmissions: 2,
                maximumAdmissionsPerGeneration: 1,
                maximumExpiryWorkPerOperation: 1,
                loginGenerationTtl: TimeSpan.FromSeconds(3),
                reservationTtl: TimeSpan.FromSeconds(1),
                committedAdmissionTtl: TimeSpan.FromSeconds(2)),
            time);
        var firstPrincipal =
            new SemanticGatewayPrincipal(1, "FIRST");
        var first = authority.BeginLogin(
            firstPrincipal,
            Source(),
            SemanticGatewayTestRealm.TempestGrant);
        Check.True(
            authority.ActivateLogin(first.Generation!),
            "expiry fixture activates its first login");
        var reservation = authority.Reserve(
            first.Generation!.GenerationId,
            firstPrincipal,
            Source(),
            Target(Sparta, SpartaInstance));
        time.Advance(TimeSpan.FromSeconds(2));
        Check.True(
            authority.Commit(Claim(reservation.Admission!)).Status ==
                SemanticGatewayAdmissionStatus.AdmissionExpired,
            "expired reservation cannot commit");

        var second = authority.BeginLogin(
            new SemanticGatewayPrincipal(2, "SECOND"),
            Source(),
            SemanticGatewayTestRealm.TempestGrant);
        Check.True(second.IsStarted, "second bounded generation starts");
        time.Advance(TimeSpan.FromSeconds(2));
        Check.True(
            authority.TryFindLogin(
                "FIRST",
                first.Generation.LoginSource.Address!).Status ==
                SemanticGatewayLoginLookupStatus.Expired,
            "exact generation lookup reports and removes expiry");
        time.Advance(TimeSpan.FromSeconds(2));
        var once = authority.GetSnapshot();
        var twice = authority.GetSnapshot();
        Check.True(
            once.ActiveLoginGenerations is >= 0 and <= 1 &&
            twice.ActiveLoginGenerations == 0,
            "bounded one-record sweeps eventually clear all expired state");
        Check.Equal(
            0,
            twice.Routes.ActiveReservations,
            "expiry always settles route reservations");

        var capacity = new SemanticGatewayAdmissionAuthority(
            CreateDirectory(),
            new SemanticGatewayAuthorityLimits(
                maximumLoginGenerations: 1,
                maximumAdmissions: 1),
            new ManualTimeProvider());
        Check.True(
            capacity.BeginLogin(
                new SemanticGatewayPrincipal(11, "ONE"),
                Source(),
                SemanticGatewayTestRealm.TempestGrant).IsStarted,
            "authority fills its generation capacity");
        Check.True(
            capacity.BeginLogin(
                new SemanticGatewayPrincipal(12, "TWO"),
                Source(),
                SemanticGatewayTestRealm.TempestGrant).Status ==
                SemanticGatewayLoginStatus.CapacityExceeded,
            "generation capacity rejects additional identities");
    }

    private static void CheckConcurrentDuplicateLogin()
    {
        var authority = new SemanticGatewayAdmissionAuthority(
            CreateDirectory(),
            new SemanticGatewayAuthorityLimits(
                maximumLoginGenerations: 8,
                maximumAdmissions: 8),
            new ManualTimeProvider());
        var principal =
            new SemanticGatewayPrincipal(77, "CONCURRENT");
        var results = new SemanticGatewayLoginResult[64];
        Parallel.For(
            0,
            results.Length,
            index =>
            {
                results[index] = authority.BeginLogin(
                    principal,
                    Source($"192.0.2.{20 + index % 100}"),
                    SemanticGatewayTestRealm.TempestGrant);
            });
        Check.True(
            results.All(static result => result.IsStarted),
            "concurrent duplicate logins serialize without partial state");
        var snapshot = authority.GetSnapshot();
        Check.Equal(
            1,
            snapshot.ActiveLoginGenerations,
            "concurrent duplicate login has exactly one final generation");
        Check.Equal(
            63L,
            snapshot.LoginGenerationsSuperseded,
            "every prior concurrent generation is invalidated exactly once");
    }

}
