#pragma once

namespace godswar::network {

using AvatarReadinessProbe = bool (*)() noexcept;
using LegacyMessageDisposer = void (*)(void*) noexcept;

class AvatarPreviewGate final {
public:
    AvatarPreviewGate() noexcept;
    AvatarPreviewGate(
        bool enabled,
        AvatarReadinessProbe readinessProbe,
        LegacyMessageDisposer messageDisposer) noexcept;
    ~AvatarPreviewGate() noexcept;

    AvatarPreviewGate(const AvatarPreviewGate&) = delete;
    AvatarPreviewGate& operator=(const AvatarPreviewGate&) = delete;

    bool IsHolding() const noexcept;
    void* Filter(void* message) noexcept;
    void* TryRelease() noexcept;
    long AdjustMessageCount(long legacyCount) const noexcept;
    void Reset() noexcept;

private:
    bool enabled_;
    AvatarReadinessProbe readinessProbe_;
    LegacyMessageDisposer messageDisposer_;
    void* heldMessage_;
};

bool IsSupportedOriginAvatarHost() noexcept;
bool AreOriginAvatarResourcesReady() noexcept;
bool IsCharacterPreviewMessage(const void* message) noexcept;
void DestroyLegacyMessage(void* message) noexcept;

} // namespace godswar::network
