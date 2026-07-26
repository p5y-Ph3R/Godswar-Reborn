namespace Godswar.Server.World.Systems.Players;

/// <summary>
/// Captured conservative defaults for the first authoritative movement slice.
/// The values are policy, not properties accepted from a client.
/// </summary>
internal sealed class AuthoritativePlayerMovementPolicy
{
    public const int SimulationTicksPerSecond = 20;

    public static readonly TimeSpan FixedStep =
        TimeSpan.FromMilliseconds(50);

    public static readonly TimeSpan DefaultElapsedCreditCap =
        TimeSpan.FromSeconds(1);

    public static readonly TimeSpan DefaultMinimumInputCadence =
        TimeSpan.FromMilliseconds(20);

    public AuthoritativePlayerMovementPolicy(
        float baseMaximumSpeed = 8f,
        float positionTolerance = 0.75f,
        TimeSpan? elapsedCreditCap = null,
        TimeSpan? minimumInputCadence = null,
        float maximumMovementMultiplier = 4f)
    {
        if (!float.IsFinite(baseMaximumSpeed) ||
            baseMaximumSpeed <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseMaximumSpeed));
        }

        if (!float.IsFinite(positionTolerance) ||
            positionTolerance < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(positionTolerance));
        }

        if (!float.IsFinite(maximumMovementMultiplier) ||
            maximumMovementMultiplier <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMovementMultiplier));
        }

        var resolvedCreditCap =
            elapsedCreditCap ?? DefaultElapsedCreditCap;
        if (resolvedCreditCap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsedCreditCap));
        }

        var resolvedMinimumCadence =
            minimumInputCadence ?? DefaultMinimumInputCadence;
        if (resolvedMinimumCadence < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumInputCadence));
        }

        BaseMaximumSpeed = baseMaximumSpeed;
        PositionTolerance = positionTolerance;
        ElapsedCreditCap = resolvedCreditCap;
        MinimumInputCadence = resolvedMinimumCadence;
        MaximumMovementMultiplier = maximumMovementMultiplier;
    }

    public float BaseMaximumSpeed { get; }

    public float PositionTolerance { get; }

    public TimeSpan ElapsedCreditCap { get; }

    public TimeSpan MinimumInputCadence { get; }

    public float MaximumMovementMultiplier { get; }
}
