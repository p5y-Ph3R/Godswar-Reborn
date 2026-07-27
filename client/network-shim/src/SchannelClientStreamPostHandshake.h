#pragma once

#define SECURITY_WIN32
#define SCHANNEL_USE_BLACKLISTS

#include <Windows.h>
#include <winternl.h>
#include <security.h>
#include <schannel.h>
#include <wincrypt.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network::schannel_detail {

inline constexpr std::uint8_t RequiredAlpn[] = {
    'g', 'o', 'd', 's', 'w', 'a', 'r',
    '-', 's', 'h', 'i', 'm', '/', '1',
};

inline constexpr ULONG RequestedContextAttributes =
    ISC_REQ_REPLAY_DETECT |
    ISC_REQ_SEQUENCE_DETECT |
    ISC_REQ_CONFIDENTIALITY |
    ISC_REQ_INTEGRITY |
    ISC_REQ_ALLOCATE_MEMORY |
    ISC_REQ_STREAM;

inline constexpr unsigned MaximumPostHandshakeSteps = 64;
inline constexpr unsigned MaximumPostHandshakeTransitionsPerRead = 16;
inline constexpr std::size_t MaximumPostHandshakeOutputBytes = 64 * 1024;

bool IsTls13PostHandshakeRequest(
    DWORD negotiatedProtocol,
    SECURITY_STATUS status) noexcept;

bool AreTls13PostHandshakeParametersUnchanged(
    DWORD establishedProtocol,
    DWORD establishedCipherSuite,
    DWORD currentProtocol,
    DWORD currentCipherSuite) noexcept;

// Schannel can clear its queryable ALPN result after consuming a TLS 1.3
// NewSessionTicket on the same context. Accept that exact empty result only
// when the original handshake already validated the required ALPN.
bool IsAcceptedTls13PostHandshakeAlpn(
    bool establishedAlpnValidated,
    SECURITY_STATUS queryStatus,
    const SecPkgContext_ApplicationProtocol&
        queriedAlpn) noexcept;

// Selects the exact continuation token described by the buffers that
// DecryptMessage modified after SEC_I_RENEGOTIATE. An EXTRA slice wins;
// otherwise the allowed, nonempty slices must form one contiguous range.
bool TryPrepareSchannelPostHandshakeToken(
    const SecBuffer* buffers,
    std::size_t bufferCount,
    void* encryptedInput,
    std::size_t encryptedInputBytes,
    std::size_t encryptedInputCapacity,
    std::size_t* tokenBytes) noexcept;

// Retains zero or one SECBUFFER_EXTRA region at the beginning of the
// original encrypted-input allocation. Schannel may report only cbBuffer,
// so a null pvBuffer denotes that many trailing input bytes. Multiple,
// empty, or out-of-range regions are rejected.
bool TryRetainSchannelExtraBuffer(
    const SecBuffer* buffers,
    std::size_t bufferCount,
    void* encryptedInput,
    std::size_t encryptedInputBytes,
    std::size_t encryptedInputCapacity,
    bool* found,
    std::size_t* retainedBytes) noexcept;

bool IsAcceptedSchannelCertificate(
    PCCERT_CONTEXT certificate) noexcept;

} // namespace godswar::network::schannel_detail
