using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySuitWireProtocolChecks
{
    private static void CheckDisplayCounterLimits()
    {
        Check.True(
            HolySuitDesignProtocol.TryEncodeCounter(
                HolySuitDesignProtocol.MaximumEncodedCounter,
                HolySuitDesignProtocol.UpdatedTransferredCounterSuffix,
                out _),
            "largest dynamic client counter is encodable");
        Check.True(
            !HolySuitDesignProtocol.TryEncodeCounter(
                HolySuitDesignProtocol.MaximumEncodedCounter + 1,
                HolySuitDesignProtocol.UpdatedTransferredCounterSuffix,
                out _) &&
            !HolySuitDesignProtocol.TryEncodeCounter(-1, 4, out _),
            "dynamic counters reject overflow and negative values");
        Check.Equal(
            HolySuitDesignProtocol.MaximumEncodedCounter,
            HolySuitDesignProtocol.ClampDisplayCounter(long.MaxValue),
            "stock-client quota display clamps authoritative large values");
        Check.Equal(
            HolySuitDesignProtocol.MaximumEncodedCounter,
            HolySuitDesignProtocol.ClampDisplayCounter(2_000_000_000),
            "2b backend daily cap saturates only the stock UI counter");
        Check.Throws<ArgumentOutOfRangeException>(
            () => HolySuitDesignProtocol.ClampDisplayCounter(-1),
            "stock-client quota display rejects negative values");
    }
}
