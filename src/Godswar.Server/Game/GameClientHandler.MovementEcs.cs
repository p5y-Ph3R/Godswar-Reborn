using System.Buffers.Binary;
using Godswar.Server.Protocol;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly PlayerMovementEcsAdapter
        _playerMovementEcs = new();

    internal PlayerMovementEcsDecision?
        GetPlayerMovementEcsDiagnostics() =>
        _playerMovementEcs.Snapshot();

    private bool UpdateCharacterPositionFromWalkEcs(
        GamePacket packet,
        out AcceptedMapMovementSegment movement)
    {
        movement = default;
        if (_character is null || packet.Payload.Length < 12)
        {
            return false;
        }

        var previousX = _character.PositionX;
        var previousZ = _character.PositionZ;
        var mapId = _character.CurrentMap;

        // Payload bytes 0..3 are an opaque client movement-state word. The
        // server rewrites its low bits only for outbound world projection, but
        // no captured inbound evidence identifies a trustworthy source-object
        // field. Do not infer one here.
        var positionX = BinaryPrimitives.ReadSingleLittleEndian(
            packet.Payload.Slice(4, 4));
        var positionZ = BinaryPrimitives.ReadSingleLittleEndian(
            packet.Payload.Slice(8, 4));
        var decision = _playerMovementEcs.Evaluate(
            _character,
            _account?.Id ?? _character.AccountId,
            LocalPlayerObjectId,
            verifiedSourceObjectId: null,
            positionX,
            positionZ);
        if (!decision.Accepted)
        {
            Console.WriteLine(
                $"[world] ignored ECS walk character={_character.Name} reason={decision.RejectionReason} x={positionX} z={positionZ}");
            return false;
        }

        _character.PositionX = decision.CurrentX;
        _character.PositionZ = decision.CurrentZ;
        _character.MarkPositionChanged();
        _positionDirty = true;
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        movement = new AcceptedMapMovementSegment(
            mapId,
            new MapTraversalPosition(previousX, previousZ),
            new MapTraversalPosition(
                decision.CurrentX,
                decision.CurrentZ));
        return true;
    }

    private void ResetPlayerMovementEcs() =>
        _playerMovementEcs.Reset();
}
