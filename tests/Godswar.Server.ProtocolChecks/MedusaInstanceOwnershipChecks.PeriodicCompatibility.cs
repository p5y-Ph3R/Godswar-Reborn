using System.Reflection;
using System.Runtime.ExceptionServices;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static readonly MethodInfo PeriodicCompletionProtocolCheck =
        typeof(MapInstance).GetMethod(
            "TryCompleteMedusaPeriodicDamageForProtocolCheck",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "The private periodic owner compatibility seam is unavailable.");

    private static bool TryCompletePeriodicDamageForProtocolCheck(
        MapInstance map,
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
            reservation,
        bool terminal,
        out MedusaPeriodicDamageDispositionOutcome outcome)
    {
        object?[] arguments = [reservation, terminal, null];
        try
        {
            var routed = (bool)PeriodicCompletionProtocolCheck.Invoke(
                map,
                arguments)!;
            outcome =
                (MedusaPeriodicDamageDispositionOutcome)arguments[2]!;
            return routed;
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}
