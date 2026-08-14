namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct HostileSkillCooldownOwner(
    int AccountId,
    int CharacterId)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(AccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CharacterId);
    }
}

internal readonly record struct OwnedHostileSkillCooldownLease(
    HostileSkillCooldownOwner Owner,
    long StateRevision,
    HostileSkillCooldownLease Lease)
{
    public bool IsClaimed => StateRevision > 0 && Lease.IsClaimed;
}

/// <summary>
/// Process-owned cooldown authority retained independently of a connection.
/// Exact state revisions prevent a lease from a removed/pruned owner state
/// from releasing a later claim created for the same character.
/// </summary>
internal sealed class HostileSkillCooldownAuthority
{
    private readonly object _gate = new();
    private readonly Dictionary<
        HostileSkillCooldownOwner,
        OwnerState> _owners = [];
    private long _nextStateRevision;

    public int OwnerCount
    {
        get
        {
            lock (_gate)
            {
                return _owners.Count;
            }
        }
    }

    public bool TryClaim(
        in HostileSkillCooldownOwner owner,
        uint skillId,
        TimeSpan cooldown,
        DateTimeOffset observedAt,
        out OwnedHostileSkillCooldownLease lease,
        out DateTimeOffset readyAt)
    {
        owner.Validate();
        if (cooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        if (cooldown == TimeSpan.Zero)
        {
            lease = default;
            readyAt = observedAt;
            return true;
        }

        lock (_gate)
        {
            if (!_owners.TryGetValue(owner, out var state))
            {
                var revision = checked(_nextStateRevision + 1);
                if (revision <= 0)
                {
                    throw new OverflowException(
                        "The hostile-skill owner revision was exhausted.");
                }

                _nextStateRevision = revision;
                state = new OwnerState(
                    revision,
                    new HostileSkillCooldownLedger());
                _owners.Add(owner, state);
            }

            if (!state.Ledger.TryClaim(
                    skillId,
                    cooldown,
                    observedAt,
                    out var innerLease,
                    out readyAt))
            {
                lease = default;
                return false;
            }

            lease = new OwnedHostileSkillCooldownLease(
                owner,
                state.Revision,
                innerLease);
            return true;
        }
    }

    public bool TryRelease(
        in OwnedHostileSkillCooldownLease lease)
    {
        if (!lease.IsClaimed)
        {
            return false;
        }

        lock (_gate)
        {
            return _owners.TryGetValue(lease.Owner, out var state) &&
                   state.Revision == lease.StateRevision &&
                   state.Ledger.TryRelease(lease.Lease);
        }
    }

    public int PruneExpired(DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            var expiredOwners = _owners
                .Where(pair =>
                    pair.Value.Ledger.PruneExpiredAndIsEmpty(observedAt))
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (var owner in expiredOwners)
            {
                _owners.Remove(owner);
            }

            return expiredOwners.Length;
        }
    }

    private sealed record OwnerState(
        long Revision,
        HostileSkillCooldownLedger Ledger);
}
