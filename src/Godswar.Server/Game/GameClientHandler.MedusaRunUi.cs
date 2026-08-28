using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleMedusaLeaderPanelActionAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        const byte terminateAction = 0;
        if (packet.Length != 6 ||
            packet.Buffer.Length != 6 ||
            packet.Payload[0] != terminateAction)
        {
            Console.WriteLine(
                "[instance] ignored unsupported Medusa panel action " +
                $"len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        if (!_registry.TryTerminateMedusaRunFromLeader(
                _session,
                DateTimeOffset.UtcNow))
        {
            Console.WriteLine(
                "[instance] rejected non-authoritative Medusa " +
                "terminate action");
            return;
        }

        await _session.SendAsync(
            PacketBuilder.RepetitionReset(),
            cancellationToken,
            "MedusaLeaderInstanceTerminate");
    }

    private async Task HandleMedusaLeaderEndAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Length != 12 || packet.Buffer.Length != 12)
        {
            Console.Error.WriteLine(
                "[instance] rejected malformed Medusa end request");
            return;
        }

        var repetitionId = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload);
        var repetitionIndex = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(sizeof(int)));
        if (!_registry.TryEndMedusaRunFromLeader(
                _session,
                repetitionId,
                repetitionIndex,
                DateTimeOffset.UtcNow))
        {
            Console.WriteLine(
                "[instance] rejected non-authoritative Medusa end request " +
                $"repetition={repetitionId} index={repetitionIndex}");
            return;
        }

        await _session.SendAsync(
            PacketBuilder.RepetitionReset(),
            cancellationToken,
            "MedusaLeaderInstanceEnd");
    }
}
