namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct HostileSkillCooldownLease(
    uint SkillId,
    long ClaimRevision,
    DateTimeOffset ReadyAt)
{
    public bool IsClaimed => ClaimRevision > 0;
}

/// <summary>
/// Thread-safe, server-owned admission for ordinary hostile damage skills.
/// A lease can be released only while it is still the current claim, so a
/// rejected downstream mutation cannot erase a newer admission.
/// </summary>
internal sealed class HostileSkillCooldownLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, CooldownEntry> _entries = [];
    private long _claimRevision;

    public bool TryClaim(
        uint skillId,
        TimeSpan cooldown,
        DateTimeOffset observedAt,
        out HostileSkillCooldownLease lease,
        out DateTimeOffset readyAt)
    {
        if (cooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        if (cooldown == TimeSpan.Zero)
        {
            readyAt = observedAt;
            lease = default;
            return true;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(skillId, out var current) &&
                observedAt < current.ReadyAt)
            {
                readyAt = current.ReadyAt;
                lease = default;
                return false;
            }

            var claimRevision = checked(_claimRevision + 1);
            if (claimRevision <= 0)
            {
                throw new OverflowException(
                    "The hostile-skill cooldown revision was exhausted.");
            }

            readyAt = observedAt + cooldown;
            _claimRevision = claimRevision;
            _entries[skillId] = new CooldownEntry(
                claimRevision,
                readyAt);
            lease = new HostileSkillCooldownLease(
                skillId,
                claimRevision,
                readyAt);
            return true;
        }
    }

    public bool TryRelease(in HostileSkillCooldownLease lease)
    {
        if (!lease.IsClaimed)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(lease.SkillId, out var current) ||
                current.ClaimRevision != lease.ClaimRevision ||
                current.ReadyAt != lease.ReadyAt)
            {
                return false;
            }

            return _entries.Remove(lease.SkillId);
        }
    }

    public bool PruneExpiredAndIsEmpty(DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            foreach (var skillId in _entries
                         .Where(pair => observedAt >= pair.Value.ReadyAt)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                _entries.Remove(skillId);
            }

            return _entries.Count == 0;
        }
    }

    private readonly record struct CooldownEntry(
        long ClaimRevision,
        DateTimeOffset ReadyAt);
}
