#pragma once

#include "SecureUdpBindingGrant.h"
#include "SecureUdpBindingProtocol.h"
#include "SecureUdpProtectedProtocol.h"
#include "SecureRealtimeMovementProtocol.h"
#include "SecureUdpReplayWindow.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

enum class SecureUdpClientChannelState : std::uint8_t {
    Idle = 0,
    AwaitingChallenge,
    AwaitingConfirmation,
    Active,
    Stopped,
    Failed,
};

enum class SecureUdpClientChannelFailure : std::uint8_t {
    None = 0,
    InvalidArgument,
    ExpiredGrant,
    GrantSecret,
    BindingEncoding,
    SequenceExhausted,
};

struct SecureUdpClientChannelSnapshot final {
    SecureUdpClientChannelState state =
        SecureUdpClientChannelState::Idle;
    SecureUdpClientChannelFailure failure =
        SecureUdpClientChannelFailure::None;
    std::uint16_t udpPort = 0;
    std::uint32_t serverId = 0;
    std::uint64_t bindingRevision = 0;
    std::uint32_t sendEpoch = 0;
    std::uint64_t nextSendSequence = 0;
    std::uint32_t receiveEpoch = 0;
    std::uint64_t lastAuthenticatedReceiveMilliseconds = 0;
    std::uint64_t lastSendMilliseconds = 0;
    std::uint64_t lastRoundTripMilliseconds = 0;
    std::uint64_t jitterMilliseconds = 0;
    std::uint64_t lostPings = 0;
    std::uint64_t authenticatedPackets = 0;
    std::uint64_t rejectedPackets = 0;
    std::uint64_t replayedPackets = 0;
    std::uint64_t latestPositionSnapshotSequence = 0;
};

// Pure, single-owner binding/protected-channel state machine. Socket lifecycle
// is intentionally separate so all protocol and timing behavior is testable
// without contacting any live endpoint.
class SecureUdpClientChannel final {
public:
    static constexpr std::uint64_t MinimumSendIntervalMilliseconds = 20;
    static constexpr std::uint64_t KeepaliveIntervalMilliseconds = 5'000;
    static constexpr std::uint64_t PeerTimeoutMilliseconds = 15'000;
    static constexpr std::uint64_t EpochLifetimeMilliseconds = 300'000;
    static constexpr std::uint64_t PreviousEpochOverlapMilliseconds = 10'000;
    static constexpr std::uint64_t MaximumPacketsPerEpoch = 1'000'000;

    SecureUdpClientChannel() noexcept = default;
    ~SecureUdpClientChannel() noexcept;

    SecureUdpClientChannel(const SecureUdpClientChannel&) = delete;
    SecureUdpClientChannel& operator=(
        const SecureUdpClientChannel&) = delete;

    bool Initialize(
        SecureUdpBindingGrant* grant,
        const std::uint8_t* clientNonce,
        std::size_t clientNonceBytes,
        std::uint64_t nowUnixMilliseconds,
        std::uint64_t nowMonotonicMilliseconds) noexcept;
    bool BeginRebind(
        const std::uint8_t* clientNonce,
        std::size_t clientNonceBytes) noexcept;

    bool TryBuildBindingHello(
        void* destination,
        std::size_t destinationBytes,
        std::size_t* bytesWritten) noexcept;
    bool TryHandleBindingChallenge(
        const void* challenge,
        std::size_t challengeBytes,
        void* proofDestination,
        std::size_t proofDestinationBytes,
        std::size_t* proofBytes) noexcept;

    bool TryHandleProtectedDatagram(
        const void* datagram,
        std::size_t datagramBytes,
        std::uint64_t nowMonotonicMilliseconds) noexcept;
    bool TryBuildPing(
        std::uint64_t pingId,
        std::uint64_t nowMonotonicMilliseconds,
        void* destination,
        std::size_t destinationBytes,
        std::size_t* bytesWritten) noexcept;
    bool TryBuildMovementInput(
        const SecureRealtimeMovementInput& movement,
        std::uint64_t nowMonotonicMilliseconds,
        void* destination,
        std::size_t destinationBytes,
        std::size_t* bytesWritten) noexcept;
    bool TryTakePositionSnapshot(
        SecureRealtimePositionSnapshot* snapshot) noexcept;

