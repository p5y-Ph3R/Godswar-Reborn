#include "SecureUdpClientChannel.h"

#include <Windows.h>

#include <cstring>
#include <limits>

namespace godswar::network {

SecureUdpClientChannel::~SecureUdpClientChannel() noexcept {
    Stop();
}

bool SecureUdpClientChannel::Initialize(
    SecureUdpBindingGrant* grant,
    const std::uint8_t* clientNonce,
    std::size_t clientNonceBytes,
    std::uint64_t nowUnixMilliseconds,
    std::uint64_t nowMonotonicMilliseconds) noexcept {
    if (grant == nullptr ||
        !grant->IsValid() ||
        clientNonce == nullptr ||
        clientNonceBytes != sizeof(clientNonce_) ||
        !IsNonzero(clientNonce, clientNonceBytes) ||
        nowUnixMilliseconds == 0 ||
        state_ != SecureUdpClientChannelState::Idle) {
        Fail(SecureUdpClientChannelFailure::InvalidArgument);
        return false;
    }
    if (grant->ExpiryUnixMilliseconds() <= nowUnixMilliseconds) {
        grant->Clear();
        Fail(SecureUdpClientChannelFailure::ExpiredGrant);
        return false;
    }

    udpPort_ = grant->UdpPort();
    serverId_ = grant->ServerId();
    if (!grant->TryCopyConnectionId(
            connectionId_,
            sizeof(connectionId_)) ||
        !grant->TryCopyProofKey(
            proofKey_,
            sizeof(proofKey_))) {
        grant->Clear();
        Fail(SecureUdpClientChannelFailure::GrantSecret);
        return false;
    }
    grant->Clear();
    std::memcpy(clientNonce_, clientNonce, sizeof(clientNonce_));
    expectedBindingRevision_ = 1;
    sendEpoch_ = 1;
    receiveEpoch_ = 1;
    sendEpochStartedMilliseconds_ = nowMonotonicMilliseconds;
    lastSendMilliseconds_ = nowMonotonicMilliseconds;
    state_ = SecureUdpClientChannelState::AwaitingChallenge;
    return true;
}

bool SecureUdpClientChannel::BeginRebind(
    const std::uint8_t* clientNonce,
    std::size_t clientNonceBytes) noexcept {
    if (state_ != SecureUdpClientChannelState::Active ||
        clientNonce == nullptr ||
        clientNonceBytes != sizeof(clientNonce_) ||
        !IsNonzero(clientNonce, clientNonceBytes) ||
        bindingRevision_ ==
            (std::numeric_limits<std::uint64_t>::max)()) {
        return false;
    }

    std::memcpy(clientNonce_, clientNonce, sizeof(clientNonce_));
    expectedBindingRevision_ = bindingRevision_ + 1;
    if (pingPending_) {
        IncrementSaturated(&lostPings_);
    }
    pingPending_ = false;
    SecureZeroMemory(pendingPing_, sizeof(pendingPing_));
    pendingPingSentMilliseconds_ = 0;
    state_ = SecureUdpClientChannelState::AwaitingChallenge;
    return true;
}

bool SecureUdpClientChannel::TryBuildBindingHello(
    void* destination,
    std::size_t destinationBytes,
    std::size_t* bytesWritten) noexcept {
    if (bytesWritten != nullptr) {
        *bytesWritten = 0;
    }
    if ((state_ !=
            SecureUdpClientChannelState::AwaitingChallenge &&
         state_ !=
            SecureUdpClientChannelState::AwaitingConfirmation) ||
        bytesWritten == nullptr ||
        !TryEncodeSecureUdpClientHello(
            connectionId_,
            sizeof(connectionId_),
            clientNonce_,
            sizeof(clientNonce_),
            destination,
            destinationBytes)) {
        return false;
    }
    *bytesWritten = SecureUdpBindingDatagramBytes;
    return true;
}

bool SecureUdpClientChannel::TryHandleBindingChallenge(
    const void* challenge,
    std::size_t challengeBytes,
    void* proofDestination,
    std::size_t proofDestinationBytes,
    std::size_t* proofBytes) noexcept {
    if (proofBytes != nullptr) {
        *proofBytes = 0;
    }
    if ((state_ !=
            SecureUdpClientChannelState::AwaitingChallenge &&
         state_ !=
            SecureUdpClientChannelState::AwaitingConfirmation) ||
        proofBytes == nullptr ||
        !TryEncodeSecureUdpAuthenticatedProof(
            challenge,
            challengeBytes,
            connectionId_,
            sizeof(connectionId_),
            clientNonce_,
            sizeof(clientNonce_),
            proofKey_,
            sizeof(proofKey_),
            proofDestination,
            proofDestinationBytes)) {
        IncrementSaturated(&rejectedPackets_);
        return false;
    }
    state_ = SecureUdpClientChannelState::AwaitingConfirmation;
    *proofBytes = SecureUdpBindingDatagramBytes;
    return true;
}

bool SecureUdpClientChannel::KeepaliveDue(
    std::uint64_t nowMonotonicMilliseconds) const noexcept {
    const auto interval = pingPending_
        ? KeepaliveIntervalMilliseconds * 2
        : KeepaliveIntervalMilliseconds;
    return state_ == SecureUdpClientChannelState::Active &&
        nowMonotonicMilliseconds >= lastSendMilliseconds_ &&
        nowMonotonicMilliseconds - lastSendMilliseconds_ >=
            interval;
}

bool SecureUdpClientChannel::PeerTimedOut(
    std::uint64_t nowMonotonicMilliseconds) const noexcept {
    return state_ == SecureUdpClientChannelState::Active &&
        lastAuthenticatedReceiveMilliseconds_ != 0 &&
        nowMonotonicMilliseconds >=
            lastAuthenticatedReceiveMilliseconds_ &&
        nowMonotonicMilliseconds -
                lastAuthenticatedReceiveMilliseconds_ >=
            PeerTimeoutMilliseconds;
}

bool SecureUdpClientChannel::IsUsable() const noexcept {
    return state_ == SecureUdpClientChannelState::AwaitingChallenge ||
        state_ ==
            SecureUdpClientChannelState::AwaitingConfirmation ||
        state_ == SecureUdpClientChannelState::Active;
}

void SecureUdpClientChannel::Stop() noexcept {
    ClearSecrets();
    if (state_ != SecureUdpClientChannelState::Failed) {
        state_ = SecureUdpClientChannelState::Stopped;
    }
}

SecureUdpClientChannelSnapshot
SecureUdpClientChannel::Snapshot() const noexcept {
    SecureUdpClientChannelSnapshot snapshot{};
    snapshot.state = state_;
    snapshot.failure = failure_;
    snapshot.udpPort = udpPort_;
    snapshot.serverId = serverId_;
    snapshot.bindingRevision = bindingRevision_;
    snapshot.sendEpoch = sendEpoch_;
    snapshot.nextSendSequence = nextSendSequence_;
    snapshot.receiveEpoch = receiveEpoch_;
    snapshot.lastAuthenticatedReceiveMilliseconds =
        lastAuthenticatedReceiveMilliseconds_;
    snapshot.lastSendMilliseconds = lastSendMilliseconds_;
    snapshot.lastRoundTripMilliseconds =
        lastRoundTripMilliseconds_;
    snapshot.jitterMilliseconds = jitterMilliseconds_;
    snapshot.lostPings = lostPings_;
    snapshot.authenticatedPackets = authenticatedPackets_;
    snapshot.rejectedPackets = rejectedPackets_;
    snapshot.replayedPackets = replayedPackets_;
    snapshot.latestPositionSnapshotSequence =
        latestPositionSnapshot_.snapshotSequence;
    return snapshot;
}

void SecureUdpClientChannel::Fail(
    SecureUdpClientChannelFailure failure) noexcept {
    if (failure_ == SecureUdpClientChannelFailure::None) {
        failure_ = failure;
    }
    state_ = SecureUdpClientChannelState::Failed;
    ClearSecrets();
}

void SecureUdpClientChannel::ClearSecrets() noexcept {
    SecureZeroMemory(connectionId_, sizeof(connectionId_));
    SecureZeroMemory(proofKey_, sizeof(proofKey_));
    SecureZeroMemory(clientNonce_, sizeof(clientNonce_));
    SecureZeroMemory(pendingPing_, sizeof(pendingPing_));
    udpPort_ = 0;
    serverId_ = 0;
    expectedBindingRevision_ = 0;
    pingPending_ = false;
    pendingPingSentMilliseconds_ = 0;
    currentReceiveWindow_.Reset();
    previousReceiveWindow_.Reset();
    nextReceiveWindow_.Reset();
    latestPositionSnapshot_ =
        SecureRealtimePositionSnapshot{};
    positionSnapshotPending_ = false;
}

bool SecureUdpClientChannel::IsNonzero(
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

void SecureUdpClientChannel::IncrementSaturated(
    std::uint64_t* value) noexcept {
    if (value != nullptr &&
        *value != (std::numeric_limits<std::uint64_t>::max)()) {
        ++*value;
    }
}

} // namespace godswar::network
