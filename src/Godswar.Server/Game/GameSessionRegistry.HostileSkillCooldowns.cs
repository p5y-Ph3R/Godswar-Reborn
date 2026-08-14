using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly HostileSkillCooldownAuthority
        _hostileSkillCooldowns = new();

    internal bool TryClaimHostileSkillCooldown(
        ClientSession session,
        GameCharacter character,
        uint skillId,
        TimeSpan cooldown,
        DateTimeOffset observedAt,
        out OwnedHostileSkillCooldownLease lease,
        out DateTimeOffset readyAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);

        lock (_gate)
        {
            if (!TryResolveCurrentHostileSkillOwnerLocked(
                    session,
                    character,
                    out var owner))
            {
                lease = default;
                readyAt = observedAt;
                return false;
            }

            return _hostileSkillCooldowns.TryClaim(
                owner,
                skillId,
                cooldown,
                observedAt,
                out lease,
                out readyAt);
        }
    }

    internal bool ReleaseHostileSkillCooldown(
        in OwnedHostileSkillCooldownLease lease) =>
        _hostileSkillCooldowns.TryRelease(lease);

    internal int PruneHostileSkillCooldowns(
        DateTimeOffset observedAt) =>
        _hostileSkillCooldowns.PruneExpired(observedAt);

    internal int HostileSkillCooldownOwnerCount =>
        _hostileSkillCooldowns.OwnerCount;

    private bool TryResolveCurrentHostileSkillOwnerLocked(
        ClientSession session,
        GameCharacter character,
        out HostileSkillCooldownOwner owner)
    {
        owner = default;
        if (!_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.CharacterId != character.Id ||
            !ReferenceEquals(context.Character, character))
        {
            return false;
        }

        if (context.Ownership.IsValid)
        {
            if (!IsCurrentAccountSession(
                    context.AccountId,
                    session,
                    context.Ownership))
            {
                return false;
            }
        }
        else if (_accountSessions.TryGetValue(
                     context.AccountId,
                     out var registered) &&
                 !ReferenceEquals(registered.Session, session))
        {
            return false;
        }

        owner = new HostileSkillCooldownOwner(
            context.AccountId,
            context.CharacterId);
        return true;
    }
}
