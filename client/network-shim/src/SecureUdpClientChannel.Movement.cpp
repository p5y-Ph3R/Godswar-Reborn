#include "SecureUdpClientChannel.h"

#include <Windows.h>

namespace godswar::network {

bool SecureUdpClientChannel::TryBuildMovementInput(
    const SecureRealtimeMovementInput& movement,
    std::uint64_t nowMonotonicMilliseconds,
    void* destination,
    std::size_t destinationBytes,
    std::size_t* bytesWritten) noexcept {
    if (bytesWritten != nullptr) {
        *bytesWritten = 0;
    }
    if (state_ != SecureUdpClientChannelState::Active ||
        destination == nullptr ||
        bytesWritten == nullptr ||
        nowMonotonicMilliseconds < lastSendMilliseconds_ ||
        nowMonotonicMilliseconds - lastSendMilliseconds_ <
            MinimumSendIntervalMilliseconds ||
        !RotateSendEpochIfNeeded(
            nowMonotonicMilliseconds)) {
        return false;
    }

    std::uint8_t payload[SecureRealtimeMovementInputBytes]{};
    if (!TryEncodeSecureRealtimeMovementInput(
            movement,
            SecureRealtimeMovementSource::Udp,
            payload,
            sizeof(payload))) {
        SecureZeroMemory(payload, sizeof(payload));
        return false;
    }
    const auto header = BuildOutgoingHeader(
        SecureUdpProtectedMessageType::MovementInput,
        static_cast<std::uint16_t>(sizeof(payload)));
    const bool sealed = TrySealSecureUdpProtectedDatagram(
        proofKey_,
        sizeof(proofKey_),
        connectionId_,
        sizeof(connectionId_),
        serverId_,
        SecureUdpDirection::ClientToServer,
        header,
        payload,
        sizeof(payload),
        destination,
        destinationBytes,
        bytesWritten);
    SecureZeroMemory(payload, sizeof(payload));
    if (!sealed) {
        return false;
    }
    CompleteProtectedSend(nowMonotonicMilliseconds);
    return true;
}

bool SecureUdpClientChannel::TryTakePositionSnapshot(
    SecureRealtimePositionSnapshot* snapshot) noexcept {
    if (snapshot == nullptr) {
        return false;
    }
    *snapshot = SecureRealtimePositionSnapshot{};
    if (state_ != SecureUdpClientChannelState::Active ||
        !positionSnapshotPending_) {
        return false;
    }
    *snapshot = latestPositionSnapshot_;
    positionSnapshotPending_ = false;
    return true;
}

bool SecureUdpClientChannel::CanAcceptPositionSnapshot(
    const std::uint8_t* plaintext,
    std::size_t plaintextBytes,
    SecureRealtimePositionSnapshot* snapshot) const noexcept {
    if ((state_ != SecureUdpClientChannelState::Active &&
            state_ !=
                SecureUdpClientChannelState::
                    AwaitingConfirmation) ||
        snapshot == nullptr ||
        !TryDecodeSecureRealtimePositionSnapshot(
            plaintext,
            plaintextBytes,
            snapshot)) {
        return false;
    }
    return snapshot->snapshotSequence >
        latestPositionSnapshot_.snapshotSequence;
}

bool SecureUdpClientChannel::AcceptPositionSnapshot(
    const SecureRealtimePositionSnapshot& snapshot) noexcept {
    if ((state_ != SecureUdpClientChannelState::Active &&
            state_ !=
                SecureUdpClientChannelState::
                    AwaitingConfirmation) ||
        snapshot.snapshotSequence <=
            latestPositionSnapshot_.snapshotSequence) {
        return false;
    }
    latestPositionSnapshot_ = snapshot;
    positionSnapshotPending_ = true;
    return true;
}

} // namespace godswar::network
