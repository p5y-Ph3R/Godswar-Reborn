using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeMovementProtocolChecks
{
    private static void CheckFeatureDefaultsAndDatagramBudget()
    {
        Check.True(
            !new SecureUdpOptions().GameplayMovementEnabled,
            "authoritative movement is disabled by default");
        var undersized = new SecureUdpOptions
        {
            GameplayMovementEnabled = true,
            MaximumDatagramBytes =
                SecureUdpBindingConstants.DatagramBytes
        };
        Check.Throws<InvalidDataException>(
            () => undersized.NormalizeAndValidate(
                rawLoginPort: 5_999,
                rawGamePort: 6_443,
                tlsLoginPort: 6_599,
                tlsGamePort: 7_443),
            "movement snapshot requires its bounded datagram budget");
    }
}
