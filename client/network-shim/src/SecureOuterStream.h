#pragma once

#include "OpaqueDuplexPump.h"
#include "SchannelClientStream.h"
#include "SecureClientProtocol.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

enum class SecureOuterFailure : std::uint8_t {
    None = 0,
    InvalidArgument,
    InvalidState,
    PrefaceWrite,
    PrefaceRead,
    PrefaceRejected,
    FrameHeader,
    FrameBody,
    FrameSequenceExhausted,
    UnsupportedControl,
    PongWrite,
    LegacyWrite,
    OperationDeadline,
    Stopped,
};

struct SecureOuterSnapshot final {
    bool established = false;
    bool stopped = false;
    SecureOuterFailure failure = SecureOuterFailure::None;
    SecureEndpointRole role = SecureEndpointRole::Login;
    std::uint64_t nextInboundSequence = 1;
    std::uint64_t nextOutboundSequence = 1;
};

// Converts the secure Phase 2 preface/frame protocol into the exact opaque
// legacy byte stream consumed by NativeClientBridge. The caller owns the
// plaintext TLS stream and keeps it alive through this object's destruction.
class SecureOuterStream final : public IByteStream {
public:
    static constexpr DWORD PrefaceDeadlineMilliseconds = 2'000;
    static constexpr DWORD FrameHeaderDeadlineMilliseconds = 5'000;
    static constexpr DWORD FrameBodyDeadlineMilliseconds = 10'000;
    static constexpr DWORD WriteDeadlineMilliseconds = 5'000;
    static constexpr DWORD IdleDeadlineMilliseconds = 90'000;

    explicit SecureOuterStream(
        IDeadlinePlaintextStream* plaintextStream) noexcept;
    ~SecureOuterStream() noexcept;

    SecureOuterStream(const SecureOuterStream&) = delete;
    SecureOuterStream& operator=(const SecureOuterStream&) = delete;

    bool Establish(
        SecureEndpointRole role,
        const std::uint8_t* clientInstanceId,
        std::size_t clientInstanceIdBytes,
        const std::uint8_t* originSha256,
        std::size_t originSha256Bytes) noexcept;

    ByteStreamIoResult Read(
        void* destination,
        std::size_t destinationCapacity) noexcept override;
    ByteStreamIoResult Write(
        const void* source,
        std::size_t sourceBytes) noexcept override;
    void Stop() noexcept override;

    SecureOuterSnapshot Snapshot() const noexcept;

private:
    DeadlineStreamResult ReadExact(
        void* destination,
        std::size_t bytes,
        ULONGLONG firstDeadline,
        DWORD partialDeadlineMilliseconds) noexcept;
    bool WriteFrame(
        SecureFrameType type,
        const void* payload,
        std::size_t payloadBytes,
        ULONGLONG deadline) noexcept;
    void Fail(SecureOuterFailure failure) noexcept;
    bool IsStopped() const noexcept;

    IDeadlinePlaintextStream* plaintextStream_ = nullptr;
    SRWLOCK writeLock_{};
    mutable SRWLOCK snapshotLock_{};
    volatile LONG stopped_ = 0;
    volatile LONG failure_ = 0;
    bool established_ = false;
    SecureEndpointRole role_ = SecureEndpointRole::Login;
    std::uint64_t nextInboundSequence_ = 1;
    std::uint64_t nextOutboundSequence_ = 1;
    std::uint8_t inboundPayload_[SecureMaximumPayloadBytes]{};
    std::size_t inboundOffset_ = 0;
    std::size_t inboundBytes_ = 0;
};

} // namespace godswar::network
