using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal enum MedusaCompletionRewardStatus : byte
{
    Applied = 1,
    Duplicate = 2,
    RequestConflict = 3,
    CharacterUnavailable = 4
}

internal sealed class MedusaCompletionRewardRequest
{
    public MedusaCompletionRewardRequest(
        WorldInstanceId worldInstanceId,
        RealmId realmId,
        MedusaEncounterDifficulty difficulty,
        DateTimeOffset completedAtUtc,
        TimeSpan elapsed,
        int finalScore,
        IReadOnlyCollection<int> characterIds)
    {
        ArgumentNullException.ThrowIfNull(characterIds);
        var frozenCharacterIds = characterIds
            .Distinct()
            .Order()
            .ToArray();
        if (!worldInstanceId.IsValid ||
            !realmId.IsValid ||
            completedAtUtc == default ||
            completedAtUtc.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(difficulty) ||
            elapsed < TimeSpan.Zero ||
            elapsed >= MedusaIslandPolicy.TimeLimit ||
            finalScore < 0 ||
            frozenCharacterIds.Length != characterIds.Count ||
            frozenCharacterIds.Length is < MedusaIslandPolicy.MinimumPartySize
                or > MedusaIslandPolicy.MaximumPartySize ||
            frozenCharacterIds.Any(static characterId => characterId <= 0) ||
            !MedusaCompletionRewardPolicy.TryResolve(
                difficulty,
                finalScore,
                elapsed,
                out var award))
        {
            throw new ArgumentException(
                "Invalid completed Medusa reward evidence.");
        }

        WorldInstanceId = worldInstanceId;
        RealmId = realmId;
        Difficulty = difficulty;
        CompletedAtUtc = completedAtUtc;
        Elapsed = elapsed;
        FinalScore = finalScore;
        CharacterIds = Array.AsReadOnly(frozenCharacterIds);
        Award = award;
    }

    public WorldInstanceId WorldInstanceId { get; }

    public RealmId RealmId { get; }

    public MedusaEncounterDifficulty Difficulty { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public TimeSpan Elapsed { get; }

    public int FinalScore { get; }

    public IReadOnlyList<int> CharacterIds { get; }

    public MedusaCompletionRewardAward Award { get; }
}

internal readonly record struct MedusaCompletionRewardMember(
    int CharacterId,
    byte Camp,
    int HonorBefore,
    int HonorAfter,
    long RewardRevision,
    uint AwardedTitleId);

internal sealed record MedusaCompletionRewardReceipt(
    MedusaCompletionRewardStatus Status,
    WorldInstanceId WorldInstanceId,
    MedusaCompletionRewardAward Award,
    IReadOnlyList<MedusaCompletionRewardMember> Members)
{
    public bool Succeeded => Status is
        MedusaCompletionRewardStatus.Applied or
        MedusaCompletionRewardStatus.Duplicate;
}

internal interface IMedusaCompletionRewardStore
{
    Task<MedusaCompletionRewardReceipt> SettleAsync(
        MedusaCompletionRewardRequest request,
        CancellationToken cancellationToken = default);
}
