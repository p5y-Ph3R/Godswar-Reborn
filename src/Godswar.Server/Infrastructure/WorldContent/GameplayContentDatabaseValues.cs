using Godswar.Server.Application.World;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class GameplayContentDatabaseValues
{
    public static GameplayMapLinkConfidence ParseConfidence(string value) =>
        value switch
        {
            "captured-span-map" =>
                GameplayMapLinkConfidence.CapturedSpanMap,
            "reciprocal-address-point" =>
                GameplayMapLinkConfidence.ReciprocalAddressPoint,
            "excluded-by-observed-topology" =>
                GameplayMapLinkConfidence.ExcludedByObservedTopology,
            _ => throw new InvalidDataException(
                $"Unknown gameplay map-link confidence '{value}'.")
        };

    public static GameplayMapLinkActivation ParseActivation(string value) =>
        value switch
        {
            "automatic" => GameplayMapLinkActivation.Automatic,
            "disabled-by-world-topology" =>
                GameplayMapLinkActivation.DisabledByWorldTopology,
            _ => throw new InvalidDataException(
                $"Unknown gameplay map-link activation '{value}'.")
        };
}
