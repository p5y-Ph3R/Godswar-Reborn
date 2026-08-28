using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct PartyAdmissionMember
{
    public PartyAdmissionMember(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        RealmId realmId,
        int level,
        WorldInstanceId sourceWorldInstanceId,
        MapId sourceMapId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        ownership.Validate();
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        if (level < MedusaIslandPolicy.MinimumLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "Medusa party members must carry trusted level eligibility.");
        }
        if (!sourceWorldInstanceId.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceWorldInstanceId));
        }
        if (!sourceMapId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceMapId));
        }

        AccountId = accountId;
        CharacterId = characterId;
        Ownership = ownership;
        RealmId = realmId;
        Level = level;
        SourceWorldInstanceId = sourceWorldInstanceId;
        SourceMapId = sourceMapId;
    }

    public int AccountId { get; }

    public int CharacterId { get; }

    public PlayerOwnershipFence Ownership { get; }

    public RealmId RealmId { get; }

    public int Level { get; }

    public WorldInstanceId SourceWorldInstanceId { get; }

    public MapId SourceMapId { get; }
}

/// <summary>
/// Frozen, non-revocable eligibility capability issued by a future
/// authoritative party component. Membership, leader revision, account/character ownership,
/// realm, and minimum level are trusted assertions; callers must never derive
/// them from proximity or client-supplied IDs. This assembly deliberately
/// provides no live issuer or NPC wiring yet. An issuer must serialize any
/// leader/roster/revision mutation that would invalidate this exact capability
/// until <see cref="ExpiresAtUtc"/>; a best-effort party snapshot is not an
/// acceptable implementation. The constructor copies and
/// validates the ordered roster so later saga stages cannot observe
/// caller-owned collection mutation.
/// </summary>
internal sealed class PartyAdmissionLease
{
    public PartyAdmissionLease(
        Guid leaseId,
        Guid partyId,
        long partyRevision,
        int leaderAccountId,
        int leaderCharacterId,
        IEnumerable<PartyAdmissionMember> orderedMembers,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (leaseId == Guid.Empty)
        {
            throw new ArgumentException(
                "Party admission lease IDs cannot be empty.",
                nameof(leaseId));
        }
        if (partyId == Guid.Empty)
        {
            throw new ArgumentException(
                "Party IDs cannot be empty.",
                nameof(partyId));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partyRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaderAccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaderCharacterId);
        ArgumentNullException.ThrowIfNull(orderedMembers);

        var members = ImmutableArray.CreateRange(orderedMembers);
        if (members.Length is < MedusaIslandPolicy.MinimumPartySize or
            > MedusaIslandPolicy.MaximumPartySize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderedMembers),
                members.Length,
                "Medusa admission rosters must contain one to five members.");
        }
        if (members.Any(static member =>
                member.AccountId <= 0 ||
                member.CharacterId <= 0 ||
                !member.Ownership.IsValid ||
                !member.RealmId.IsValid ||
                member.Level < MedusaIslandPolicy.MinimumLevel ||
                !member.SourceWorldInstanceId.IsValid ||
                !member.SourceMapId.IsValid))
        {
            throw new ArgumentException(
                "Party admission members require complete trusted eligibility evidence.",
                nameof(orderedMembers));
        }
        if (members.Select(static member => member.AccountId).Distinct().Count()
                != members.Length ||
            members.Select(static member => member.CharacterId).Distinct().Count()
                != members.Length)
        {
            throw new ArgumentException(
                "Party admission members must have unique accounts and characters.",
                nameof(orderedMembers));
        }

        var leaderMatches = members.Count(member =>
            member.AccountId == leaderAccountId &&
            member.CharacterId == leaderCharacterId);
        if (leaderMatches != 1)
        {
            throw new ArgumentException(
                "The declared party leader must occur exactly once in the roster.",
                nameof(orderedMembers));
        }

        issuedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            issuedAtUtc,
            nameof(issuedAtUtc));
        expiresAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            expiresAtUtc,
            nameof(expiresAtUtc));
        if (expiresAtUtc <= issuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "Party admission leases must expire after issuance.");
        }

        LeaseId = leaseId;
        PartyId = partyId;
        PartyRevision = partyRevision;
        LeaderAccountId = leaderAccountId;
        LeaderCharacterId = leaderCharacterId;
        Members = members;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid LeaseId { get; }

    public Guid PartyId { get; }

    public long PartyRevision { get; }

    public int LeaderAccountId { get; }

    public int LeaderCharacterId { get; }

    public ImmutableArray<PartyAdmissionMember> Members { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsValidAt(DateTimeOffset instantUtc)
    {
        instantUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            instantUtc,
            nameof(instantUtc));
        return instantUtc >= IssuedAtUtc && instantUtc < ExpiresAtUtc;
    }
}
