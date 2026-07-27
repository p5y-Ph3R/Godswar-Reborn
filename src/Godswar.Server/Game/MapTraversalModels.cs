namespace Godswar.Server.Game;

internal enum MapTraversalClassification : byte
{
    City = 0,
    CoreWorld = 1,
    SpecialInstance = 2
}

internal enum MapTraversalEvidenceConfidence : byte
{
    CapturedSpanMap = 0,
    ReciprocalAddressPoint = 1,
    ExcludedByObservedTopology = 2
}

internal enum MapTraversalActivation : byte
{
    Automatic = 0,
    DisabledByWorldTopology = 1
}

internal static class MapTraversalLimits
{
    public const float MaximumCoordinateMagnitude = 4096f;
    public const float MinimumTriggerRadius = 0.25f;
    public const float MaximumTriggerRadius = 12f;
    public const float MaximumAcceptedSegmentLength = 96f;
    public const float MinimumAcceptedSegmentLength = 0.001f;
    public const float ArrivalClearance = 4f;

    public static bool IsFiniteAndBounded(in MapTraversalPosition position) =>
        float.IsFinite(position.X) &&
        float.IsFinite(position.Z) &&
        MathF.Abs(position.X) <= MaximumCoordinateMagnitude &&
        MathF.Abs(position.Z) <= MaximumCoordinateMagnitude;

    public static bool IsValidTriggerRadius(float radius) =>
        float.IsFinite(radius) &&
        radius >= MinimumTriggerRadius &&
        radius <= MaximumTriggerRadius;
}

internal readonly record struct MapTraversalPosition(float X, float Z);

internal sealed record MapTraversalMap(
    short MapId,
    string SceneKey,
    string DisplayName,
    int? ClientSceneId,
    MapTraversalClassification Classification,
    MapTraversalPosition Center);

internal sealed record MapTraversalLinkEvidence(
    short SourceMapId,
    short TargetMapId,
    MapTraversalPosition Portal,
    string Source,
    MapTraversalEvidenceConfidence Confidence,
    MapTraversalActivation Activation,
    string Note);

internal sealed record MapTraversalArrivalEvidence(
    short SourceMapId,
    short TargetMapId,
    MapTraversalPosition Arrival,
    string Source,
    MapTraversalEvidenceConfidence Confidence,
    string Note);

/// <summary>
/// A movement segment supplied only after the authoritative movement system
/// accepts both endpoints in the same map generation.
/// </summary>
internal readonly record struct AcceptedMapMovementSegment(
    short MapId,
    MapTraversalPosition Start,
    MapTraversalPosition End);

internal sealed record MapTraversalResolution(
    short SourceMapId,
    short TargetMapId,
    MapTraversalPosition SourcePortal,
    MapTraversalPosition TargetPortal,
    MapTraversalPosition TargetArrival,
    float TriggerRadius,
    string Source,
    MapTraversalEvidenceConfidence Confidence);
