#pragma once

#include "LegacyClientApi.h"
#include "LoopbackAcceptor.h"
#include "OpaqueDuplexPump.h"
#include "WinSocketByteStream.h"

#include <Windows.h>

#include <cstdint>

namespace godswar::network {

enum class NativeBridgeState : std::uint8_t {
    Idle = 0,
    Starting,
    Running,
    Stopping,
    JoinPending,
    Stopped,
    Failed,
};

enum class NativeBridgeFailure : std::uint8_t {
    None = 0,
    InvalidArgument,
    InvalidState,
    ListenerOpen,
    AcceptStart,
    StockConnect,
    AcceptComplete,
    LocalStreamAllocation,
    PumpAllocation,
    PumpStart,
    PumpTerminated,
    OperationDeadline,
    JoinDeadline,
};

struct NativeClientBridgeSnapshot final {
    NativeBridgeState state = NativeBridgeState::Idle;
    NativeBridgeFailure failure = NativeBridgeFailure::None;
    LoopbackAcceptFailure acceptFailure =
        LoopbackAcceptFailure::None;
    bool hasPump = false;
    bool stockDisconnectIssued = false;
    OpaqueDuplexPumpSnapshot pump{};
};

// Joins one already-established outer stream to the stock client's opaque
// loopback byte stream. The caller owns both `legacyClient` and `outerStream`
// and must keep them alive until StopAndJoin completes. A failed Start owns
// startup cleanup and issues at most one stock DisConnect after any attempted
// stock Connect. After a successful Start, the caller stops the bridge first
// and then owns the one stock DisConnect call.
class NativeClientBridge final {
public:
    static constexpr DWORD DefaultOperationDeadlineMilliseconds = 5'000;

    explicit NativeClientBridge(
        const BoundedChunkQueueLimits& queueLimits =
            BoundedChunkQueueLimits{}) noexcept;
    ~NativeClientBridge() noexcept;

    NativeClientBridge(const NativeClientBridge&) = delete;
    NativeClientBridge& operator=(const NativeClientBridge&) = delete;

    // Success is refused after the deadline. The proprietary synchronous
    // Connect call cannot be preempted, so a late return is cleaned up and
    // reported as failure rather than turning this into a raw fallback.
    bool Start(
        ILegacyNetClient* legacyClient,
        IByteStream* outerStream,
        DWORD startupDeadlineMilliseconds =
            DefaultOperationDeadlineMilliseconds) noexcept;
    bool StopAndJoin(
        DWORD timeoutMilliseconds =
            DefaultOperationDeadlineMilliseconds) noexcept;

    NativeClientBridgeSnapshot Snapshot() const noexcept;

private:
    void RecordStartFailure(
        NativeBridgeFailure failure,
        LoopbackAcceptFailure acceptFailure,
        ILegacyNetClient* legacyClient,
        IByteStream* outerStream,
        bool stockConnectAttempted) noexcept;
    static DWORD RemainingWait(
        ULONGLONG deadline,
        DWORD timeoutMilliseconds) noexcept;

    mutable SRWLOCK lock_{};
    BoundedChunkQueueLimits queueLimits_{};
    NativeBridgeState state_ = NativeBridgeState::Idle;
    NativeBridgeFailure failure_ = NativeBridgeFailure::None;
    LoopbackAcceptFailure acceptFailure_ =
        LoopbackAcceptFailure::None;
    bool stockDisconnectIssued_ = false;
    HANDLE transitionEvent_ = nullptr;
    IByteStream* outerStream_ = nullptr;
    WinSocketByteStream* localStream_ = nullptr;
    OpaqueDuplexPump* pump_ = nullptr;
};

} // namespace godswar::network
