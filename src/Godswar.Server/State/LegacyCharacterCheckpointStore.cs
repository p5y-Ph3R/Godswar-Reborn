using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;

namespace Godswar.Server.State;

/// <summary>
/// Adapts the local-development JSON store to the B10 checkpoint contract.
/// Ownership is process-local and is deliberately not a scale-out fence.
/// </summary>
internal sealed class LegacyCharacterCheckpointStore(
    IGameStore gameStore) : ICharacterCheckpointStore
{
    private readonly IGameStore _gameStore =
        gameStore ?? throw new ArgumentNullException(nameof(gameStore));
    private readonly ConcurrentDictionary<CharacterKey, LocalState> _states =
        new();

    public async Task<CharacterCheckpointOwnership?> AcquireAsync(
        int accountId,
        int characterId,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        CharacterCheckpointValidation.ValidateIdentity(
            accountId,
            characterId,
            new CharacterCheckpointOwner(ownerId, 1));
        var key = new CharacterKey(accountId, characterId);
        var state = _states.GetOrAdd(key, static _ => new LocalState());
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Owner is { } existing &&
                existing.OwnerId == ownerId)
            {
                return new CharacterCheckpointOwnership(
                    existing,
                    state.Position.Revision,
                    state.Vitals.Revision);
            }

            var character = await _gameStore.GetFirstCharacterAsync(
                accountId,
                cancellationToken);
            if (character is null || character.Id != characterId)
            {
                _states.TryRemove(
                    new KeyValuePair<CharacterKey, LocalState>(key, state));
                return null;
            }

            var generation = checked(state.Generation + 1);
            state.Generation = generation;
            state.Owner = new CharacterCheckpointOwner(
                ownerId,
                generation);
            state.Position = new PositionState(
                character.CurrentMap,
                character.PositionX,
                character.PositionZ,
                character.PositionRevision);
            state.Vitals = new VitalsState(
                character.CurrentHp,
                character.CurrentMp,
                character.VitalsRevision);
            return new CharacterCheckpointOwnership(
                state.Owner.Value,
                state.Position.Revision,
                state.Vitals.Revision);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<CharacterCheckpointWriteResult> WritePositionAsync(
        CharacterPositionCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        checkpoint.Validate();
        var key = new CharacterKey(
            checkpoint.AccountId,
            checkpoint.CharacterId);
        if (!_states.TryGetValue(key, out var state))
        {
            return OwnershipLost();
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Owner != checkpoint.Owner)
            {
                return OwnershipLost(state.Position.Revision);
            }

            var comparison = ComparePosition(checkpoint, state.Position);
            if (comparison is { } terminal)
            {
                return terminal;
            }

            if (_gameStore is JsonGameStore jsonStore)
            {
                await jsonStore.SaveCharacterPositionCheckpointAsync(
                    checkpoint.AccountId,
                    checkpoint.CharacterId,
                    checkpoint.CurrentMap,
                    checkpoint.PositionX,
                    checkpoint.PositionZ,
                    checkpoint.Revision,
                    cancellationToken);
            }
            else
            {
                await _gameStore.SaveCharacterPositionAsync(
                    checkpoint.AccountId,
                    checkpoint.CharacterId,
                    checkpoint.CurrentMap,
                    checkpoint.PositionX,
                    checkpoint.PositionZ,
                    cancellationToken);
            }
            state.Position = new PositionState(
                checkpoint.CurrentMap,
                checkpoint.PositionX,
                checkpoint.PositionZ,
                checkpoint.Revision);
            return Applied(checkpoint.Revision);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<CharacterCheckpointWriteResult> WriteVitalsAsync(
        CharacterVitalsCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        checkpoint.Validate();
        var key = new CharacterKey(
            checkpoint.AccountId,
            checkpoint.CharacterId);
        if (!_states.TryGetValue(key, out var state))
        {
            return OwnershipLost();
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Owner != checkpoint.Owner)
            {
                return OwnershipLost(state.Vitals.Revision);
            }

            var comparison = CompareVitals(checkpoint, state.Vitals);
            if (comparison is { } terminal)
            {
                return terminal;
            }

            await _gameStore.SaveCharacterVitalsAsync(
                checkpoint.AccountId,
                checkpoint.CharacterId,
                checkpoint.CurrentHp,
                checkpoint.CurrentMp,
                checkpoint.Revision,
                cancellationToken);
            state.Vitals = new VitalsState(
                checkpoint.CurrentHp,
                checkpoint.CurrentMp,
                checkpoint.Revision);
            return Applied(checkpoint.Revision);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
        int accountId,
        int characterId,
        CharacterCheckpointOwner owner,
        CancellationToken cancellationToken = default)
    {
        CharacterCheckpointValidation.ValidateIdentity(
            accountId,
            characterId,
            owner);
        var key = new CharacterKey(accountId, characterId);
        if (!_states.TryGetValue(key, out var state))
        {
            return CharacterCheckpointReleaseStatus.AlreadyReleased;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Owner is null)
            {
                return CharacterCheckpointReleaseStatus.AlreadyReleased;
            }
            if (state.Owner != owner)
            {
                return CharacterCheckpointReleaseStatus.OwnershipLost;
            }

            state.Owner = null;
            _states.TryRemove(
                new KeyValuePair<CharacterKey, LocalState>(key, state));
            return CharacterCheckpointReleaseStatus.Released;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static CharacterCheckpointWriteResult? ComparePosition(
        CharacterPositionCheckpoint candidate,
        PositionState stored)
    {
        if (candidate.Revision < stored.Revision)
        {
            return Superseded(stored.Revision);
        }
        if (candidate.Revision > stored.Revision)
        {
            return null;
        }

        return candidate.CurrentMap == stored.Map &&
               candidate.PositionX.Equals(stored.X) &&
               candidate.PositionZ.Equals(stored.Z)
            ? AlreadyApplied(stored.Revision)
            : Conflict(stored.Revision);
    }

    private static CharacterCheckpointWriteResult? CompareVitals(
        CharacterVitalsCheckpoint candidate,
        VitalsState stored)
    {
        if (candidate.Revision < stored.Revision)
        {
            return Superseded(stored.Revision);
        }
        if (candidate.Revision > stored.Revision)
        {
            return null;
        }

        return candidate.CurrentHp == stored.Hp &&
               candidate.CurrentMp == stored.Mp
            ? AlreadyApplied(stored.Revision)
            : Conflict(stored.Revision);
    }

    private static CharacterCheckpointWriteResult Applied(long revision) =>
        new(CharacterCheckpointWriteStatus.Applied, revision);

    private static CharacterCheckpointWriteResult AlreadyApplied(
        long revision) =>
        new(CharacterCheckpointWriteStatus.AlreadyApplied, revision);

    private static CharacterCheckpointWriteResult Superseded(long revision) =>
        new(CharacterCheckpointWriteStatus.Superseded, revision);

    private static CharacterCheckpointWriteResult Conflict(long revision) =>
        new(CharacterCheckpointWriteStatus.RevisionConflict, revision);

    private static CharacterCheckpointWriteResult OwnershipLost(
        long? revision = null) =>
        new(CharacterCheckpointWriteStatus.OwnershipLost, revision);

    private readonly record struct CharacterKey(
        int AccountId,
        int CharacterId);

    private sealed class LocalState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long Generation { get; set; }

        public CharacterCheckpointOwner? Owner { get; set; }

        public PositionState Position { get; set; }

        public VitalsState Vitals { get; set; }
    }

    private readonly record struct PositionState(
        byte Map,
        float X,
        float Z,
        long Revision);

    private readonly record struct VitalsState(
        int Hp,
        int Mp,
        long Revision);
}
