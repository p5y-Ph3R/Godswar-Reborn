#pragma once

#include <cstdint>

namespace godswar::network {

using AvatarReadinessProbe = bool (*)() noexcept;
enum class AvatarPreloadResult : std::uint8_t {
    NotInvoked = 0,
    Invoking = 1,
    InvokedNotReady = 2,
    Ready = 3,
};
using AvatarPreloadRequester = AvatarPreloadResult (*)() noexcept;
using LegacyMessageDisposer = void (*)(void*) noexcept;

class AvatarPreviewGate final {
public:
    AvatarPreviewGate() noexcept;
    AvatarPreviewGate(
        bool enabled,
        AvatarReadinessProbe readinessProbe,
        LegacyMessageDisposer messageDisposer,
        AvatarPreloadRequester preloadRequester = nullptr) noexcept;
    ~AvatarPreviewGate() noexcept;

    AvatarPreviewGate(const AvatarPreviewGate&) = delete;
    AvatarPreviewGate& operator=(const AvatarPreviewGate&) = delete;

    bool IsHolding() const noexcept;
    bool BlocksLegacyPolling() const noexcept;
    void* Filter(void* message) noexcept;
    void* TryRelease() noexcept;
    long AdjustMessageCount(long legacyCount) const noexcept;
    void Reset() noexcept;

private:
    bool ObserveResourcesReady() noexcept;
    void TryRequestPreload() noexcept;

    bool enabled_;
    AvatarReadinessProbe readinessProbe_;
    LegacyMessageDisposer messageDisposer_;
    AvatarPreloadRequester preloadRequester_;
    AvatarPreloadResult preloadResult_;
    void* heldMessage_;
    std::uint64_t generation_;
    bool resourcesWereReady_;
    bool initializerActive_;
};

bool IsSupportedOriginAvatarHost() noexcept;
bool AreOriginAvatarResourcesReady() noexcept;
AvatarPreloadResult RequestOriginAvatarPreload() noexcept;
bool IsAfterLoginBootstrapMessage(const void* message) noexcept;
bool IsCharacterPreviewMessage(const void* message) noexcept;
void DestroyLegacyMessage(void* message) noexcept;

} // namespace godswar::network
