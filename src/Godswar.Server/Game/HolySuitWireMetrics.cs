using System.Diagnostics.Metrics;

namespace Godswar.Server.Game;

internal static class HolySuitWireMetrics
{
    public const string MeterName = "Godswar.Server.HolySuitWire";
    public const string InstrumentName =
        "godswar_holy_suit_wire_events_total";

    private const string EventTag = "event";
    private const string OperationTag = "operation";
    private const string ReasonTag = "reason";
    private const string ArgumentTag = "argument";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Events =
        Meter.CreateCounter<long>(
            InstrumentName,
            "{event}",
            "Holy Suit stock-client wire events by bounded classification.");

    public static void RecordNavigation(HolySuitWireOperation operation) =>
        Record("navigation", operation, default);

    public static void RecordMutation(HolySuitWireOperation operation) =>
        Record("mutation", operation, default);

    public static void RecordRejected(
        HolySuitWireOperation operation,
        HolySuitWireRejection rejection) =>
        Record("rejected", operation, rejection);

    private static void Record(
        string eventCode,
        HolySuitWireOperation operation,
        HolySuitWireRejection rejection)
    {
        Events.Add(
            1,
            new KeyValuePair<string, object?>(EventTag, eventCode),
            new KeyValuePair<string, object?>(
                OperationTag,
                OperationCode(operation)),
            new KeyValuePair<string, object?>(
                ReasonTag,
                RejectionCode(rejection.Reason)),
            new KeyValuePair<string, object?>(
                ArgumentTag,
                ArgumentCode(rejection.ArgumentIndex)));
    }

    private static string OperationCode(HolySuitWireOperation operation) =>
        operation switch
        {
            HolySuitWireOperation.StoreExperience => "store_experience",
            HolySuitWireOperation.TransferExperience =>
                "transfer_experience",
            HolySuitWireOperation.ConsumeWare => "consume_ware",
            HolySuitWireOperation.TransformExperience =>
                "transform_experience",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static string RejectionCode(
        HolySuitWireRejectionReason reason) =>
        reason switch
        {
            HolySuitWireRejectionReason.None => "none",
            HolySuitWireRejectionReason.ActionShape => "action_shape",
            HolySuitWireRejectionReason.UnknownOperation =>
                "unknown_operation",
            HolySuitWireRejectionReason.InvalidItemReference =>
                "invalid_item_reference",
            HolySuitWireRejectionReason.MissingAmount => "missing_amount",
            HolySuitWireRejectionReason.DuplicateItemReference =>
                "duplicate_item_reference",
            HolySuitWireRejectionReason.UnexpectedArgument =>
                "unexpected_argument",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };

    private static string ArgumentCode(int argumentIndex) =>
        argumentIndex is >= 0 and <
            HolySuitDesignProtocol.FunctionArgumentCount
                ? $"arg_{argumentIndex}"
                : "none";
}
