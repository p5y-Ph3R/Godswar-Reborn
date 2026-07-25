using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpFoundationChecks
{
    public static async Task RunAsync()
    {
        CheckOptions();
        SecureUdpBindingCodecChecks.Run();
        SecureUdpBindingGrantChecks.Run();
        SecureUdpCookieChecks.Run();
        SecureUdpProtectedCodecChecks.Run();
        SecureUdpReplayWindowChecks.Run();
        SecureUdpProtectedSessionChecks.Run();
        await SecureUdpSessionAuthorityChecks.RunAsync();
        await SecureUdpEndpointServerChecks.RunAsync();
        await SecureUdpRuntimeChecks.RunAsync();
    }

    private static void CheckOptions()
    {
        var options = new SecureUdpOptions();
        options.NormalizeAndValidate(5999, 7000, 6599, 7443);
        Check.True(!options.Enabled, "Slice 9B UDP defaults disabled");
        Check.Equal(7444, options.Port, "reserved UDP port");
        Check.Equal(1200, options.MaximumDatagramBytes, "path-MTU ceiling");
        Check.Equal(10, options.CookieLifetimeSeconds, "cookie lifetime");
        Check.Equal(60, options.CookieKeyRotationSeconds, "key rotation");
        Check.Equal(1_024, options.SessionCapacity, "UDP session capacity");
        Check.Equal(
            30,
            options.BindingOfferTtlSeconds,
            "UDP binding-offer TTL");
        Check.Equal(
            3_072,
            options.UnvalidatedPacketsPerSecond,
            "unvalidated admission limit");
        Check.Equal(
            512,
            options.BindingProofPacketsPerSecond,
            "binding-proof admission limit");
        Check.Equal(
            512,
            options.ProtectedCandidatePacketsPerSecond,
            "pre-auth protected-candidate admission limit");
        Check.Equal(
            256,
            options.ProtectedCandidatePrefixPacketsPerSecond,
            "pre-auth protected-candidate prefix limit");
        Check.Equal(
            256,
            options.AuthenticatedSessionPacketsPerSecond,
            "per-session authenticated admission limit");

        var mappedLoopback = WithUdp(
            static udp => udp.BindHost = "::ffff:127.0.0.1");
        mappedLoopback.NormalizeAndValidate(
            5999,
            7000,
            6599,
            7443);
        Check.Equal(
            "::ffff:127.0.0.1",
            mappedLoopback.BindHost,
            "IPv4-mapped loopback development bind");

        var optIn = WithUdp(static udp => udp.Enabled = true);
        optIn.NormalizeAndValidate(5999, 7000, 6599, 7443);
        Check.True(optIn.Enabled, "UDP option permits guarded opt-in");
        CheckInvalid(
            WithUdp(static udp => udp.Port = 7443),
            "TCP port collision");
        CheckInvalid(
            WithUdp(static udp => udp.BindHost = "8.8.8.8"),
            "public development bind");
        CheckInvalid(
            WithUdp(static udp => udp.MaximumDatagramBytes = 1201),
            "oversized path MTU");
        CheckInvalid(
            WithUdp(static udp => udp.CookieLifetimeSeconds = 4),
            "short cookie lifetime");
        CheckInvalid(
            WithUdp(static udp =>
                udp.CookieKeyRotationSeconds = 15),
            "insufficient rotation overlap");
        CheckInvalid(
            WithUdp(static udp => udp.SessionCapacity = 0),
            "zero UDP session capacity");
        CheckInvalid(
            WithUdp(static udp => udp.BindingOfferTtlSeconds = 121),
            "long UDP binding-offer TTL");
        CheckInvalid(
            WithUdp(static udp =>
                udp.PrefixPacketsPerSecond =
                    udp.UnvalidatedPacketsPerSecond + 1),
            "UDP prefix limit above unvalidated limit");
        CheckInvalid(
            WithUdp(static udp =>
                udp.BindingProofPacketsPerSecond =
                    udp.GlobalPacketsPerSecond -
                    udp.UnvalidatedPacketsPerSecond),
            "UDP pre-auth classes exceed the global admission envelope");
        CheckInvalid(
            WithUdp(static udp =>
                udp.ProtectedCandidatePrefixPacketsPerSecond =
                    udp.ProtectedCandidatePacketsPerSecond + 1),
            "UDP protected-candidate prefix limit above class limit");

        var tlsDisabled = new SecureNetworkOptions();
        tlsDisabled.Udp.Enabled = true;
        Check.Throws<InvalidDataException>(
            () => tlsDisabled.NormalizeAndValidate(
                "appsettings.json",
                rawLoginPort: 5_999,
                rawGamePort: 7_000),
            "UDP cannot activate without secure TLS");
    }

    private static SecureUdpOptions WithUdp(
        Action<SecureUdpOptions> mutate)
    {
        var value = new SecureUdpOptions();
        mutate(value);
        return value;
    }

    private static void CheckInvalid(
        SecureUdpOptions options,
        string description)
    {
        Check.Throws<InvalidDataException>(
            () => options.NormalizeAndValidate(
                5999,
                7000,
                6599,
                7443),
            description);
    }
}
