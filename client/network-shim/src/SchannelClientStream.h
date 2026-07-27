#pragma once

#include "SocketHandle.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

enum class DeadlineStreamStatus : std::uint8_t {
    Success = 0,
    EndOfStream,
    TimedOut,
    Failed,
};

struct DeadlineStreamResult final {
    DeadlineStreamStatus status = DeadlineStreamStatus::Failed;
    std::size_t bytesTransferred = 0;
};

class IDeadlinePlaintextStream {
public:
    virtual DeadlineStreamResult Read(
        void* destination,
        std::size_t destinationBytes,
        ULONGLONG absoluteDeadline) noexcept = 0;
    virtual bool WriteAll(
        const void* source,
        std::size_t sourceBytes,
        ULONGLONG absoluteDeadline) noexcept = 0;
    virtual void Stop() noexcept = 0;

protected:
    ~IDeadlinePlaintextStream() = default;
};

enum class SchannelClientFailure : std::uint8_t {
    None = 0,
    InvalidArgument,
    SocketConfiguration,
    CredentialAcquisition,
    HandshakeRead,
    HandshakeWrite,
    HandshakeProtocol,
    HandshakeDeadline,
    ContextAttributes,
    AlpnPolicy,
    TlsPolicy,
    CertificatePolicy,
    RecordRead,
    RecordWrite,
    RenegotiationRejected,
    TruncatedStream,
    Stopped,
    PostHandshakeRead,
    PostHandshakeWrite,
    PostHandshakeProtocol,
    PostHandshakePolicy,
    PostHandshakeDeadline,
    PostHandshakeLimit,
};

enum class SchannelRevocationPolicy : std::uint8_t {
    Strict = 0,
    AllowMissingSourceForDevelopment,
    PinnedRootForDevelopment,
};

struct SchannelClientSnapshot final {
    bool valid = false;
    bool established = false;
    bool stopped = false;
    SchannelClientFailure failure = SchannelClientFailure::None;
    LONG securityStatus = 0;
    DWORD negotiatedProtocol = 0;
    DWORD negotiatedCipherSuite = 0;
};

inline constexpr std::size_t SchannelAlpnOfferBytes = 25;

bool IsValidSchannelTargetName(const wchar_t* targetName) noexcept;

bool TryBuildSchannelAlpnOffer(
    void* destination,
    std::size_t destinationBytes) noexcept;

bool IsAcceptedSchannelProtocolAndCipher(
    DWORD protocol,
    DWORD cipherSuite) noexcept;

bool HasRequiredSchannelStreamAttributes(
    ULONG returnedAttributes) noexcept;

DWORD GetSchannelCredentialFlags(
    SchannelRevocationPolicy revocationPolicy) noexcept;

// Owns an already-connected socket and exposes authenticated TLS plaintext.
// Stop only shuts down the descriptor; destruction and SSPI-handle release
// must occur after all concurrent Read/Write calls have returned.
class SchannelClientStream final : public IDeadlinePlaintextStream {
public:
    static constexpr DWORD DefaultHandshakeDeadlineMilliseconds = 5'000;
    static constexpr DWORD DefaultWriteDeadlineMilliseconds = 5'000;

    explicit SchannelClientStream(SocketHandle&& socket) noexcept;
    ~SchannelClientStream() noexcept;

    SchannelClientStream(const SchannelClientStream&) = delete;
    SchannelClientStream& operator=(const SchannelClientStream&) = delete;

    bool IsValid() const noexcept;
    bool Establish(
        const wchar_t* targetName,
        SchannelRevocationPolicy revocationPolicy,
        DWORD timeoutMilliseconds =
            DefaultHandshakeDeadlineMilliseconds) noexcept;

    DeadlineStreamResult Read(
        void* destination,
        std::size_t destinationBytes,
        ULONGLONG absoluteDeadline) noexcept override;
    bool WriteAll(
        const void* source,
        std::size_t sourceBytes,
        ULONGLONG absoluteDeadline) noexcept override;
    void Stop() noexcept override;

    SchannelClientSnapshot Snapshot() const noexcept;

private:
    struct State;
    State* state_ = nullptr;
};

} // namespace godswar::network
