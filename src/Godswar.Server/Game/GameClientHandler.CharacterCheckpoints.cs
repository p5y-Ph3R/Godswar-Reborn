using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static readonly TimeSpan CheckpointFinalizationTimeout =
        TimeSpan.FromSeconds(10);
    private readonly ICharacterCheckpointCoordinator?
        _characterCheckpoints;
    private readonly IPlayerCoordinationLeaseIssuer?
        _playerCoordination;
    private IPlayerCoordinationLease? _playerCoordinationLease;
    private bool _checkpointOwnershipAcquired;

    private async Task<bool> EnsureCheckpointOwnershipAsync(
        CancellationToken cancellationToken)
    {
        if (_characterCheckpoints is null)
        {
            return true;
        }
        if (_checkpointOwnershipAcquired)
        {
            if (TryCaptureCurrentPlayerOwnership(out _))
            {
                return true;
            }

            _session.Disconnect();
            return false;
        }
        if (_account is null || _character is null)
        {
            return false;
        }

        var accountId = _account.Id;
        var characterId = _character.Id;
        using var acquisitionScope =
            await _registry.EnterAccountCheckpointAcquisitionAsync(
                accountId,
                _session,
                cancellationToken);
        if (acquisitionScope is null)
        {
            _session.Disconnect();
            return false;
        }

        var ownership = await _characterCheckpoints.AcquireAsync(
            accountId,
            characterId,
            _commandConnectionId,
            cancellationToken);
        if (!acquisitionScope.IsCurrent)
        {
            if (ownership is { } staleOwnership)
            {
                await ReleaseCheckpointOwnershipAsync(
                    accountId,
                    characterId,
                    staleOwnership.Owner,
                    CancellationToken.None);
            }
            _session.Disconnect();
            return false;
        }
        if (ownership is null)
        {
            RejectCharacterSnapshot(
                "enter",
                "checkpoint_character_not_found");
            return false;
        }

        try
        {
            if (!await RefreshCharacterSnapshotAsync(
                    "checkpoint-owner",
                    cancellationToken) ||
                !acquisitionScope.IsCurrent ||
                _character is null ||
                _character.Id != characterId)
            {
                await ReleaseCheckpointOwnershipAsync(
                    accountId,
                    characterId,
                    ownership.Value.Owner,
                    CancellationToken.None);
                return false;
            }

            if (_character.PositionRevision !=
                    ownership.Value.PositionRevision ||
                _character.VitalsRevision !=
                    ownership.Value.VitalsRevision)
            {
                await ReleaseCheckpointOwnershipAsync(
                    accountId,
                    characterId,
                    ownership.Value.Owner,
                    CancellationToken.None);
                RejectCharacterSnapshot(
                    "enter",
                    "checkpoint_revision_mismatch");
                return false;
            }

            if (!acquisitionScope.IsCurrent)
            {
                await ReleaseCheckpointOwnershipAsync(
                    accountId,
                    characterId,
                    ownership.Value.Owner,
                    CancellationToken.None);
                _session.Disconnect();
                return false;
            }

            _character.CheckpointOwnerId =
                ownership.Value.Owner.OwnerId;
            _character.CheckpointOwnerGeneration =
                ownership.Value.Owner.Generation;
            var playerOwnership = new PlayerOwnershipFence(
                ownership.Value.Owner.OwnerId,
                ownership.Value.Owner.Generation);
            if (_playerCoordination?.IsEnabled == true)
            {
                if (!TryResolveCoordinatedRoute(
                        _character.CurrentMap,
                        out var route,
                        requireInitialGatewayRoute: true))
                {
                    await ReleaseCheckpointOwnershipAsync(
                        accountId,
                        characterId,
                        ownership.Value.Owner,
                        CancellationToken.None);
                    _session.Disconnect();
                    return false;
                }

                _playerCoordinationLease =
                    await _playerCoordination.AcquireAsync(
                        accountId,
                        characterId,
                        playerOwnership,
                        route,
                        _session.Disconnect,
                        cancellationToken);
                if (_playerCoordinationLease is null)
                {
                    await ReleaseCheckpointOwnershipAsync(
                        accountId,
                        characterId,
                        ownership.Value.Owner,
                        CancellationToken.None);
                    _session.Disconnect();
                    return false;
                }
            }
            if (!_registry.TryBindAccountSessionOwnership(
                    accountId,
                    _session,
                    playerOwnership))
            {
                await ReleasePlayerCoordinationLeaseAsync();
                await ReleaseCheckpointOwnershipAsync(
                    accountId,
                    characterId,
                    ownership.Value.Owner,
                    CancellationToken.None);
                RejectLostPlayerOwnership();
                return false;
            }

            _checkpointOwnershipAcquired = true;
            return true;
        }
        catch
        {
            await ReleasePlayerCoordinationLeaseAsync();
            await ReleaseCheckpointOwnershipAsync(
                accountId,
                characterId,
                ownership.Value.Owner,
                CancellationToken.None);
            throw;
        }
    }

    private async Task<bool> PersistPositionCheckpointAsync(
        GameCharacter character,
        bool force,
        CancellationToken cancellationToken) =>
        await PersistPositionCheckpointAsync(
            character,
            character.CurrentMap,
            character.PositionX,
            character.PositionZ,
            character.PositionRevision,
            force,
            cancellationToken);

    private async Task<bool> PersistPositionCheckpointAsync(
        GameCharacter character,
        byte mapId,
        float positionX,
        float positionZ,
        long revision,
        bool force,
        CancellationToken cancellationToken)
    {
        var accountId = _account?.Id ?? character.AccountId;
        if (_characterCheckpoints is null)
        {
            if (!AllowLegacyPlayerMutationFallback(
                    "save_character_position"))
            {
                return false;
            }

            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.SaveCharacterPosition);
            await _store.SaveCharacterPositionAsync(
                accountId,
                character.Id,
                mapId,
                positionX,
                positionZ,
                cancellationToken);
            return true;
        }
        if (revision <= 0)
        {
            return true;
        }

        var checkpoint = new CharacterPositionCheckpoint(
            accountId,
            character.Id,
            GetCheckpointOwner(character),
            mapId,
            positionX,
            positionZ,
            revision);
        if (force)
        {
            var result = await _characterCheckpoints.FlushThroughAsync(
                checkpoint,
                cancellationToken);
            return RequireSatisfied(result, revision, "position");
        }

        return AcceptEnqueue(
            _characterCheckpoints.TryEnqueue(checkpoint),
            "position");
    }

    private async Task<bool> PersistVitalsCheckpointAsync(
        GameCharacter character,
        bool force,
        CancellationToken cancellationToken)
    {
        int currentHp;
        int currentMp;
        long revision;
        lock (character.VitalsSync)
        {
            currentHp = character.CurrentHp;
            currentMp = character.CurrentMp;
            revision = character.VitalsRevision;
        }

        var accountId = _account?.Id ?? character.AccountId;
        if (_characterCheckpoints is null)
        {
            if (!AllowLegacyPlayerMutationFallback(
                    "save_character_vitals"))
            {
                return false;
            }

            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.SaveCharacterVitals);
            await _store.SaveCharacterVitalsAsync(
                accountId,
                character.Id,
                currentHp,
                currentMp,
                revision,
                cancellationToken);
            return true;
        }
        if (revision <= 0)
        {
            return true;
        }

        var checkpoint = new CharacterVitalsCheckpoint(
            accountId,
            character.Id,
            GetCheckpointOwner(character),
            currentHp,
            currentMp,
            revision);
        if (force)
        {
            var result = await _characterCheckpoints.FlushThroughAsync(
                checkpoint,
                cancellationToken);
            return RequireSatisfied(result, revision, "vitals");
        }

        return AcceptEnqueue(
            _characterCheckpoints.TryEnqueue(checkpoint),
            "vitals");
    }

    private async Task<bool> PersistRelocationCheckpointAsync(
        byte mapId,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }

        var revision = _character.MarkPositionChanged();
        return await PersistPositionCheckpointAsync(
            _character,
            mapId,
            positionX,
            positionZ,
            revision,
            force: true,
            cancellationToken);
    }

    private async Task FinalizeCheckpointOwnershipAsync()
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        using var flushDeadline = new CancellationTokenSource(
            CheckpointFinalizationTimeout);
        await Task.WhenAll(
            FlushFinalPositionAsync(
                character,
                flushDeadline.Token),
            FlushFinalVitalsAsync(
                character,
                flushDeadline.Token));

        await ReleasePlayerCoordinationLeaseAsync();

        if (_checkpointOwnershipAcquired &&
            TryGetCheckpointOwner(character, out var owner))
        {
            using var releaseDeadline =
                new CancellationTokenSource(
                    CheckpointFinalizationTimeout);
            try
            {
                await ReleaseCheckpointOwnershipAsync(
                    _account?.Id ?? character.AccountId,
                    character.Id,
                    owner,
                    releaseDeadline.Token);
            }
            catch (Exception error)
            {
                Console.WriteLine(
                    "[checkpoint] owner release failed " +
                    $"reason={error.GetType().Name}");
            }
        }
    }

    private async Task FlushFinalPositionAsync(
        GameCharacter character,
        CancellationToken cancellationToken)
    {
        try
        {
            await PersistPositionCheckpointAsync(
                character,
                force: true,
                cancellationToken);
        }
        catch (Exception error)
        {
            Console.WriteLine(
                "[checkpoint] final position flush failed " +
                $"reason={error.GetType().Name}");
        }
    }

    private async Task FlushFinalVitalsAsync(
        GameCharacter character,
        CancellationToken cancellationToken)
    {
        try
        {
            await PersistVitalsCheckpointAsync(
                character,
                force: true,
                cancellationToken);
        }
        catch (Exception error)
        {
            Console.WriteLine(
                "[checkpoint] final vitals flush failed " +
                $"reason={error.GetType().Name}");
        }
    }

    private async Task ReleaseCheckpointOwnershipAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence owner,
        CancellationToken cancellationToken)
    {
        if (_characterCheckpoints is null)
        {
            return;
        }

        var status = await _characterCheckpoints.ReleaseAsync(
            accountId,
            characterId,
            owner,
            cancellationToken);
        if (status is CharacterCheckpointReleaseStatus.Released or
            CharacterCheckpointReleaseStatus.AlreadyReleased or
            CharacterCheckpointReleaseStatus.OwnershipLost)
        {
            _checkpointOwnershipAcquired = false;
            if (_character is { } character &&
                character.Id == characterId &&
                character.CheckpointOwnerId == owner.OwnerId &&
                character.CheckpointOwnerGeneration ==
                    owner.Generation)
            {
                character.CheckpointOwnerId = Guid.Empty;
                character.CheckpointOwnerGeneration = 0;
            }
            return;
        }

        throw new InvalidOperationException(
            "The checkpoint owner could not be released because the " +
            "character no longer exists.");
    }

    private void InstallUpdatedCharacter(GameCharacter updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        var current = _character;
        if (current is not null && current.Id == updated.Id)
        {
            updated.CurrentMap = current.CurrentMap;
            updated.PositionX = current.PositionX;
            updated.PositionZ = current.PositionZ;
            updated.PositionRevision = Math.Max(
                updated.PositionRevision,
                current.PositionRevision);
            lock (current.VitalsSync)
            {
                var currentHp = current.CurrentHp;
                var currentMp = current.CurrentMp;
                updated.CurrentHp = Math.Clamp(
                    currentHp,
                    0,
                    Math.Max(1, updated.MaxHp));
                updated.CurrentMp = Math.Clamp(
                    currentMp,
                    0,
                    Math.Max(0, updated.MaxMp));
                updated.VitalsRevision = Math.Max(
                    updated.VitalsRevision,
                    current.VitalsRevision);
                if (updated.CurrentHp != currentHp ||
                    updated.CurrentMp != currentMp)
                {
                    updated.MarkVitalsChanged();
                }
            }
            updated.CheckpointOwnerId =
                current.CheckpointOwnerId;
            updated.CheckpointOwnerGeneration =
                current.CheckpointOwnerGeneration;
        }

        _character = updated;
    }

    private bool AcceptEnqueue(
        CharacterCheckpointEnqueueResult result,
        string facet)
    {
        if (result.Status is
                CharacterCheckpointEnqueueStatus.Accepted or
                CharacterCheckpointEnqueueStatus.Coalesced or
                CharacterCheckpointEnqueueStatus.IgnoredStale)
        {
            return true;
        }
        if (result.Status ==
            CharacterCheckpointEnqueueStatus.OwnershipLost)
        {
            _session.Disconnect();
        }
        if (result.Status is
                CharacterCheckpointEnqueueStatus.RevisionConflict or
                CharacterCheckpointEnqueueStatus.OwnershipLost)
        {
            throw new InvalidOperationException(
                $"The {facet} checkpoint was rejected: {result.Status}.");
        }

        Console.WriteLine(
            $"[checkpoint] {facet} deferred reason={result.Status}");
        return false;
    }

    private static bool RequireSatisfied(
        CharacterCheckpointWriteResult result,
        long revision,
        string facet)
    {
        if (result.Satisfies(revision))
        {
            return true;
        }

        throw new InvalidOperationException(
            $"The {facet} checkpoint barrier was rejected: " +
            $"{result.Status}.");
    }

    private static PlayerOwnershipFence GetCheckpointOwner(
        GameCharacter character)
    {
        if (!TryGetCheckpointOwner(character, out var owner))
        {
            throw new InvalidOperationException(
                "The character has no active checkpoint owner.");
        }
        return owner;
    }

    private static bool TryGetCheckpointOwner(
        GameCharacter character,
        out PlayerOwnershipFence owner)
    {
        owner = new PlayerOwnershipFence(
            character.CheckpointOwnerId,
            character.CheckpointOwnerGeneration);
        return character.CheckpointOwnerId != Guid.Empty &&
               character.CheckpointOwnerGeneration > 0;
    }
}
