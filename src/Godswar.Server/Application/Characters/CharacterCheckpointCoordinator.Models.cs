namespace Godswar.Server.Application.Characters;

internal sealed partial class CharacterCheckpointCoordinator
{
    private readonly record struct CheckpointKey(
        int AccountId,
        int CharacterId,
        CharacterCheckpointFacet Facet);

    private readonly record struct CheckpointWorkItem(
        CharacterCheckpointFacet Facet,
        CharacterPositionCheckpoint Position,
        CharacterVitalsCheckpoint Vitals)
    {
        public int AccountId =>
            Facet == CharacterCheckpointFacet.Position
                ? Position.AccountId
                : Vitals.AccountId;

        public int CharacterId =>
            Facet == CharacterCheckpointFacet.Position
                ? Position.CharacterId
                : Vitals.CharacterId;

        public PlayerOwnershipFence Owner =>
            Facet == CharacterCheckpointFacet.Position
                ? Position.Owner
                : Vitals.Owner;

        public long Revision =>
            Facet == CharacterCheckpointFacet.Position
                ? Position.Revision
                : Vitals.Revision;

        public CheckpointKey Key =>
            new(AccountId, CharacterId, Facet);

        public static CheckpointWorkItem From(
            CharacterPositionCheckpoint checkpoint) =>
            new(
                CharacterCheckpointFacet.Position,
                checkpoint,
                default);

        public static CheckpointWorkItem From(
            CharacterVitalsCheckpoint checkpoint) =>
            new(
                CharacterCheckpointFacet.Vitals,
                default,
                checkpoint);

        public bool HasSameValue(CheckpointWorkItem other) =>
            Facet == other.Facet &&
            (Facet == CharacterCheckpointFacet.Position
                ? Position == other.Position
                : Vitals == other.Vitals);
    }

    private sealed class PendingEntry(
        CheckpointWorkItem latest,
        DateTimeOffset firstEnqueuedAt)
    {
        public bool Active { get; set; }

        public int FailureCount { get; set; }

        public DateTimeOffset FirstEnqueuedAt { get; set; } =
            firstEnqueuedAt;

        public bool Invalidated { get; set; }

        public CheckpointWorkItem Latest { get; set; } = latest;

        public bool Queued { get; set; }

        public bool RetryScheduled { get; set; }
    }
}
