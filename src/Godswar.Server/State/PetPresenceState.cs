namespace Godswar.Server.State;

internal enum PetPresenceOperation
{
    Take,
    CallOut,
    Recall
}

internal enum PetPresenceTransitionStatus
{
    Succeeded,
    CharacterNotFound,
    PetNotFound,
    PetUnavailable,
    PetNotTaken
}

internal sealed record PetPresenceTransitionResult(
    PetPresenceTransitionStatus Status,
    long PetId,
    bool IsCarried,
    bool IsSummoned)
{
    public bool Succeeded =>
        Status == PetPresenceTransitionStatus.Succeeded;
}
