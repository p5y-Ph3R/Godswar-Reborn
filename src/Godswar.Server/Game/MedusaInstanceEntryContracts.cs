using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal enum MedusaPartyEntryStatus : byte
{
    Ready = 1,
    LeaderRequired = 2,
    PartyUnavailable = 3,
    LevelRequirementNotMet = 4,
    DailyEntryAlreadyUsed = 5,
    RuntimeUnavailable = 6,
    TransferFailed = 7,
    InvitationAlreadyPending = 8
}

internal enum MedusaInvitationResponseStatus : byte
{
    Missing = 0,
    AwaitingParty = 1,
    Ready = 2,
    Declined = 3,
    Expired = 4,
    PartyChanged = 5
}

internal sealed record MedusaInstancePartySnapshot(
    long? PartyId,
    RealmId RealmId,
    int LeaderCharacterId,
    IReadOnlyList<MedusaInstancePartyMember> Members);

internal sealed record MedusaInstancePartyMember(
    ClientSession Session,
    int AccountId,
    int CharacterId,
    string CharacterName,
    int Level,
    RealmId RealmId,
    WorldInstanceId SourceWorldInstanceId,
    byte SourceMapId,
    PlayerOwnershipFence Ownership);

internal sealed record MedusaInstanceInvitation(
    int ClientSceneId,
    int InvitationId,
    MedusaEncounterDifficulty Difficulty,
    MedusaInstancePartySnapshot Party,
    MedusaInstancePartyMember Invitee,
    WorldInstanceId TargetWorldInstanceId,
    DateTimeOffset ExpiresAt);

internal readonly record struct MedusaInvitationResponseResult(
    MedusaInvitationResponseStatus Status,
    MedusaInstanceInvitation? Invitation);

internal readonly record struct MedusaInstanceTransitionCommand(
    int CharacterId,
    WorldInstanceId ExpectedSourceWorldInstanceId,
    byte ExpectedSourceMapId,
    PlayerOwnershipFence ExpectedOwnership,
    WorldInstanceId TargetWorldInstanceId,
    byte TargetMapId,
    float TargetX,
    float TargetZ);
