using Godswar.Server.Application.Characters;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task PublishPetOwnerMergeStartedAsync(
        PetBootstrapSnapshot pet,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        _registry.SetPetOwnerMergePresentation(
            _session,
            active: true,
            pet.Aptitude,
            pet.CompletedRebirths);

        await _session.SendAsync(
            PacketBuilder.PetEnergy(
                pet.CurrentEnergy,
                pet.MaximumEnergy),
            cancellationToken,
            "PetOwnerMergeEnergyStart");
        await _session.SendAsync(
            PacketBuilder.PetOwnerMergeStarted(
                LocalPlayerObjectId,
                pet.Aptitude,
                pet.CompletedRebirths),
            cancellationToken,
            "PetOwnerMergeStarted");
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PetOwnerMergeStatusRefresh");

        await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.PetOwnerMergeStarted(
                CurrentPlayerObjectId,
                pet.Aptitude,
                pet.CompletedRebirths),
            cancellationToken,
            _session,
            "PetOwnerMergeStartedWorld");
        await BroadcastPetOwnerMergeStatusAsync(
            "PetOwnerMergeStartedStatusWorld",
            cancellationToken);
        StartPetOwnerMergeEnergyDrain();
    }

    private async Task PublishPetOwnerMergeEndedAsync(
        PetBootstrapSnapshot pet,
        bool restoreCompanion,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        _registry.SetPetOwnerMergePresentation(_session, active: false);

        await _session.SendAsync(
            PacketBuilder.PetEnergy(
                pet.CurrentEnergy,
                pet.MaximumEnergy),
            cancellationToken,
            "PetOwnerMergeEnergyEnd");
        await _session.SendAsync(
            PacketBuilder.PetOwnerMergeEnded(LocalPlayerObjectId),
            cancellationToken,
            "PetOwnerMergeEnded");
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PetOwnerMergeEndedStatusRefresh");

        await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.PetOwnerMergeEnded(
                CurrentPlayerObjectId),
            cancellationToken,
            _session,
            "PetOwnerMergeEndedWorld");
        await BroadcastPetOwnerMergeStatusAsync(
            "PetOwnerMergeEndedStatusWorld",
            cancellationToken);

        if (restoreCompanion && pet.IsCarried && pet.IsSummoned)
        {
            var petId = checked((uint)pet.PetId);
            await _session.SendAsync(
                PacketBuilder.PetOperationResult(
                    petId,
                    PetOperationResultCode.CallOutSucceeded),
                cancellationToken,
                "PetOwnerMergeCompanionRestore");
            await _session.SendAsync(
                PacketBuilder.PetWorldPresence(
                    petId,
                    LocalPlayerObjectId),
                cancellationToken,
                "PetOwnerMergeWorldPresenceRestore");
        }

        StartPetOwnerMergeEnergyRecharge();
    }

    private async Task BroadcastPetOwnerMergeStatusAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var status = await _registry.GetStatusSnapshotAsync(
            _session,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.RemotePlayerStatusUpdate(
                _character,
                CurrentPlayerObjectId,
                status.Aggregate,
                _registry.TrainingDummySpawnPkMode(
                    _character)),
            cancellationToken,
            _session,
            reason);
    }
}
