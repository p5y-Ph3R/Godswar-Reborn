using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal enum PartyOperationStatus : byte
{
    Applied = 0,
    ActorUnavailable = 1,
    InvalidActorName = 2,
    TargetUnavailable = 3,
    InvalidTarget = 4,
    NotLeader = 5,
    AlreadyInParty = 6,
    PartyFull = 7,
    InvitationMissing = 8
}

internal sealed record PartyDelivery(
    ClientSession Recipient,
    byte[] Packet,
    string Label);

internal sealed record PartyOperationResult(
    PartyOperationStatus Status,
    IReadOnlyList<PartyDelivery> Deliveries)
{
    public static PartyOperationResult Rejected(
        PartyOperationStatus status) => new(status, []);
}

internal sealed record PartyMembershipSnapshot(
    long PartyId,
    bool IsLeader,
    IReadOnlyList<int> MemberCharacterIds);

internal readonly record struct PartyMemberSnapshot(
    int CharacterId,
    uint ObjectId,
    int CurrentHp,
    int MaxHp,
    int Level,
    byte Profession,
    string Name,
    short MapId,
    float PositionX,
    float PositionZ);
