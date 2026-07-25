using System.Net;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal enum SecureUdpBindingProcessOutcome : byte
{
    Rejected = 1,
    ChallengeCreated = 2,
    Bound = 3,
    AlreadyBound = 4,
    UnknownSession = 5,
    Expired = 6,
    InvalidProof = 7,
    EndpointConflict = 8,
    InvalidEndpoint = 9
}

internal readonly record struct SecureUdpBindingProcessResult(
    SecureUdpBindingProcessOutcome Outcome,
    int ResponseBytes,
    SecureBoundGamePrincipal? Principal)
{
    public bool HasResponse =>
        Outcome == SecureUdpBindingProcessOutcome.ChallengeCreated &&
        ResponseBytes == SecureUdpBindingConstants.DatagramBytes;
}

internal sealed class SecureUdpBindingCoordinator
{
    private readonly SecureUdpAddressValidation _addressValidation;
    private readonly SecureUdpSessionAuthority _sessions;

    public SecureUdpBindingCoordinator(
        SecureUdpAddressValidation addressValidation,
        SecureUdpSessionAuthority sessions)
    {
        _addressValidation = addressValidation ??
            throw new ArgumentNullException(nameof(addressValidation));
        _sessions = sessions ??
            throw new ArgumentNullException(nameof(sessions));
    }

    public SecureUdpBindingProcessResult ProcessDatagram(
        ReadOnlySpan<byte> datagram,
        IPEndPoint remoteEndpoint,
        Span<byte> responseDestination)
    {
        if (!SecureUdpBindingCodec.TryDecode(
                datagram,
                out var binding))
        {
            return Rejected();
        }

        if (binding.Type == SecureUdpBindingType.ClientHello)
        {
            if (!_addressValidation.TryCreateChallenge(
                    datagram,
                    remoteEndpoint,
                    responseDestination,
                    out var responseBytes) ||
                responseBytes !=
                    SecureUdpBindingConstants.DatagramBytes ||
                responseBytes > datagram.Length)
            {
                return Rejected();
            }

            return new SecureUdpBindingProcessResult(
                SecureUdpBindingProcessOutcome.ChallengeCreated,
                responseBytes,
                null);
        }

        // Slice 9A's cookie-only ClientProof is deliberately never sufficient
        // to associate an endpoint with an authenticated TLS game session.
        if (binding.Type !=
            SecureUdpBindingType.AuthenticatedClientProof)
        {
            return Rejected();
        }

        Span<byte> connectionId = stackalloc byte[
            SecureUdpBindingConstants.ConnectionIdBytes];
        Span<byte> challenge = stackalloc byte[
            SecureUdpBindingConstants.DatagramBytes];
        Span<byte> tlsProof = stackalloc byte[
            SecureUdpBindingConstants.TlsProofTagBytes];
        try
        {
            // The stateless endpoint cookie is verified before the bounded
            // TLS-session authority is touched.
            if (!_addressValidation.TryValidateAuthenticatedProofCookie(
                    datagram,
                    remoteEndpoint,
                    connectionId,
                    challenge,
                    tlsProof))
            {
                return Rejected();
            }

            var status = _sessions.TryBind(
                connectionId,
                challenge,
                tlsProof,
                remoteEndpoint,
                out var principal);
            return new SecureUdpBindingProcessResult(
                ToProcessOutcome(status),
                ResponseBytes: 0,
                principal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(connectionId);
            CryptographicOperations.ZeroMemory(challenge);
            CryptographicOperations.ZeroMemory(tlsProof);
        }
    }

    private static SecureUdpBindingProcessResult Rejected()
    {
        return new SecureUdpBindingProcessResult(
            SecureUdpBindingProcessOutcome.Rejected,
            ResponseBytes: 0,
            Principal: null);
    }

    private static SecureUdpBindingProcessOutcome ToProcessOutcome(
        SecureUdpSessionBindStatus status)
    {
        return status switch
        {
            SecureUdpSessionBindStatus.Bound =>
                SecureUdpBindingProcessOutcome.Bound,
            SecureUdpSessionBindStatus.AlreadyBound =>
                SecureUdpBindingProcessOutcome.AlreadyBound,
            SecureUdpSessionBindStatus.UnknownSession =>
                SecureUdpBindingProcessOutcome.UnknownSession,
            SecureUdpSessionBindStatus.Expired =>
                SecureUdpBindingProcessOutcome.Expired,
            SecureUdpSessionBindStatus.InvalidProof =>
                SecureUdpBindingProcessOutcome.InvalidProof,
            SecureUdpSessionBindStatus.EndpointConflict =>
                SecureUdpBindingProcessOutcome.EndpointConflict,
            SecureUdpSessionBindStatus.InvalidEndpoint =>
                SecureUdpBindingProcessOutcome.InvalidEndpoint,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
    }
}
