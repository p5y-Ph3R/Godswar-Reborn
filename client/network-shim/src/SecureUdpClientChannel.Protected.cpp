#include "SecureUdpClientChannel.h"

#include <Windows.h>

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

void WriteUInt64(
    std::uint8_t* destination,
    std::uint64_t value) noexcept {
    for (std::size_t index = 0; index < 8; ++index) {
        destination[7 - index] =
            static_cast<std::uint8_t>(value);
        value >>= 8U;
    }
}

bool Exact(
    const std::uint8_t* left,
    const std::uint8_t* right,
    std::size_t bytes) noexcept {
    if (left == nullptr || right == nullptr || bytes == 0) {
        return false;
    }
    std::uint8_t difference = 0;
    for (std::size_t index = 0; index < bytes; ++index) {
        difference |= static_cast<std::uint8_t>(
            left[index] ^ right[index]);
    }
    return difference == 0;
}

std::uint64_t ReadUInt64(const std::uint8_t* source) noexcept {
    std::uint64_t value = 0;
    for (std::size_t index = 0; index < 8; ++index) {
        value = (value << 8U) | source[index];
    }
    return value;
}

} // namespace

bool SecureUdpClientChannel::TryHandleProtectedDatagram(
    const void* datagram,
    std::size_t datagramBytes,
    std::uint64_t nowMonotonicMilliseconds) noexcept {
    if ((state_ !=
            SecureUdpClientChannelState::AwaitingConfirmation &&
         state_ != SecureUdpClientChannelState::Active) ||
        datagram == nullptr) {
        return false;
    }

    SecureUdpProtectedHeader inspected{};
    if (!TryInspectSecureUdpProtectedDatagram(
            connectionId_,
            sizeof(connectionId_),
            datagram,
            datagramBytes,
            &inspected)) {
        IncrementSaturated(&rejectedPackets_);
        return false;
    }

    SecureUdpReplayWindow* window = nullptr;
    const auto selected = SelectReceiveEpoch(
        inspected,
        nowMonotonicMilliseconds,
        &window);
    if (selected == ReceiveEpochSelection::None ||
        window == nullptr) {
        IncrementSaturated(&rejectedPackets_);
        return false;
    }
    if (!window->CouldAccept(inspected.sequence)) {
        IncrementSaturated(&replayedPackets_);
        return false;
    }

    std::uint8_t plaintext[
        SecureUdpProtectedMaximumPayloadBytes]{};
    SecureUdpProtectedHeader opened{};
    std::size_t plaintextBytes = 0;
    const bool authenticated =
        TryOpenSecureUdpProtectedDatagram(
            proofKey_,
            sizeof(proofKey_),
            connectionId_,
            sizeof(connectionId_),
            serverId_,
            SecureUdpDirection::ServerToClient,
            datagram,
            datagramBytes,
            &opened,
            plaintext,
            sizeof(plaintext),
            &plaintextBytes);
    if (!authenticated) {
        SecureZeroMemory(plaintext, sizeof(plaintext));
        IncrementSaturated(&rejectedPackets_);
        return false;
    }

    bool semanticallyValid = false;
    SecureRealtimePositionSnapshot positionSnapshot{};
    if (opened.messageType ==
            SecureUdpProtectedMessageType::BindingConfirm &&
        plaintextBytes == 32) {
        const auto revision = ReadUInt64(plaintext + 16);
        const bool revisionAccepted =
            bindingRevision_ == 0
                ? revision == 1
                : revision == bindingRevision_ ||
                    revision == expectedBindingRevision_;
        semanticallyValid =
            state_ ==
                SecureUdpClientChannelState::
                    AwaitingConfirmation &&
            Exact(
                plaintext,
                clientNonce_,
                sizeof(clientNonce_)) &&
            revisionAccepted &&
            revision != 0 &&
            ReadUInt64(plaintext + 24) != 0 &&
            (bindingRevision_ != 0 ||
                (opened.acknowledgmentEpoch == 0 &&
                    opened.acknowledgmentSequence == 0 &&
                    opened.acknowledgmentMask == 0));
    } else if (
        opened.messageType ==
            SecureUdpProtectedMessageType::Pong &&
        plaintextBytes == 32) {
        semanticallyValid =
            state_ == SecureUdpClientChannelState::Active &&
            pingPending_ &&
            Exact(
                plaintext,
                pendingPing_,
                sizeof(pendingPing_)) &&
            nowMonotonicMilliseconds >=
                pendingPingSentMilliseconds_;
    } else if (
        opened.messageType ==
            SecureUdpProtectedMessageType::PositionSnapshot &&
        plaintextBytes ==
            SecureRealtimePositionSnapshotBytes) {
        semanticallyValid = CanAcceptPositionSnapshot(
            plaintext,
            plaintextBytes,
            &positionSnapshot);
    }
    if (!semanticallyValid) {
        SecureZeroMemory(plaintext, sizeof(plaintext));
        IncrementSaturated(&rejectedPackets_);
        return false;
    }

    if (selected == ReceiveEpochSelection::Next) {
        PromoteReceiveEpoch(nowMonotonicMilliseconds);
        window = &currentReceiveWindow_;
    }
    if (!window->CommitAuthenticated(opened.sequence)) {
        SecureZeroMemory(plaintext, sizeof(plaintext));
        IncrementSaturated(&replayedPackets_);
        return false;
    }

    IncrementSaturated(&authenticatedPackets_);
    lastAuthenticatedReceiveMilliseconds_ =
        nowMonotonicMilliseconds;
    bool accepted = true;
    if (opened.messageType ==
            SecureUdpProtectedMessageType::BindingConfirm &&
        plaintextBytes == 32) {
        accepted = AcceptBindingConfirmation(plaintext, opened);
    } else if (
        opened.messageType ==
            SecureUdpProtectedMessageType::Pong &&
        plaintextBytes == 32) {
        accepted = AcceptPong(
            plaintext,
            nowMonotonicMilliseconds);
    } else if (
        opened.messageType ==
            SecureUdpProtectedMessageType::PositionSnapshot &&
        plaintextBytes ==
            SecureRealtimePositionSnapshotBytes) {
        accepted = AcceptPositionSnapshot(positionSnapshot);
    }
    SecureZeroMemory(plaintext, sizeof(plaintext));
    if (!accepted) {
        IncrementSaturated(&rejectedPackets_);
    }
    return accepted;
}

