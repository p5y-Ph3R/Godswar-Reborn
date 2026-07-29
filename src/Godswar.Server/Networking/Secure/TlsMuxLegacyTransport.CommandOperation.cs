namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransport
{
    public void BeginPacketRead()
    {
        lock (_packetOperationGate)
        {
            if (_packetReadActive)
            {
                RejectPacketAssociation(
                    "A secure legacy packet read was already active.");
            }

            _packetReadActive = true;
            _packetReadHasBytes = false;
            _packetOperation = null;
        }
    }

    public Guid? CompletePacketRead(
        ushort packetLength,
        ushort opcode)
    {
        lock (_packetOperationGate)
        {
            if (!_packetReadActive || !_packetReadHasBytes)
            {
                RejectPacketAssociation(
                    "A secure legacy packet completed outside an active packet boundary.");
            }

            var operation = _packetOperation;
            ClearPacketAssociation();
            if (operation is null)
            {
                return null;
            }
            if (operation.Value.PacketLength != packetLength ||
                operation.Value.Opcode != opcode)
            {
                RejectPacketAssociation(
                    "Secure operation metadata did not describe the next legacy packet.");
            }

            return operation.Value.OperationId;
        }
    }

    public void AbortPacketRead()
    {
        lock (_packetOperationGate)
        {
            ClearPacketAssociation();
        }
    }

    private void AcceptOperationMetadata(
        SecureLegacyCommandOperation operation)
    {
        lock (_packetOperationGate)
        {
            if (!_packetReadActive ||
                _packetReadHasBytes ||
                _packetOperation is not null)
            {
                RejectPacketAssociation(
                    "Secure operation metadata was duplicated or arrived inside a legacy packet.");
            }

            _packetOperation = operation;
        }
    }

    private void MarkPacketBytesRead()
    {
        lock (_packetOperationGate)
        {
            if (_packetReadActive)
            {
                _packetReadHasBytes = true;
            }
        }
    }

    private void ClearPacketAssociation()
    {
        _packetReadActive = false;
        _packetReadHasBytes = false;
        _packetOperation = null;
    }

    private void RejectPacketAssociation(string message)
    {
        ClearPacketAssociation();
        var error = new SecureTransportException(message);
        Fail(error);
        throw error;
    }
}
