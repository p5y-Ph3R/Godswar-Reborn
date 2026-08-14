using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public void SetPetOwnerMergePresentation(
        ClientSession session,
        bool active)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var existing) ||
                existing.PetOwnerMergeActive == active)
            {
                return;
            }

            var updated = existing with
            {
                PetOwnerMergeActive = active,
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
