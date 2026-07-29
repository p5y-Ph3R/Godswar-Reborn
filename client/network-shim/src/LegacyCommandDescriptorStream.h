#pragma once

#include "OpaqueDuplexPump.h"
#include "SecureLegacyCommandIdentity.h"
#include "SecureLegacyFrameStream.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t LegacyDescriptorQueueCapacity = 64;

enum class LegacyDescriptorFailure : std::uint8_t {
    None = 0,
    InvalidArgument,
    QueueFull,
    DescriptorMissing,
    PacketWrite,
    PartialPacket,
    Stopped,
};

struct LegacyDescriptorStreamSnapshot final {
    LegacyDescriptorFailure failure =
        LegacyDescriptorFailure::None;
    std::size_t queuedDescriptors = 0;
    std::size_t currentPacketRemaining = 0;
    bool stopped = false;
};

class LegacyCommandDescriptorStream final : public IByteStream {
public:
    explicit LegacyCommandDescriptorStream(
        ISecureLegacyFrameStream* outerStream) noexcept;
    ~LegacyCommandDescriptorStream() noexcept;

    LegacyCommandDescriptorStream(
        const LegacyCommandDescriptorStream&) = delete;
    LegacyCommandDescriptorStream& operator=(
        const LegacyCommandDescriptorStream&) = delete;

    bool Enqueue(
        const LegacyPacketDescriptor& descriptor,
        std::uint64_t* token) noexcept;
    // Cancellation succeeds only while this exact descriptor is the
    // unstarted queue tail.
    bool CancelUnstarted(std::uint64_t token) noexcept;
    // A descriptor that already owns ciphertext cannot be removed safely.
    // Stop the stream so later packet bytes cannot complete the wrong packet.
    bool CancelUnstartedOrStop(std::uint64_t token) noexcept;

    ByteStreamIoResult Read(
        void* destination,
        std::size_t destinationCapacity) noexcept override;
    ByteStreamIoResult Write(
        const void* source,
        std::size_t sourceBytes) noexcept override;
    void Stop() noexcept override;

    LegacyDescriptorStreamSnapshot Snapshot() const noexcept;

private:
    struct QueuedDescriptor final {
        LegacyPacketDescriptor descriptor{};
        std::uint64_t token = 0;
    };

    bool BeginNextDescriptor() noexcept;
    void Fail(LegacyDescriptorFailure failure) noexcept;
    bool IsStopped() const noexcept;

    ISecureLegacyFrameStream* outerStream_ = nullptr;
    mutable SRWLOCK lock_{};
    QueuedDescriptor queue_[LegacyDescriptorQueueCapacity]{};
    std::size_t head_ = 0;
    std::size_t count_ = 0;
    std::uint64_t nextToken_ = 1;
    LegacyPacketDescriptor current_{};
    std::size_t currentRemaining_ = 0;
    bool currentStarted_ = false;
    volatile LONG stopped_ = 0;
    volatile LONG failure_ = 0;
};

} // namespace godswar::network
