#pragma once

#include "OpaqueDuplexPump.h"
#include "SchannelClientStream.h"
#include "SecureClientProtocol.h"
#include "SecureGameControl.h"
#include "SecureGameGrantRegistry.h"
#include "SecureRealtimeMovementProtocol.h"
#include "SecureUdpBindingGrant.h"

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
    GrantDecode,
    GrantCommit,
    BindWrite,
    BindResult,
    BindRejected,
    OperationDeadline,
    Stopped,
    UdpGrantDecode,
    UdpGrantState,
    UdpGrantConnection,
    RealtimeMovementWrite,
};

struct SecureOuterSnapshot final {
    bool established = false;
    bool gameBound = false;
    bool hasUdpBindingGrant = false;
    bool stopped = false;
    SecureOuterFailure failure = SecureOuterFailure::None;
    SecureEndpointRole role = SecureEndpointRole::Login;
    std::uint64_t nextInboundSequence = 1;
    std::uint64_t nextOutboundSequence = 1;
};

// Converts the secure Phase 2 preface/frame protocol into the exact opaque
// legacy byte stream consumed by NativeClientBridge. The caller owns the
// plaintext TLS stream and optional grant registry, and keeps both alive
// through this object's destruction.
class SecureOuterStream final : public IByteStream {
public:
    static constexpr DWORD PrefaceDeadlineMilliseconds = 2'000;
    static constexpr DWORD FrameHeaderDeadlineMilliseconds = 5'000;
    static constexpr DWORD FrameBodyDeadlineMilliseconds = 10'000;
    static constexpr DWORD WriteDeadlineMilliseconds = 5'000;
    static constexpr DWORD GameBindDeadlineMilliseconds = 5'000;
    static constexpr DWORD IdleDeadlineMilliseconds = 90'000;

    explicit SecureOuterStream(
        IDeadlinePlaintextStream* plaintextStream,
        SecureGameGrantRegistry* grantRegistry = nullptr) noexcept;
    ~SecureOuterStream() noexcept;

    SecureOuterStream(const SecureOuterStream&) = delete;
    SecureOuterStream& operator=(const SecureOuterStream&) = delete;

    bool Establish(
        SecureEndpointRole role,
        const std::uint8_t* clientInstanceId,
        std::size_t clientInstanceIdBytes,
        const std::uint8_t* originSha256,
        std::size_t originSha256Bytes) noexcept;
    bool PresentGameBind(SecureGameGrant* grant) noexcept;

    ByteStreamIoResult Read(
        void* destination,
        std::size_t destinationCapacity) noexcept override;
    ByteStreamIoResult Write(
        const void* source,
        std::size_t sourceBytes) noexcept override;
    void Stop() noexcept override;

    SecureOuterSnapshot Snapshot() const noexcept;
    bool TryCopyConnectionId(
        std::uint8_t* destination,
        std::size_t destinationBytes) const noexcept;

    // Transfers the single retained proof key to a future UDP worker. The
    // caller owns and must clear the returned grant.
    bool TryTakeUdpBindingGrant(
        SecureUdpBindingGrant* grant) noexcept;
    // Called only by the realtime worker. The game-facing SendMsg path
    // enqueues and returns without waiting for TLS I/O.
    bool WriteRealtimeMovementInput(
        const SecureRealtimeMovementInput& movement) noexcept;

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
    void InvalidateUnexposedGrant() noexcept;
    void ClearUdpBindingState() noexcept;
    bool TryRetainUdpBindingGrant(
        const void* payload,
        std::size_t payloadBytes,
        SecureOuterFailure* failure) noexcept;
    bool IsStopped() const noexcept;

    IDeadlinePlaintextStream* plaintextStream_ = nullptr;
    SecureGameGrantRegistry* grantRegistry_ = nullptr;
    SRWLOCK writeLock_{};
    mutable SRWLOCK snapshotLock_{};
    volatile LONG stopped_ = 0;
    volatile LONG failure_ = 0;
    bool established_ = false;
    bool gameBound_ = false;
    bool connectionIdRetained_ = false;
    bool udpBindingGrantReceived_ = false;
    bool udpBindingGrantAvailable_ = false;
    bool grantCommitted_ = false;
    bool grantExposed_ = false;
    std::uint64_t committedGrantGeneration_ = 0;
    SecureEndpointRole role_ = SecureEndpointRole::Login;
    std::uint64_t nextInboundSequence_ = 1;
    std::uint64_t nextOutboundSequence_ = 1;
    std::uint8_t connectionId_[SecureUdpConnectionIdBytes]{};
    SecureUdpBindingGrant udpBindingGrant_{};
    std::uint8_t inboundPayload_[SecureMaximumPayloadBytes]{};
    std::size_t inboundOffset_ = 0;
    std::size_t inboundBytes_ = 0;
};

} // namespace godswar::network
