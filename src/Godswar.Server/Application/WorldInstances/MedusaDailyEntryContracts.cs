using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal enum MedusaDailyEntryClaimStatus : byte
{
    Claimed = 1,
    AlreadyUsed = 2
}

internal readonly record struct MedusaDailyEntryClaimResult(
    MedusaDailyEntryClaimStatus Status,
    ushort DailyEntryLimit);

internal sealed record MedusaDailyEntryClaimRequest(
    Guid ReservationId,
    RealmId RealmId,
    DateOnly RealmDay,
    MedusaEncounterDifficulty Difficulty,
    IReadOnlyCollection<int> CharacterIds,
    DateTimeOffset ClaimedAtUtc)
{
    public void Validate()
    {
        if (ReservationId == Guid.Empty ||
            !RealmId.IsValid ||
            ClaimedAtUtc == default ||
            ClaimedAtUtc.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(Difficulty))
        {
            throw new ArgumentException(
                "Invalid Medusa daily-entry claim identity.");
        }
        if (CharacterIds.Count is <
                MedusaIslandPolicy.MinimumPartySize or >
                MedusaIslandPolicy.MaximumPartySize ||
            CharacterIds.Any(static id => id <= 0) ||
            CharacterIds.Distinct().Count() != CharacterIds.Count)
        {
            throw new ArgumentException(
                "A Medusa daily-entry claim requires one to five unique " +
                "characters.");
        }
    }
}

internal interface IMedusaDailyEntryClaimStore
{
    Task<IReadOnlySet<int>> FindUsedCharacterIdsAsync(
        RealmId realmId,
        DateOnly realmDay,
        IReadOnlyCollection<int> characterIds,
        CancellationToken cancellationToken = default);

    Task<MedusaDailyEntryClaimResult> TryClaimAsync(
        MedusaDailyEntryClaimRequest request,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default);
}
