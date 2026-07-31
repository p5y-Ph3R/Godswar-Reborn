using System.Net;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Networking;
using Godswar.Server.Networking.SemanticGateway;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SemanticGatewayChecks
{
    private static async Task
        CheckGenerationAwareRelayReplacementAsync()
    {
        var authority = CreateLoginAuthority();
        var principal = new SemanticGatewayPrincipal(7, "TEST");
        var generationA = authority.BeginLogin(principal, Source());
        Check.True(
            generationA.IsStarted &&
            authority.ActivateLogin(generationA.Generation!),
            "generation A activates for relay coordination");
        var generationB = authority.BeginLogin(principal, Source());
        Check.True(
            generationB.IsStarted &&
            authority.ActivateLogin(generationB.Generation!),
            "newer generation B activates for relay coordination");
        Check.True(
            generationA.Generation!.Sequence <
                generationB.Generation!.Sequence,
            "login authority assigns an ordered generation sequence");

        using var connections =
            new SemanticGatewayConnectionCoordinator(
                maximumConnections: 4,
                replacementTimeout: TimeSpan.FromSeconds(1));
        var leaseA =
            await connections.AcquireAsync(
                principal.AccountId,
                generationA.Generation.GenerationId,
                generationA.Generation.Sequence,
                GatewayConnectionId.New()) ??
            throw new InvalidOperationException(
                "Generation A did not acquire its relay.");
        using (leaseA)
        {
            var acquireB = connections.AcquireAsync(
                    principal.AccountId,
                    generationB.Generation.GenerationId,
                    generationB.Generation.Sequence,
                    GatewayConnectionId.New())
                .AsTask();
            Check.True(
                leaseA.ReplacementToken.IsCancellationRequested,
                "newer generation B requests generation A shutdown");
            leaseA.Dispose();

            var leaseB = await acquireB ??
                throw new InvalidOperationException(
                    "Generation B did not acquire its relay.");
            Check.True(
                leaseB.GenerationId ==
                generationB.Generation.GenerationId,
                "generation B acquires ownership after A releases");
            using (leaseB)
            {
                Check.True(
                    !connections.RequestReplacement(
                        principal.AccountId,
                        generationA.Generation.GenerationId,
                        generationA.Generation.Sequence),
                    "delayed generation A replacement cannot cancel B");
                Check.True(
                    !connections.RequestStop(
                        principal.AccountId,
                        generationA.Generation.GenerationId),
                    "generation A exact stop cannot cancel B");
                Check.True(
                    !leaseB.ReplacementToken.IsCancellationRequested,
                    "newer relay B remains live after stale A requests");

                var staleAcquire = await connections.AcquireAsync(
                    principal.AccountId,
                    generationA.Generation.GenerationId,
                    generationA.Generation.Sequence,
                    GatewayConnectionId.New());
                Check.True(
                    staleAcquire is null &&
                    !leaseB.ReplacementToken.IsCancellationRequested,
                    "stale generation A cannot reacquire over B");
                Check.True(
                    connections.RequestStop(
                        principal.AccountId,
                        generationB.Generation.GenerationId) &&
                    leaseB.ReplacementToken.IsCancellationRequested,
                    "matching cancellation stops only generation B");
            }
        }
    }

    private static async Task
        CheckRedirectFailureCancelsMatchingRelayAsync()
    {
        var authority = CreateLoginAuthority();
        using var connections =
            new SemanticGatewayConnectionCoordinator(
                maximumConnections: 4,
                replacementTimeout: TimeSpan.FromSeconds(1));
        await using var data =
            new LoginHandlerDataSession(
                new SemanticGatewayAuthenticatedAccount(7, "TEST"));
        var transport = new RedirectFailingLegacyByteTransport(
            EncryptLoginStream(
                "TEST",
                "password",
                Opcodes.LoginReturnInfo));
        SemanticGatewayConnectionCoordinator
            .SemanticGatewayConnectionLease? matchingRelay = null;
        SemanticGatewayLoginGenerationLease? newerGeneration = null;
        transport.BeforeRedirectFailure = async () =>
        {
            var activated = authority.TryFindLogin(
                "TEST",
                IPAddress.Loopback);
            Check.True(
                activated.IsFound && activated.Generation is not null,
                "redirect path activates before publishing redirect bytes");
            var generation = activated.Generation ??
                throw new InvalidOperationException(
                    "Activated redirect generation was unavailable.");
            matchingRelay = await connections.AcquireAsync(
                generation.Principal.AccountId,
                generation.GenerationId,
                generation.Sequence,
                GatewayConnectionId.New());
            Check.True(
                matchingRelay is not null,
                "failure fixture installs the matching generation relay");
            var replacement = authority.BeginLogin(
                generation.Principal,
                new SemanticGatewayConnectionSource(
                    GatewayConnectionId.New(),
                    IPAddress.Loopback));
            Check.True(
                replacement.IsStarted &&
                authority.ActivateLogin(replacement.Generation!),
                "newer generation supersedes A before its send fails");
            newerGeneration = replacement.Generation;
        };

        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login);
        var handler = new SemanticGatewayLoginHandler(
            session,
            data,
            authority,
            connections,
            "127.0.0.1",
            41006);
        var failed = false;
        try
        {
            await handler.RunAsync(CancellationToken.None);
        }
        catch (IOException)
        {
            failed = true;
        }

        try
        {
            Check.True(
                failed,
                "redirect transport failure propagates to endpoint policy");
            var current = authority.TryFindLogin(
                "TEST",
                IPAddress.Loopback);
            var expectedNewer = newerGeneration ??
                throw new InvalidOperationException(
                    "Newer redirect generation was not installed.");
            Check.True(
                current.IsFound &&
                current.Generation?.GenerationId ==
                    expectedNewer.GenerationId,
                "stale redirect failure preserves newer authority");
            Check.True(
                matchingRelay is not null &&
                matchingRelay.ReplacementToken.IsCancellationRequested,
                "stale redirect failure stops only its matching relay");
        }
        finally
        {
            matchingRelay?.Dispose();
        }
    }

    private sealed class RedirectFailingLegacyByteTransport(
        byte[] inbound) :
        ILegacyByteTransport
    {
        private int _offset;
        private int _writes;

        public Func<Task>? BeforeRedirectFailure { get; set; }

        public string RemoteEndPoint => "127.0.0.1:41007";

        public ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset >= inbound.Length)
            {
                return ValueTask.FromResult(0);
            }

            var count = Math.Min(
                destination.Length,
                inbound.Length - _offset);
            inbound.AsMemory(_offset, count).CopyTo(destination);
            _offset += count;
            return ValueTask.FromResult(count);
        }

        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _writes) != 2)
            {
                return;
            }

            if (BeforeRedirectFailure is not null)
            {
                await BeforeRedirectFailure();
            }
            throw new IOException("Deterministic redirect write failure.");
        }

        public void Disconnect()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
