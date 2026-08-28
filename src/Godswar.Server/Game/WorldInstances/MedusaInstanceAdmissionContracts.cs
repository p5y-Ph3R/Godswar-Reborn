namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaInstanceCharacterAdmissionOutcome : byte
{
    InstanceUnbound = 1,
    CharacterAdmitted = 2,
    CharacterNotAdmitted = 3
}

/// <summary>
/// A bound Medusa roster is authoritative for the lifetime of its instance.
/// Unbound instances retain the ordinary world-membership behavior.
/// </summary>
internal readonly record struct MedusaInstanceCharacterAdmissionResult(
    MedusaInstanceCharacterAdmissionOutcome Outcome)
{
    public bool MayEnter => Outcome is
        MedusaInstanceCharacterAdmissionOutcome.InstanceUnbound or
        MedusaInstanceCharacterAdmissionOutcome.CharacterAdmitted;

    public bool IsBound => Outcome !=
        MedusaInstanceCharacterAdmissionOutcome.InstanceUnbound;
}
