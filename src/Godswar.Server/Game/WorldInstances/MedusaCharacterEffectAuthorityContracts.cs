namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaCharacterEffectAuthorityOutcome : byte
{
    Unbound = 1,
    ResolvedActive = 2,
    RunNotActive = 3,
    CurrentMembershipRequired = 4,
    BoundAuthorityUnavailable = 5
}

internal readonly record struct MedusaCharacterEffectAuthorityResult(
    MedusaCharacterEffectAuthorityOutcome Outcome,
    MedusaActiveCharacterEffectView? View)
{
    public bool IsBound =>
        Outcome != MedusaCharacterEffectAuthorityOutcome.Unbound;

    public bool IsResolved =>
        Outcome ==
            MedusaCharacterEffectAuthorityOutcome.ResolvedActive &&
        View is not null;

    public bool ShouldFailClosed => Outcome is
        MedusaCharacterEffectAuthorityOutcome.CurrentMembershipRequired or
        MedusaCharacterEffectAuthorityOutcome.BoundAuthorityUnavailable;

    public bool Allows(MedusaEncounterControlRestriction action) =>
        Outcome switch
        {
            MedusaCharacterEffectAuthorityOutcome.Unbound or
            MedusaCharacterEffectAuthorityOutcome.RunNotActive => true,
            MedusaCharacterEffectAuthorityOutcome.ResolvedActive =>
                View is not null &&
                (View.ControlRestriction & action) == 0,
            _ => false
        };
}
