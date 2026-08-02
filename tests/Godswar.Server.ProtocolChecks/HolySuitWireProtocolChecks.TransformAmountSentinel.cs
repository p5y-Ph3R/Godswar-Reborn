using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySuitWireProtocolChecks
{
    private static void CheckTransformAmountSentinel()
    {
        var mouseOnlyConfirmation = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.TransformExperienceSubId,
            args => args[0] = 0);
        Check.True(
            HolySuitDesignProtocol.TryReadMutation(
                mouseOnlyConfirmation,
                out _,
                out _,
                out var intent) &&
            intent.Amount ==
                HolySuitDesignProtocol.MouseOnlyTransformPrismCount,
            "stock blank Transform amount uses the displayed 20-prism default");

        var navigation = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.TransformExperienceSubId,
            static _ => { });
        Check.True(
            HolySuitDesignProtocol.IsExactNavigation(
                navigation,
                HolySuitDesignProtocol.TransformExperienceSubId) &&
            !HolySuitDesignProtocol.TryReadMutation(
                navigation,
                out _,
                out _,
                out _),
            "all-minus-one Transform navigation cannot spend EXP");

        var typedAmount = CreatePacket(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.TransformExperienceSubId,
            args =>
            {
                args[0] = 0;
                args[HolySuitDesignProtocol.AmountArgumentIndex] = 7;
            });
        Check.True(
            HolySuitDesignProtocol.TryReadMutation(
                typedAmount,
                out _,
                out _,
                out var typedIntent) &&
            typedIntent.Amount == 7,
            "typed Transform prism count remains authoritative");

        foreach (var invalidAmount in new[] { 0, -2 })
        {
            var invalid = CreatePacket(
                HolySuitDesignProtocol.SpartaNpcId,
                HolySuitDesignProtocol.TransformExperienceSubId,
                args =>
                {
                    args[0] = 0;
                    args[HolySuitDesignProtocol.AmountArgumentIndex] =
                        invalidAmount;
                });
            Check.True(
                !HolySuitDesignProtocol.TryReadMutation(
                    invalid,
                    out _,
                    out _,
                    out _,
                    out var rejection) &&
                rejection.Reason == HolySuitWireRejectionReason.MissingAmount,
                $"Transform amount {invalidAmount} remains invalid");
        }
    }
}
