#pragma once

#include "SecureUdpBindingGrant.h"
#include "SecureUdpClientChannel.h"
#include "SecureRealtimeMovementRouter.h"

#include <WinSock2.h>
#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

class SecureOuterStream;

enum class SecureUdpClientWorkerState : std::uint8_t {
    Idle = 0,
    Starting,
    Binding,
    Active,
    TlsFallback,
    Stopping,
    Stopped,
    Failed,
};

enum class SecureUdpClientWorkerFailure : std::uint8_t {
    None = 0,
    InvalidArgument,
    Clock,
    Random,
    Channel,
    WinSock,
    Socket,
    Thread,
    Send,
    HandshakeTimeout,
    PeerTimeout,
    GameplayAcknowledgmentTimeout,
    TlsMovementWrite,
    StopDeadline,
};

struct SecureUdpClientWorkerSnapshot final {
    SecureUdpClientWorkerState state =
        SecureUdpClientWorkerState::Idle;
    SecureUdpClientWorkerFailure failure =
        SecureUdpClientWorkerFailure::None;
    int nativeError = 0;
    unsigned bindingAttempts = 0;
    unsigned rebinds = 0;
    std::uint64_t datagramsSent = 0;
    std::uint64_t datagramsReceived = 0;
    std::uint64_t oversizedDatagramsDropped = 0;
    SecureUdpClientChannelSnapshot channel{};
    SecureRealtimeMovementRouterSnapshot movement{};
};

// Owns one bounded UDP worker and the capacity-one movement mailbox. Origin's
// SendMsg thread only enqueues; all UDP and TLS-fallback I/O stays here.
class SecureUdpClientWorker final {
public:
    static constexpr DWORD StopDeadlineMilliseconds = 2'000;

    SecureUdpClientWorker() noexcept;
    ~SecureUdpClientWorker() noexcept;

    SecureUdpClientWorker(const SecureUdpClientWorker&) = delete;
    SecureUdpClientWorker& operator=(
        const SecureUdpClientWorker&) = delete;

    bool Start(
        SecureUdpBindingGrant* grant,
        const sockaddr* tlsPeer,
        int tlsPeerBytes,
        SecureOuterStream* outerStream = nullptr) noexcept;
    bool StopAndJoin(
        DWORD timeoutMilliseconds =
            StopDeadlineMilliseconds) noexcept;

    static DWORD BindingRetryDelayMilliseconds(
        unsigned completedAttempts) noexcept;
    SecureRealtimeMovementRouteResult RouteLegacyMovement(
        const void* packet,
        int packetBytes) noexcept;
    SecureUdpClientWorkerSnapshot Snapshot() const noexcept;

private:
    static DWORD WINAPI ThreadEntry(void* context) noexcept;
    DWORD Run() noexcept;
    bool OpenSocket() noexcept;
    void CloseSocket() noexcept;
    bool SendDatagram(
        const void* datagram,
        std::size_t datagramBytes) noexcept;
    bool DrainIncoming(std::uint64_t nowMilliseconds) noexcept;
    bool HandleIncoming(
        const void* datagram,
        std::size_t datagramBytes,
        std::uint64_t nowMilliseconds) noexcept;
    bool SendBindingHello(
        std::uint64_t nowMilliseconds) noexcept;
    bool SendKeepalive(
        std::uint64_t nowMilliseconds) noexcept;
    bool BeginRebind(std::uint64_t nowMilliseconds) noexcept;
    bool ProcessUdpMovement(
        std::uint64_t nowMilliseconds) noexcept;
    bool ProcessTlsMovement() noexcept;
    bool SwitchMovementToTls(
        SecureUdpClientWorkerFailure failure,
        int nativeError = 0) noexcept;
    bool ContinueTlsFallbackLoop() noexcept;
    void ConsumePositionSnapshot(
        std::uint64_t nowMilliseconds) noexcept;
    bool GenerateNonce(std::uint8_t* nonce) noexcept;
    bool GeneratePingId(std::uint64_t* pingId) noexcept;
    bool ShouldStop() const noexcept;
    void SetState(
        SecureUdpClientWorkerState state,
        SecureUdpClientWorkerFailure failure =
            SecureUdpClientWorkerFailure::None,
        int nativeError = 0) noexcept;
    void PublishChannel() noexcept;
    void PublishMovement() noexcept;
    void EnterTlsFallback(
        SecureUdpClientWorkerFailure failure,
        int nativeError = 0) noexcept;
    static bool CopyPeer(
        const sockaddr* source,
        int sourceBytes,
        std::uint16_t udpPort,
        sockaddr_storage* destination,
        int* destinationBytes) noexcept;

    mutable SRWLOCK lock_{};
    HANDLE stopEvent_ = nullptr;
    HANDLE thread_ = nullptr;
    SOCKET socket_ = INVALID_SOCKET;
    sockaddr_storage remote_{};
    int remoteBytes_ = 0;
    SecureUdpClientChannel channel_{};
    SecureRealtimeMovementRouter movementRouter_{};
    SecureUdpClientWorkerSnapshot published_{};
    SecureOuterStream* outerStream_ = nullptr;
    SecureRealtimeMovementInput pendingMovement_{};
    SecureRealtimeMovementInput retryMovement_{};
    bool hasPendingMovement_ = false;
    bool hasRetryMovement_ = false;
    std::uint64_t nextBindingSendMilliseconds_ = 0;
    std::uint64_t bindingDeadlineMilliseconds_ = 0;
    bool hasReachedActive_ = false;
};

} // namespace godswar::network