    bool KeepaliveDue(
        std::uint64_t nowMonotonicMilliseconds) const noexcept;
    bool PeerTimedOut(
        std::uint64_t nowMonotonicMilliseconds) const noexcept;
    bool IsUsable() const noexcept;
    void Stop() noexcept;

    SecureUdpClientChannelSnapshot Snapshot() const noexcept;

private:
    enum class ReceiveEpochSelection : std::uint8_t {
        None = 0,
        Previous,
        Current,
        Next,
    };

    void Fail(SecureUdpClientChannelFailure failure) noexcept;
    void ClearSecrets() noexcept;
    bool RotateSendEpochIfNeeded(
        std::uint64_t nowMonotonicMilliseconds) noexcept;
    SecureUdpProtectedHeader BuildOutgoingHeader(
        SecureUdpProtectedMessageType messageType,
        std::uint16_t payloadBytes) const noexcept;
    ReceiveEpochSelection SelectReceiveEpoch(
        const SecureUdpProtectedHeader& header,
        std::uint64_t nowMonotonicMilliseconds,
        SecureUdpReplayWindow** window) noexcept;
    void PromoteReceiveEpoch(
        std::uint64_t nowMonotonicMilliseconds) noexcept;
    bool AcceptBindingConfirmation(
        const std::uint8_t* plaintext,
        const SecureUdpProtectedHeader& header) noexcept;
    bool AcceptPong(
        const std::uint8_t* plaintext,
        std::uint64_t nowMonotonicMilliseconds) noexcept;
    bool CanAcceptPositionSnapshot(
        const std::uint8_t* plaintext,
        std::size_t plaintextBytes,
        SecureRealtimePositionSnapshot* snapshot) const noexcept;
    bool AcceptPositionSnapshot(
        const SecureRealtimePositionSnapshot& snapshot) noexcept;
    void CompleteProtectedSend(
        std::uint64_t nowMonotonicMilliseconds) noexcept;
    static bool IsNonzero(
        const std::uint8_t* bytes,
        std::size_t byteCount) noexcept;
    static void IncrementSaturated(std::uint64_t* value) noexcept;

    SecureUdpClientChannelState state_ =
        SecureUdpClientChannelState::Idle;
    SecureUdpClientChannelFailure failure_ =
        SecureUdpClientChannelFailure::None;
    std::uint16_t udpPort_ = 0;
    std::uint32_t serverId_ = 0;
    std::uint64_t bindingRevision_ = 0;
    std::uint64_t expectedBindingRevision_ = 0;
    std::uint32_t sendEpoch_ = 0;
    std::uint64_t nextSendSequence_ = 0;
    std::uint64_t packetsInSendEpoch_ = 0;
    std::uint64_t sendEpochStartedMilliseconds_ = 0;
    std::uint32_t receiveEpoch_ = 0;
    std::uint32_t previousReceiveEpoch_ = 0;
    std::uint64_t previousReceiveExpiryMilliseconds_ = 0;
    SecureUdpReplayWindow currentReceiveWindow_{};
    SecureUdpReplayWindow previousReceiveWindow_{};
    SecureUdpReplayWindow nextReceiveWindow_{};
    std::uint8_t connectionId_[SecureUdpConnectionIdBytes]{};
    std::uint8_t proofKey_[SecureUdpProofKeyBytes]{};
    std::uint8_t clientNonce_[SecureUdpClientNonceBytes]{};
    std::uint8_t pendingPing_[16]{};
    bool pingPending_ = false;
    std::uint64_t pendingPingSentMilliseconds_ = 0;
    std::uint64_t lastAuthenticatedReceiveMilliseconds_ = 0;
    std::uint64_t lastSendMilliseconds_ = 0;
    std::uint64_t lastRoundTripMilliseconds_ = 0;
    std::uint64_t jitterMilliseconds_ = 0;
    std::uint64_t lostPings_ = 0;
    std::uint64_t authenticatedPackets_ = 0;
    std::uint64_t rejectedPackets_ = 0;
    std::uint64_t replayedPackets_ = 0;
    SecureRealtimePositionSnapshot latestPositionSnapshot_{};
    bool positionSnapshotPending_ = false;
};

} // namespace godswar::network
