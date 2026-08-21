using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public void SetPetOwnerMergePresentation(
        ClientSession session,
        bool active,
        PetAptitude aptitude = 0,
        short completedRebirths = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (active &&
            (aptitude is < PetAptitude.Weak or > PetAptitude.Transcendent ||
             completedRebirths is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                "An active Merge requires a native-compatible visual profile.");
        }
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var existing) ||
                existing.PetOwnerMergeActive == active &&
                (!active ||
                 existing.PetOwnerMergeAptitude == aptitude &&
                 existing.PetOwnerMergeCompletedRebirths ==
                    completedRebirths))
            {
                return;
            }

            var updated = existing with
            {
                PetOwnerMergeActive = active,
                PetOwnerMergeAptitude = active ? aptitude : 0,
                PetOwnerMergeCompletedRebirths =
                    active ? completedRebirths : (short)0,
                WorldRevision = checked(existing.WorldRevision + 1)
            };
            AddToMap(updated);
            _sessions[session] = updated;
        }
    }

    public bool IsPetOwnerMergePresentationActive(ClientSession session)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(session, out var context) &&
                context.PetOwnerMergeActive;
        }
    }
}
