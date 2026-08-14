using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // Called only while _gate owns the world-session snapshot. Candidate
    // identity, map, life, and faction admission are all server authority;
    // no client-provided target list participates in resonance selection.
    private IReadOnlyList<PvpElementalCandidate>
        BuildPvpElementalCandidatesLocked(
            GameSessionContext attacker,
            GameSessionContext primaryTarget,
            DateTimeOffset now)
    {
        if (!float.IsFinite(primaryTarget.Character.PositionX) ||
            !float.IsFinite(primaryTarget.Character.PositionZ))
        {
            return [];
        }

        var candidates = new List<PvpElementalCandidate>();
        foreach (var candidate in _sessions.Values)
        {
            if (!candidate.WorldReady ||
                candidate.CharacterId == attacker.CharacterId ||
                candidate.CharacterId == primaryTarget.CharacterId ||
                candidate.WorldInstanceId != attacker.WorldInstanceId ||
                candidate.MapId != attacker.MapId ||
                candidate.Character.CurrentHp <= 0 ||
                !float.IsFinite(candidate.Character.PositionX) ||
                !float.IsFinite(candidate.Character.PositionZ))
            {
                continue;
            }

            var admission = _gameplayCatalogs.PvpWorldAuthority
                .EvaluateOpposingFaction(
                    attacker.Character,
                    candidate.Character,
                    now);
            if (!admission.Allowed)
            {
                continue;
            }

            var distance = AuthoredElementalCombatV1
                .AcceptedDistanceMillimeters(
                    primaryTarget.Character.PositionX,
                    primaryTarget.Character.PositionZ,
                    candidate.Character.PositionX,
                    candidate.Character.PositionZ);
            candidates.Add(new(
                candidate,
                new ResonanceTargetCandidate(
                    candidate.CharacterId,
                    candidate.MapId,
                    distance,
                    IsAlive: true,
                    IsBoss: false,
                    ResonanceTargetAuthority.AdmittedPlayer,
                    admission)));
        }

        return candidates
            .OrderBy(static value => value.Candidate.DistanceMillimeters)
            .ThenBy(static value => value.Context.CharacterId)
            .ToArray();
    }
}