bool SecureUdpClientChannel::TryBuildPing(
    std::uint64_t pingId,
    std::uint64_t nowMonotonicMilliseconds,
    void* destination,
    std::size_t destinationBytes,
    std::size_t* bytesWritten) noexcept {
    if (bytesWritten != nullptr) {
        *bytesWritten = 0;
    }
    if (state_ != SecureUdpClientChannelState::Active ||
        pingId == 0 ||
        destination == nullptr ||
        bytesWritten == nullptr ||
        nowMonotonicMilliseconds < lastSendMilliseconds_ ||
        nowMonotonicMilliseconds - lastSendMilliseconds_ <
            MinimumSendIntervalMilliseconds ||
        !RotateSendEpochIfNeeded(
            nowMonotonicMilliseconds)) {
        return false;
    }

    std::uint8_t payload[16]{};
    if (pingPending_) {
        IncrementSaturated(&lostPings_);
    }
    WriteUInt64(payload, pingId);
    WriteUInt64(payload + 8, nowMonotonicMilliseconds);
    const auto header = BuildOutgoingHeader(
        SecureUdpProtectedMessageType::Ping,
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
    if (!sealed) {
        SecureZeroMemory(payload, sizeof(payload));
        return false;
    }

    std::memcpy(pendingPing_, payload, sizeof(pendingPing_));
    SecureZeroMemory(payload, sizeof(payload));
    pingPending_ = true;
    pendingPingSentMilliseconds_ = nowMonotonicMilliseconds;
    CompleteProtectedSend(nowMonotonicMilliseconds);
    return true;
}

void SecureUdpClientChannel::CompleteProtectedSend(
    std::uint64_t nowMonotonicMilliseconds) noexcept {
    lastSendMilliseconds_ = nowMonotonicMilliseconds;
    ++packetsInSendEpoch_;
    if (nextSendSequence_ ==
        (std::numeric_limits<std::uint64_t>::max)()) {
        if (sendEpoch_ ==
            (std::numeric_limits<std::uint32_t>::max)()) {
            Fail(SecureUdpClientChannelFailure::SequenceExhausted);
        } else {
            ++sendEpoch_;
            nextSendSequence_ = 0;
            packetsInSendEpoch_ = 0;
            sendEpochStartedMilliseconds_ =
                nowMonotonicMilliseconds;
        }
    } else {
        ++nextSendSequence_;
    }
}

bool SecureUdpClientChannel::RotateSendEpochIfNeeded(
    std::uint64_t nowMonotonicMilliseconds) noexcept {
    const bool lifetimeReached =
        nowMonotonicMilliseconds >=
            sendEpochStartedMilliseconds_ &&
        nowMonotonicMilliseconds -
                sendEpochStartedMilliseconds_ >=
            EpochLifetimeMilliseconds;
    if (!lifetimeReached &&
        packetsInSendEpoch_ < MaximumPacketsPerEpoch &&
        nextSendSequence_ !=
            (std::numeric_limits<std::uint64_t>::max)()) {
        return true;
    }
    if (sendEpoch_ ==
        (std::numeric_limits<std::uint32_t>::max)()) {
        Fail(SecureUdpClientChannelFailure::SequenceExhausted);
        return false;
    }
    ++sendEpoch_;
    nextSendSequence_ = 0;
    packetsInSendEpoch_ = 0;
    sendEpochStartedMilliseconds_ =
        nowMonotonicMilliseconds;
    return true;
}

SecureUdpProtectedHeader
SecureUdpClientChannel::BuildOutgoingHeader(
    SecureUdpProtectedMessageType messageType,
    std::uint16_t payloadBytes) const noexcept {
    SecureUdpProtectedHeader header{};
    header.keyEpoch = sendEpoch_;
    header.sequence = nextSendSequence_;
    header.messageType = messageType;
    header.payloadBytes = payloadBytes;
    if (currentReceiveWindow_.HasPackets()) {
        header.acknowledgmentEpoch = receiveEpoch_;
        header.acknowledgmentSequence =
            currentReceiveWindow_.HighestSequence();
        header.acknowledgmentMask =
            currentReceiveWindow_.AcknowledgmentMask();
    }
    return header;
}

SecureUdpClientChannel::ReceiveEpochSelection
SecureUdpClientChannel::SelectReceiveEpoch(
    const SecureUdpProtectedHeader& header,
    std::uint64_t nowMonotonicMilliseconds,
    SecureUdpReplayWindow** window) noexcept {
    *window = nullptr;
    if (previousReceiveEpoch_ != 0 &&
        nowMonotonicMilliseconds >=
            previousReceiveExpiryMilliseconds_) {
        previousReceiveEpoch_ = 0;
        previousReceiveExpiryMilliseconds_ = 0;
        previousReceiveWindow_.Reset();
    }
    if (header.keyEpoch == receiveEpoch_) {
        *window = &currentReceiveWindow_;
        return ReceiveEpochSelection::Current;
    }
    if (header.keyEpoch == previousReceiveEpoch_ &&
        previousReceiveEpoch_ != 0) {
        *window = &previousReceiveWindow_;
        return ReceiveEpochSelection::Previous;
    }
    if (receiveEpoch_ !=
            (std::numeric_limits<std::uint32_t>::max)() &&
        header.keyEpoch == receiveEpoch_ + 1) {
        *window = &nextReceiveWindow_;
        return ReceiveEpochSelection::Next;
    }
    return ReceiveEpochSelection::None;
}

void SecureUdpClientChannel::PromoteReceiveEpoch(
    std::uint64_t nowMonotonicMilliseconds) noexcept {
    previousReceiveEpoch_ = receiveEpoch_;
    previousReceiveWindow_ = currentReceiveWindow_;
    previousReceiveExpiryMilliseconds_ =
        nowMonotonicMilliseconds >
            (std::numeric_limits<std::uint64_t>::max)() -
                PreviousEpochOverlapMilliseconds
        ? (std::numeric_limits<std::uint64_t>::max)()
        : nowMonotonicMilliseconds +
            PreviousEpochOverlapMilliseconds;
    ++receiveEpoch_;
    currentReceiveWindow_ = nextReceiveWindow_;
    nextReceiveWindow_.Reset();
}

bool SecureUdpClientChannel::AcceptBindingConfirmation(
    const std::uint8_t* plaintext,
    const SecureUdpProtectedHeader& header) noexcept {
    if (state_ !=
            SecureUdpClientChannelState::AwaitingConfirmation ||
        plaintext == nullptr ||
        !Exact(
            plaintext,
            clientNonce_,
            sizeof(clientNonce_)) ||
        (bindingRevision_ == 0
            ? ReadUInt64(plaintext + 16) != 1
            : ReadUInt64(plaintext + 16) !=
                    bindingRevision_ &&
                ReadUInt64(plaintext + 16) !=
                    expectedBindingRevision_) ||
        ReadUInt64(plaintext + 24) == 0 ||
        (bindingRevision_ == 0 &&
            (header.acknowledgmentEpoch != 0 ||
                header.acknowledgmentSequence != 0 ||
                header.acknowledgmentMask != 0))) {
        return false;
    }

    bindingRevision_ = ReadUInt64(plaintext + 16);
    state_ = SecureUdpClientChannelState::Active;
    return true;
}

bool SecureUdpClientChannel::AcceptPong(
    const std::uint8_t* plaintext,
    std::uint64_t nowMonotonicMilliseconds) noexcept {
    if (state_ != SecureUdpClientChannelState::Active ||
        plaintext == nullptr ||
        !pingPending_ ||
        !Exact(plaintext, pendingPing_, sizeof(pendingPing_)) ||
        nowMonotonicMilliseconds <
            pendingPingSentMilliseconds_) {
        return false;
    }

    const auto roundTrip =
        nowMonotonicMilliseconds -
        pendingPingSentMilliseconds_;
    if (lastRoundTripMilliseconds_ != 0) {
        const auto difference =
            roundTrip > lastRoundTripMilliseconds_
            ? roundTrip - lastRoundTripMilliseconds_
            : lastRoundTripMilliseconds_ - roundTrip;
        jitterMilliseconds_ =
            (jitterMilliseconds_ * 3 + difference) / 4;
    }
    lastRoundTripMilliseconds_ = roundTrip;
    pingPending_ = false;
    pendingPingSentMilliseconds_ = 0;
    SecureZeroMemory(pendingPing_, sizeof(pendingPing_));
    return true;
}

} // namespace godswar::network
