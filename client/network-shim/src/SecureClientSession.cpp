#include "SecureClientSession.h"

#include <new>

namespace godswar::network {
namespace {

constexpr DWORD ExternalConnectDeadlineMilliseconds = 5'000;

bool CopyAsciiHost(
    const char* source,
    std::size_t sourceBytes,
    wchar_t* destination,
    std::size_t destinationCapacity) noexcept {
    if (source == nullptr ||
        sourceBytes == 0 ||
        sourceBytes > EndpointManifestMaximumDnsBytes ||
        destination == nullptr ||
        destinationCapacity <= sourceBytes) {
        return false;
    }

    for (std::size_t index = 0; index < sourceBytes; ++index) {
        const auto value =
            static_cast<unsigned char>(source[index]);
        if (value == 0 || value > 0x7F) {
            return false;
        }
        destination[index] = static_cast<wchar_t>(value);
    }
    destination[sourceBytes] = L'\0';
    return IsValidSchannelTargetName(destination);
}

} // namespace

SecureClientSession::SecureClientSession(
    const SecureClientSessionConfiguration& configuration) noexcept
    : configuration_(configuration) {
}

SecureClientSession::~SecureClientSession() noexcept {
    Disconnect();
    SecureZeroMemory(
        configuration_.clientInstanceId,
        sizeof(configuration_.clientInstanceId));
    SecureZeroMemory(
        configuration_.originSha256,
        sizeof(configuration_.originSha256));
}

bool SecureClientSession::Connect(
    ILegacyNetClient* legacyClient,
    const ClientBridgePlan& plan) noexcept {
    if (legacyClient == nullptr ||
        plan.proxyId == 0 ||
        plan.generation == 0 ||
        (plan.role != ClientEndpointRole::Login &&
            plan.role != ClientEndpointRole::Game) ||
        plan.role != RoleForDecision(plan.decision) ||
        configuration_.grantRegistry == nullptr ||
        !IsNonzero(
            configuration_.clientInstanceId,
            sizeof(configuration_.clientInstanceId)) ||
        !IsNonzero(
            configuration_.originSha256,
            sizeof(configuration_.originSha256))) {
        Fail(SecureClientSessionFailure::InvalidArgument);
        return false;
    }
    if (state_ != SecureClientSessionState::Idle ||
        tls_ != nullptr ||
        outer_ != nullptr ||
        bridge_ != nullptr) {
        Fail(SecureClientSessionFailure::InvalidState);
        return false;
    }

    state_ = SecureClientSessionState::Connecting;
    role_ = plan.role;
    wchar_t tlsHost[EndpointManifestMaximumDnsBytes + 1]{};
    std::uint16_t tlsPort = 0;
    if (!PrepareTarget(
            plan,
            tlsHost,
            sizeof(tlsHost) / sizeof(tlsHost[0]),
            &tlsPort)) {
        return false;
    }

    SocketHandle connectedSocket;
    if (!ConnectExternalTcp(
            tlsHost,
            tlsPort,
            ExternalConnectDeadlineMilliseconds,
            &connectedSocket,
            &tcpSnapshot_)) {
        Fail(SecureClientSessionFailure::TcpConnect);
        return false;
    }

    tls_ = new (std::nothrow) SchannelClientStream(
        static_cast<SocketHandle&&>(connectedSocket));
    if (tls_ == nullptr || !tls_->IsValid()) {
        Fail(SecureClientSessionFailure::TlsAllocation);
        return false;
    }
    if (!tls_->Establish(tlsHost)) {
        Fail(SecureClientSessionFailure::TlsHandshake);
        return false;
    }

    outer_ = new (std::nothrow) SecureOuterStream(
        tls_,
        configuration_.grantRegistry);
    if (outer_ == nullptr) {
        Fail(SecureClientSessionFailure::OuterAllocation);
        return false;
    }

    const auto endpointRole =
        role_ == ClientEndpointRole::Login
            ? SecureEndpointRole::Login
            : SecureEndpointRole::Game;
    if (!outer_->Establish(
            endpointRole,
            configuration_.clientInstanceId,
            sizeof(configuration_.clientInstanceId),
            configuration_.originSha256,
            sizeof(configuration_.originSha256))) {
        Fail(SecureClientSessionFailure::OuterPreface);
        return false;
    }
    if (role_ == ClientEndpointRole::Game &&
        !BeginGamePresentation()) {
        return false;
    }

    bridge_ = new (std::nothrow) NativeClientBridge();
    if (bridge_ == nullptr) {
        Fail(SecureClientSessionFailure::BridgeAllocation);
        return false;
    }
    if (!bridge_->Start(legacyClient, outer_)) {
        Fail(SecureClientSessionFailure::BridgeStart);
        return false;
    }

    legacyClient_ = legacyClient;
    state_ = SecureClientSessionState::Connected;
    return true;
}

bool SecureClientSession::Poll() noexcept {
    if (state_ != SecureClientSessionState::Connected ||
        bridge_ == nullptr) {
        return false;
    }

    const auto snapshot = bridge_->Snapshot();
    if (snapshot.state == NativeBridgeState::Running &&
        snapshot.failure == NativeBridgeFailure::None) {
        return true;
    }

    Fail(SecureClientSessionFailure::BridgeTerminated);
    return false;
}

void SecureClientSession::Disconnect() noexcept {
    ReleaseClaim();
    if (bridge_ != nullptr &&
        !bridge_->StopAndJoin(
            NativeClientBridge::DefaultOperationDeadlineMilliseconds)) {
        state_ = SecureClientSessionState::Failed;
        failure_ = SecureClientSessionFailure::BridgeJoin;
        RaiseFailFastException(nullptr, nullptr, 0);
    }

    const bool disconnectStock =
        legacyClient_ != nullptr;
    DestroyTransport(disconnectStock);
    if (state_ != SecureClientSessionState::Failed) {
        state_ = SecureClientSessionState::Stopped;
    }
}

SecureClientSessionSnapshot SecureClientSession::Snapshot() const noexcept {
    SecureClientSessionSnapshot snapshot{};
    snapshot.state = state_;
    snapshot.failure = failure_;
    snapshot.role = role_;
    snapshot.hasGameClaim = claimActive_;
    snapshot.tcp = tcpSnapshot_;
    if (tls_ != nullptr) {
        snapshot.tls = tls_->Snapshot();
    }
    if (outer_ != nullptr) {
        snapshot.outer = outer_->Snapshot();
    }
    if (bridge_ != nullptr) {
        snapshot.bridge = bridge_->Snapshot();
    }
    return snapshot;
}

bool SecureClientSession::PrepareTarget(
    const ClientBridgePlan& plan,
    wchar_t* tlsHost,
    std::size_t tlsHostCapacity,
    std::uint16_t* tlsPort) noexcept {
    if (tlsHost == nullptr ||
        tlsPort == nullptr ||
        tlsHostCapacity == 0) {
        Fail(SecureClientSessionFailure::InvalidArgument);
        return false;
    }
    *tlsPort = 0;

    if (plan.role == ClientEndpointRole::Login) {
        const auto& configuredHost =
            configuration_.manifest.tlsLoginHost;
        if (configuration_.manifest.tlsLoginPort == 0 ||
            !CopyAsciiHost(
                configuredHost.bytes,
                configuredHost.length,
                tlsHost,
                tlsHostCapacity)) {
            Fail(SecureClientSessionFailure::TargetName);
            return false;
        }
        *tlsPort = configuration_.manifest.tlsLoginPort;
        return true;
    }

    if (configuration_.grantRegistry->Claim(
            plan.proxyId,
            plan.generation,
            plan.logicalRoute,
            &claim_) != SecureGameGrantResult::Success) {
        Fail(SecureClientSessionFailure::GameClaim);
        return false;
    }
    claimActive_ = true;

    SecureGameGrantTarget target{};
    if (configuration_.grantRegistry->TryCopyClaimedTarget(
            claim_,
            &target) != SecureGameGrantResult::Success) {
        Fail(SecureClientSessionFailure::GameTarget);
        return false;
    }
    if (!CopyAsciiHost(
            target.tlsHost,
            target.tlsHostLength,
            tlsHost,
            tlsHostCapacity)) {
        Fail(SecureClientSessionFailure::TargetName);
        return false;
    }
    *tlsPort = target.tlsPort;
    return true;
}

bool SecureClientSession::BeginGamePresentation() noexcept {
    SecureGameGrant grant;
    if (!claimActive_ ||
        configuration_.grantRegistry->BeginPresentation(
            claim_,
            &grant) != SecureGameGrantResult::Success) {
        Fail(SecureClientSessionFailure::GamePresentation);
        return false;
    }

    claimActive_ = false;
    claim_ = SecureGameGrantClaim{};
    if (!outer_->PresentGameBind(&grant)) {
        Fail(SecureClientSessionFailure::GameBind);
        return false;
    }
    return true;
}

void SecureClientSession::Fail(
    SecureClientSessionFailure failure) noexcept {
    if (failure_ == SecureClientSessionFailure::None) {
        failure_ = failure;
    }
    state_ = SecureClientSessionState::Failed;
    ReleaseClaim();

    bool disconnectStock = false;
    if (bridge_ != nullptr) {
        static_cast<void>(bridge_->StopAndJoin(
            NativeClientBridge::DefaultOperationDeadlineMilliseconds));
        // legacyClient_ is assigned only after a successful bridge Start.
        // A failed Start already owns any stock disconnect it attempted.
        disconnectStock = legacyClient_ != nullptr;
    }
    DestroyTransport(disconnectStock);
}

void SecureClientSession::ReleaseClaim() noexcept {
    if (claimActive_ && configuration_.grantRegistry != nullptr) {
        static_cast<void>(
            configuration_.grantRegistry->ReturnUnpresented(claim_));
    }
    claimActive_ = false;
    claim_ = SecureGameGrantClaim{};
}

void SecureClientSession::DestroyTransport(
    bool disconnectStock) noexcept {
    auto* legacyClient = legacyClient_;
    legacyClient_ = nullptr;
    if (disconnectStock && legacyClient != nullptr) {
        legacyClient->DisConnect();
    }

    delete bridge_;
    bridge_ = nullptr;
    delete outer_;
    outer_ = nullptr;
    delete tls_;
    tls_ = nullptr;
}

bool SecureClientSession::IsNonzero(
    const std::uint8_t* bytes,
    std::size_t byteCount) noexcept {
    if (bytes == nullptr || byteCount == 0) {
        return false;
    }
    std::uint8_t combined = 0;
    for (std::size_t index = 0; index < byteCount; ++index) {
        combined |= bytes[index];
    }
    return combined != 0;
}

} // namespace godswar::network
