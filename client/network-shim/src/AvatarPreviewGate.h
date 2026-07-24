#pragma once

#include <cstdint>

namespace godswar::network {

using AvatarReadinessProbe = bool (*)() noexcept;
using LegacyMessageDisposer = void (*)(void*) noexcept;
using AvatarMonotonicClock = std::uint64_t (*)() noexcept;

inline constexpr std::uint64_t AvatarPreviewWaitTimeoutMilliseconds =
    5'000;

std::uint64_t ReadAvatarMonotonicMilliseconds() noexcept;

class AvatarPreviewGate final {
public:
    AvatarPreviewGate() noexcept;
    AvatarPreviewGate(
        bool enabled,
        AvatarReadinessProbe readinessProbe,
        LegacyMessageDisposer messageDisposer,
        AvatarMonotonicClock monotonicClock =
            ReadAvatarMonotonicMilliseconds,
        std::uint64_t waitTimeoutMilliseconds =
            AvatarPreviewWaitTimeoutMilliseconds) noexcept;
    ~AvatarPreviewGate() noexcept;

    AvatarPreviewGate(const AvatarPreviewGate&) = delete;
    AvatarPreviewGate& operator=(const AvatarPreviewGate&) = delete;

    bool IsHolding() const noexcept;
    bool HasTimedOut() const noexcept;
    void* Filter(void* message) noexcept;
    void* TryRelease() noexcept;
    long AdjustMessageCount(long legacyCount) const noexcept;
    void Reset() noexcept;

private:
    bool enabled_;
    AvatarReadinessProbe readinessProbe_;
    LegacyMessageDisposer messageDisposer_;
    AvatarMonotonicClock monotonicClock_;
    std::uint64_t waitTimeoutMilliseconds_;
    std::uint64_t heldAtMilliseconds_;
    void* heldMessage_;
};

bool IsSupportedOriginAvatarHost() noexcept;
bool AreOriginAvatarResourcesReady() noexcept;
bool IsCharacterPreviewMessage(const void* message) noexcept;
void DestroyLegacyMessage(void* message) noexcept;

} // namespace godswar::network
