#pragma once

#include <cstdint>

namespace godswar::network {

// Single-owner 128-packet replay window. The caller may preflight cheaply, but
// must call CommitAuthenticated only after successful AEAD verification.
class SecureUdpReplayWindow final {
public:
    bool CouldAccept(std::uint64_t sequence) const noexcept;
    bool CommitAuthenticated(std::uint64_t sequence) noexcept;

    bool HasPackets() const noexcept;
    std::uint64_t HighestSequence() const noexcept;
    std::uint64_t AcknowledgmentMask() const noexcept;
    void Reset() noexcept;

private:
    bool initialized_ = false;
    std::uint64_t highest_ = 0;
    std::uint64_t recentLow_ = 0;
    std::uint64_t recentHigh_ = 0;
};

} // namespace godswar::network
