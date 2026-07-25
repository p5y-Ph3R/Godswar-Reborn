using System.Diagnostics;
using System.Net;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpAdmissionBaselineChecks
{
    private const int MaximumAttempts = 16_000;
    private static readonly TimeSpan MaximumDuration =
        TimeSpan.FromSeconds(2);

    public static Task RunAsync()
    {
        var time = new ManualTimeProvider();
        time.Advance(TimeSpan.FromSeconds(1));
        var limiter = new SecureUdpRateLimiter(
            globalLimit: 10_000,
            unvalidatedLimit: 6_000,
            prefixLimit: 6_000,
            prefixCapacity: 16,
            bindingProofLimit: 2_000,
            bindingProofPrefixLimit: 2_000,
            protectedCandidateLimit: 2_000,
            protectedCandidatePrefixLimit: 2_000,
            authenticatedSessionLimit: 32,
            authenticatedSessionCapacity: 128,
            time);
        var loopback = IPAddress.Loopback;
        var accepted = 0;
        var rejected = 0;
        var attempted = 0;
        var stopwatch = Stopwatch.StartNew();

        RunBounded(
            6_000,
            () => limiter.TryAcquireUnvalidated(loopback));
        RunBounded(
            2_000,
            () => limiter.TryAcquireBindingProof(loopback));
        RunBounded(
            4_000,
            () => limiter.TryAcquireProtectedCandidate(loopback));
        var sessionIndex = 0;
        RunBounded(
            4_000,
            () =>
            {
                var connectionId = new SecureUdpConnectionKey(
                    High: 0xA500000000000000,
                    Low: checked((ulong)(sessionIndex++ % 128 + 1)));
                return limiter.TryAcquireAuthenticatedSession(
                    connectionId);
            });
        stopwatch.Stop();

        var snapshot = limiter.GetSnapshot();
        Check.True(
            attempted <= MaximumAttempts &&
            accepted + rejected == attempted &&
            snapshot.CurrentPackets ==
                snapshot.UnvalidatedPackets +
                snapshot.BindingProofPackets +
                snapshot.ProtectedCandidatePackets &&
            snapshot.CurrentPackets <= snapshot.GlobalLimit &&
            snapshot.UnvalidatedPackets <=
                snapshot.UnvalidatedLimit &&
            snapshot.BindingProofPackets <=
                snapshot.BindingProofLimit &&
            snapshot.ProtectedCandidatePackets <=
                snapshot.ProtectedCandidateLimit &&
            snapshot.ActivePrefixes <=
                snapshot.PrefixCapacity &&
            snapshot.ActiveBindingProofPrefixes <=
                snapshot.PrefixCapacity &&
            snapshot.ActiveProtectedCandidatePrefixes <=
                snapshot.PrefixCapacity &&
            snapshot.ActiveAuthenticatedSessions <= 128,
            "bounded UDP admission baseline retains finite state");

        Console.WriteLine(
            "UDP_BASELINE " +
            $"mode=in-process-loopback " +
            $"attempted={attempted} accepted={accepted} " +
            $"rejected={rejected} elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F3} " +
            $"global={snapshot.CurrentPackets}/{snapshot.GlobalLimit} " +
            $"unvalidated={snapshot.UnvalidatedPackets}/{snapshot.UnvalidatedLimit} " +
            $"proof={snapshot.BindingProofPackets}/{snapshot.BindingProofLimit} " +
            $"protected_candidates={snapshot.ProtectedCandidatePackets}/" +
            $"{snapshot.ProtectedCandidateLimit} " +
            $"auth_sessions={snapshot.ActiveAuthenticatedSessions}/128 " +
            $"hard_packet_cap={MaximumAttempts} " +
            $"hard_duration_ms={MaximumDuration.TotalMilliseconds:F0}");
        return Task.CompletedTask;

        void RunBounded(int count, Func<bool> attempt)
        {
            for (var index = 0;
                 index < count &&
                 attempted < MaximumAttempts &&
                 stopwatch.Elapsed < MaximumDuration;
                 index++)
            {
                attempted++;
                if (attempt())
                {
                    accepted++;
                }
                else
                {
                    rejected++;
                }
            }
        }
    }
}
