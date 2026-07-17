using Godswar.Server.Packets;

namespace Godswar.Server.Protocol;

internal sealed class PacketCipher
{
    private static readonly byte[] HashOne = ReferencePackets.HashOne.ToArray();
    private static readonly byte[] HashTwo = ReferencePackets.HashTwo.ToArray();

    private int _pointer;

    public void Transform(Span<byte> packet)
    {
        for (var i = 0; i < packet.Length; i++)
        {
            packet[i] = (byte)((packet[i] ^ HashOne[_pointer]) ^ HashTwo[_pointer]);
            _pointer = (_pointer + 1) & 0xff;
        }
    }
}
