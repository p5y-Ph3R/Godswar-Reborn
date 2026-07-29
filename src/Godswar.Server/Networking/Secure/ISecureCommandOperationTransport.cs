namespace Godswar.Server.Networking.Secure;

// Packet-boundary coordination is explicit because the secure outer channel
// carries an opaque encrypted byte stream. Raw legacy transports never
// implement this contract and therefore cannot supply an operation identity.
internal interface ISecureCommandOperationTransport
{
    void BeginPacketRead();

    Guid? CompletePacketRead(ushort packetLength, ushort opcode);

    void AbortPacketRead();
}
