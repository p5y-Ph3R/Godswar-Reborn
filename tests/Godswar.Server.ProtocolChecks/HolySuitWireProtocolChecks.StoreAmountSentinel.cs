using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySuitWireProtocolChecks
{
    private static void CheckStoreAmountSentinel()
    {
        var blankStoreAmount = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.StoreExperienceSubId,
            args =>
            {
                args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 0;
                args[HolySuitDesignProtocol.AmountArgumentIndex] = -1;
            });
        Check.True(
            HolySuitDesignProtocol.TryReadMutation(
                blankStoreAmount,
                out _,
                out _,
                out var intent) &&
            intent.Amount == 0,
            "stock blank Store amount -1 requests authoritative maximum");

        var typedZeroAmount = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.StoreExperienceSubId,
            args =>
            {
                args[HolySuitDesignProtocol.FirstItemArgumentIndex] = 0;
                args[HolySuitDesignProtocol.AmountArgumentIndex] = 0;
            });
        Check.True(
            !HolySuitDesignProtocol.TryReadMutation(
                typedZeroAmount,
                out _,
                out _,
                out _,
                out var rejection) &&
            rejection.Reason == HolySuitWireRejectionReason.MissingAmount,
            "typed Store amount zero remains invalid");
    }
}
