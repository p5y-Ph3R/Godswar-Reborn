#include "SecureUdpReplayWindow.h"

namespace godswar::network {

bool SecureUdpReplayWindow::CouldAccept(
    std::uint64_t sequence) const noexcept {
    if (!initialized_ || sequence > highest_) {
        return true;
    }

    const auto distance = highest_ - sequence;
    if (distance >= 128) {
        return false;
    }
    const auto bit = static_cast<unsigned>(
        distance < 64 ? distance : distance - 64);
    const auto mask = std::uint64_t{1} << bit;
    return distance < 64
        ? (recentLow_ & mask) == 0
        : (recentHigh_ & mask) == 0;
}

bool SecureUdpReplayWindow::CommitAuthenticated(
    std::uint64_t sequence) noexcept {
    if (!CouldAccept(sequence)) {
        return false;
    }
    if (!initialized_) {
        initialized_ = true;
        highest_ = sequence;
        recentLow_ = 1;
        recentHigh_ = 0;
        return true;
    }
    if (sequence > highest_) {
        const auto distance = sequence - highest_;
        if (distance >= 128) {
            recentLow_ = 1;
            recentHigh_ = 0;
        } else if (distance >= 64) {
            recentHigh_ =
                recentLow_ << static_cast<unsigned>(
                    distance - 64);
            recentLow_ = 1;
        } else {
            const auto shift = static_cast<unsigned>(distance);
            recentHigh_ =
                (recentHigh_ << shift) |
                (recentLow_ >> (64 - shift));
            recentLow_ = (recentLow_ << shift) | 1;
        }
        highest_ = sequence;
        return true;
    }

    const auto distance = highest_ - sequence;
    if (distance < 64) {
        recentLow_ |=
            std::uint64_t{1} <<
            static_cast<unsigned>(distance);
    } else {
        recentHigh_ |=
            std::uint64_t{1} <<
            static_cast<unsigned>(distance - 64);
    }
    return true;
}

bool SecureUdpReplayWindow::HasPackets() const noexcept {
    return initialized_;
}

std::uint64_t
SecureUdpReplayWindow::HighestSequence() const noexcept {
    return initialized_ ? highest_ : 0;
}

std::uint64_t
SecureUdpReplayWindow::AcknowledgmentMask() const noexcept {
    return initialized_
        ? (recentLow_ >> 1U) | (recentHigh_ << 63U)
        : 0;
}

void SecureUdpReplayWindow::Reset() noexcept {
    initialized_ = false;
    highest_ = 0;
    recentLow_ = 0;
    recentHigh_ = 0;
}

} // namespace godswar::network
