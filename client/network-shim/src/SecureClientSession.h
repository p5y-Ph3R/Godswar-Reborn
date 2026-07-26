#pragma once

#include "ClientRoute.h"
#include "EndpointManifest.h"
#include "ExternalTcpConnector.h"
#include "LegacyClientApi.h"
#include "NativeClientBridge.h"
#include "SchannelClientStream.h"
#include "SecureGameGrantRegistry.h"
#include "SecureOuterStream.h"
#include "SecureUdpClientWorker.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t SecureSessionClientInstanceIdBytes = 16;
inline constexpr std::size_t SecureSessionOriginSha256Bytes = 32;

struct SecureClientSessionConfiguration final {
    EndpointManifest manifest{};
    SecureGameGrantRegistry* grantRegistry = nullptr;
    std::uint8_t
        clientInstanceId[SecureSessionClientInstanceIdBytes]{};
    std::uint8_t originSha256[SecureSessionOriginSha256Bytes]{};
};

enum class SecureClientSessionState : std::uint8_t {
    Idle = 0,
    Connecting,
    Connected,
    Stopped,
    Failed,
};

enum class SecureClientSessionFailure : std::uint8_t {
    None = 0,
    InvalidArgument,
    InvalidState,
    GameClaim,
    GameTarget,
    TargetName,
    TcpConnect,
    TlsAllocation,
    TlsHandshake,
    OuterAllocation,
    OuterPreface,
    GamePresentation,
    GameBind,
    BridgeAllocation,
    BridgeStart,
    BridgeTerminated,
    BridgeJoin,
    UdpJoin,
};

struct SecureClientSessionSnapshot final {
    SecureClientSessionState state = SecureClientSessionState::Idle;
    SecureClientSessionFailure failure =
        SecureClientSessionFailure::None;
    ClientEndpointRole role = ClientEndpointRole::None;
    bool hasGameClaim = false;
    ExternalTcpConnectSnapshot tcp{};
    SchannelClientSnapshot tls{};
    SecureOuterSnapshot outer{};
    NativeClientBridgeSnapshot bridge{};
    bool hasUdpWorker = false;
    SecureUdpClientWorkerSnapshot udp{};
};

// Owns one secure outer connection and the loopback bridge used by the stock
// client. It never falls back to the logical raw endpoint. The caller must
// serialize Connect, Poll, Disconnect, and destruction with the stock ABI.
// RouteLegacyMovement additionally protects worker ownership so SendMsg
// cannot observe a worker being detached by teardown.
class SecureClientSession final {
public:
    explicit SecureClientSession(
        const SecureClientSessionConfiguration& configuration) noexcept;
    ~SecureClientSession() noexcept;

    SecureClientSession(const SecureClientSession&) = delete;
    SecureClientSession& operator=(const SecureClientSession&) = delete;

    bool Connect(
        ILegacyNetClient* legacyClient,
        const ClientBridgePlan& plan) noexcept;
    bool Poll() noexcept;
    void Disconnect() noexcept;
    SecureRealtimeMovementRouteResult RouteLegacyMovement(
        const void* packet,
        int packetBytes) noexcept;

    SecureClientSessionSnapshot Snapshot() const noexcept;
    // UDP status never decides whether the reliable TLS bridge survives.
    // Authoritative movement can change owner to its asynchronous TLS
    // fallback while the bridge remains healthy.
    static bool ShouldContinueTlsBridge(
        const NativeClientBridgeSnapshot& bridge,
        const SecureUdpClientWorkerSnapshot* udp) noexcept;

private:
    bool PrepareTarget(
        const ClientBridgePlan& plan,
        wchar_t* tlsHost,
        std::size_t tlsHostCapacity,
        std::uint16_t* tlsPort) noexcept;
    bool BeginGamePresentation() noexcept;
    void TryStartUdpWorker() noexcept;
    SecureUdpClientWorker* DetachUdpWorker() noexcept;
    void Fail(SecureClientSessionFailure failure) noexcept;
    void ReleaseClaim() noexcept;
    void DestroyTransport(bool disconnectStock) noexcept;
    static bool IsNonzero(
        const std::uint8_t* bytes,
        std::size_t byteCount) noexcept;

    SecureClientSessionConfiguration configuration_{};
    SecureClientSessionState state_ = SecureClientSessionState::Idle;
    SecureClientSessionFailure failure_ =
        SecureClientSessionFailure::None;
    ClientEndpointRole role_ = ClientEndpointRole::None;
    SecureGameGrantClaim claim_{};
    bool claimActive_ = false;
    ILegacyNetClient* legacyClient_ = nullptr;
    ExternalTcpConnectSnapshot tcpSnapshot_{};
    SchannelClientStream* tls_ = nullptr;
    SecureOuterStream* outer_ = nullptr;
    NativeClientBridge* bridge_ = nullptr;
    SecureUdpClientWorker* udpWorker_ = nullptr;
    mutable SRWLOCK udpWorkerLock_{};
    sockaddr_storage tlsPeer_{};
    int tlsPeerBytes_ = 0;
    bool udpGrantHandled_ = false;
};

} // namespace godswar::network
