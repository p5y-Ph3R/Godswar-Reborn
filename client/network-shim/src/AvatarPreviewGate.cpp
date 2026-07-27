#include "AvatarPreviewGate.h"

#include <climits>

namespace godswar::network {

AvatarPreviewGate::AvatarPreviewGate() noexcept
    : AvatarPreviewGate(
        IsSupportedOriginAvatarHost(),
        AreOriginAvatarResourcesReady,
        DestroyLegacyMessage,
        RequestOriginAvatarPreload) {
}

AvatarPreviewGate::AvatarPreviewGate(
    bool enabled,
    AvatarReadinessProbe readinessProbe,
    LegacyMessageDisposer messageDisposer,
    AvatarPreloadRequester preloadRequester) noexcept
    : enabled_(
          enabled &&
          readinessProbe != nullptr &&
          messageDisposer != nullptr),
      readinessProbe_(readinessProbe),
      messageDisposer_(messageDisposer),
      preloadRequester_(preloadRequester),
      preloadResult_(AvatarPreloadResult::NotInvoked),
      heldMessage_(nullptr),
      generation_(0),
      resourcesWereReady_(false),
      initializerActive_(false) {
}

AvatarPreviewGate::~AvatarPreviewGate() noexcept {
    Reset();
}

bool AvatarPreviewGate::IsHolding() const noexcept {
    return heldMessage_ != nullptr;
}

bool AvatarPreviewGate::BlocksLegacyPolling() const noexcept {
    return heldMessage_ != nullptr ||
        initializerActive_;
}

void* AvatarPreviewGate::Filter(void* message) noexcept {
    if (!enabled_) {
        return message;
    }

    if (IsAfterLoginBootstrapMessage(message)) {
        if (resourcesWereReady_ && !readinessProbe_()) {
            // The stock client unloads selection resources after world entry.
            // A later AfterLogin bootstrap on the same transport begins a new
            // selection-resource lifecycle and may use one fresh initializer.
            resourcesWereReady_ = false;
            preloadResult_ = AvatarPreloadResult::NotInvoked;
        }
        // Origin must consume every ordered bootstrap record before native
        // initialization. The following preview is the invocation barrier.
        return message;
    }

    if (heldMessage_ != nullptr ||
        !IsCharacterPreviewMessage(message)) {
        return message;
    }

    if (resourcesWereReady_ && !readinessProbe_()) {
        resourcesWereReady_ = false;
        preloadResult_ = AvatarPreloadResult::NotInvoked;
    }
    if (ObserveResourcesReady()) {
        return message;
    }

    const auto generation = generation_;
    TryRequestPreload();
    if (generation_ != generation) {
        messageDisposer_(message);
        return nullptr;
    }
    if (ObserveResourcesReady()) {
        return message;
    }

    heldMessage_ = message;
    return nullptr;
}

void* AvatarPreviewGate::TryRelease() noexcept {
    if (heldMessage_ == nullptr) {
        return nullptr;
    }
    if (initializerActive_) {
        return nullptr;
    }

    if (!ObserveResourcesReady()) {
        TryRequestPreload();
        if (!ObserveResourcesReady()) {
            return nullptr;
        }
    }

    auto* message = heldMessage_;
    heldMessage_ = nullptr;
    return message;
}

long AvatarPreviewGate::AdjustMessageCount(long legacyCount) const noexcept {
    if (heldMessage_ == nullptr ||
        legacyCount < 0 ||
        legacyCount == LONG_MAX) {
        return legacyCount;
    }

    return legacyCount + 1;
}

void AvatarPreviewGate::Reset() noexcept {
    auto* message = heldMessage_;
    heldMessage_ = nullptr;
    preloadResult_ = AvatarPreloadResult::NotInvoked;
    ++generation_;
    resourcesWereReady_ = false;
    if (message != nullptr && messageDisposer_ != nullptr) {
        messageDisposer_(message);
    }
}

bool AvatarPreviewGate::ObserveResourcesReady() noexcept {
    const auto ready = readinessProbe_();
    if (ready) {
        resourcesWereReady_ = true;
    }
    return ready;
}

void AvatarPreviewGate::TryRequestPreload() noexcept {
    if (initializerActive_ ||
        preloadRequester_ == nullptr ||
        preloadResult_ != AvatarPreloadResult::NotInvoked ||
        ObserveResourcesReady()) {
        return;
    }

    // Close the re-entrant PickMsg window before crossing into the native
    // initializer. The callback may pump messages while it allocates.
    initializerActive_ = true;
    preloadResult_ = AvatarPreloadResult::Invoking;
    const auto result = preloadRequester_();
    initializerActive_ = false;
    if (preloadResult_ != AvatarPreloadResult::Invoking) {
        // Reset started a new lifecycle while the native initializer pumped.
        // Never commit an old lifecycle's result into the new one.
        return;
    }
    if (result == AvatarPreloadResult::NotInvoked) {
        preloadResult_ = AvatarPreloadResult::NotInvoked;
    } else {
        // The audited initializer is not proven idempotent after a partial
        // allocation. One actual invocation is therefore the lifecycle bound;
        // the six native resource slots remain the sole release authority.
        preloadResult_ = result;
    }
    static_cast<void>(ObserveResourcesReady());
}

} // namespace godswar::network
