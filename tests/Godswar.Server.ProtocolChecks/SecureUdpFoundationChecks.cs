using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpFoundationChecks
{
    public static Task RunAsync()
    {
        CheckOptions();
        SecureUdpBindingCodecChecks.Run();
        SecureUdpCookieChecks.Run();
        return Task.CompletedTask;
    }

    private static void CheckOptions()
    {
        var options = new SecureUdpOptions();
        options.NormalizeAndValidate(5999, 7000, 6599, 7443);
        Check.True(!options.Enabled, "Slice 9A UDP defaults disabled");
        Check.Equal(7444, options.Port, "reserved UDP port");
        Check.Equal(1200, options.MaximumDatagramBytes, "path-MTU ceiling");
        Check.Equal(10, options.CookieLifetimeSeconds, "cookie lifetime");
        Check.Equal(60, options.CookieKeyRotationSeconds, "key rotation");

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

        CheckInvalid(
            WithUdp(static udp => udp.Enabled = true),
            "Slice 9A activation");
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
