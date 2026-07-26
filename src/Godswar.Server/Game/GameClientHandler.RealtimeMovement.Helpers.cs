using System.Buffers.Binary;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static byte[] BuildRealtimeLegacyMovement(
        uint state,
        float x,
        float z,
        float auxiliary,
        uint objectId)
    {
        var packet = new byte[
            SecureRealtimeMovementProtocol.LegacyWalkBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            SecureRealtimeMovementProtocol.LegacyWalkOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            state);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(12),
            z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16),
            auxiliary);
        return PacketBuilder.PlayerWorldMovement(
            packet,
            objectId);
    }

    private static async Task ObserveRealtimeTaskAsync(
        Task? task,
        string description)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Console.WriteLine(
                $"[realtime] {description} shutdown failed: {error.Message}");
        }
    }

    private static ulong IncrementSaturated(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;

    private static uint NextNonzero(uint value) =>
        value == uint.MaxValue
            ? throw new InvalidOperationException(
                "Realtime world generation exhausted.")
            : value + 1;

    private readonly record struct RealtimeMovementEffects(
        byte MapId,
        byte[]? ViewerMovement,
        byte[]? ReliableCorrection,
        RealtimePositionSave? PositionSave);
}
