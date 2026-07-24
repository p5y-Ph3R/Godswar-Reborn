#include "NativeClientCoordinator.h"

#include <limits>

namespace godswar::network {
namespace {

class ExclusiveLock final {
public:
    explicit ExclusiveLock(SRWLOCK* lock) noexcept : lock_(lock) {
        AcquireSRWLockExclusive(lock_);
    }

    ~ExclusiveLock() noexcept {
        ReleaseSRWLockExclusive(lock_);
    }

    ExclusiveLock(const ExclusiveLock&) = delete;
    ExclusiveLock& operator=(const ExclusiveLock&) = delete;

private:
    SRWLOCK* lock_;
};

class SharedLock final {
public:
    explicit SharedLock(SRWLOCK* lock) noexcept : lock_(lock) {
        AcquireSRWLockShared(lock_);
    }

    ~SharedLock() noexcept {
        ReleaseSRWLockShared(lock_);
    }

    SharedLock(const SharedLock&) = delete;
    SharedLock& operator=(const SharedLock&) = delete;

private:
    SRWLOCK* lock_;
};

} // namespace

NativeClientCoordinator::NativeClientCoordinator() noexcept
    : NativeClientCoordinator(DisabledClientRoutePolicy()) {
}

NativeClientCoordinator::NativeClientCoordinator(
    ClientRoutePolicy policy) noexcept
    : policy_(
          policy.classify == nullptr
              ? DisabledClientRoutePolicy()
              : policy) {
    InitializeSRWLock(&lock_);
}

NativeCoordinatorResult NativeClientCoordinator::Register(
    NativeProxyId* proxyId) noexcept {
    if (proxyId == nullptr) {
        return NativeCoordinatorResult::InvalidArgument;
    }

    *proxyId = 0;
    ExclusiveLock guard(&lock_);

    Entry* available = nullptr;
    for (auto& entry : entries_) {
        if (!entry.occupied) {
            available = &entry;
            break;
        }
    }

    if (available == nullptr) {
        return NativeCoordinatorResult::CapacityReached;
    }

    const auto id = NextProxyId();
    if (id == 0) {
        return NativeCoordinatorResult::IdExhausted;
    }

    available->occupied = true;
    available->proxyId = id;
    available->generation = 1;
    available->state = NativeClientState::Registered;
    available->decision = ClientRouteDecision::Reject;
    available->route = ClientRoute{};
    *proxyId = id;
    return NativeCoordinatorResult::Success;
}

NativeCoordinatorResult NativeClientCoordinator::SetHost(
    NativeProxyId proxyId,
    const char* host,
    std::uint16_t port) noexcept {
    ClientRoute route{};
    if (proxyId == 0 || !TryCopyClientRoute(host, port, &route)) {
        return NativeCoordinatorResult::InvalidArgument;
    }

    std::uint64_t operationGeneration = 0;
    {
        ExclusiveLock guard(&lock_);
        auto* entry = FindEntry(proxyId);
        if (entry == nullptr) {
            return NativeCoordinatorResult::NotRegistered;
        }

        if (entry->state == NativeClientState::Connecting ||
            entry->state == NativeClientState::Connected ||
            entry->state == NativeClientState::ClassifyingRoute) {
            return NativeCoordinatorResult::InvalidState;
        }

        ++entry->generation;
        if (entry->generation == 0) {
            ++entry->generation;
        }
        operationGeneration = entry->generation;
        entry->state = NativeClientState::ClassifyingRoute;
        entry->decision = ClientRouteDecision::Reject;
        entry->route = route;
    }

    auto decision = policy_.classify(
        policy_.context,
        proxyId,
        route);
    if (!IsKnownClientRouteDecision(decision)) {
        decision = ClientRouteDecision::Reject;
    }

    {
        ExclusiveLock guard(&lock_);
        auto* entry = FindEntry(proxyId);
        if (entry == nullptr ||
            entry->generation != operationGeneration ||
            entry->state != NativeClientState::ClassifyingRoute) {
            return NativeCoordinatorResult::OperationSuperseded;
        }

        entry->decision = decision;
        entry->state = NativeClientState::HostReady;
    }

    return NativeCoordinatorResult::Success;
}

NativeCoordinatorResult NativeClientCoordinator::BeginConnect(
    NativeProxyId proxyId,
    ClientBridgePlan* plan) noexcept {
    if (proxyId == 0 || plan == nullptr) {
        return NativeCoordinatorResult::InvalidArgument;
    }

    *plan = ClientBridgePlan{};
    ExclusiveLock guard(&lock_);
    auto* entry = FindEntry(proxyId);
    if (entry == nullptr) {
        return NativeCoordinatorResult::NotRegistered;
    }

    if (entry->state != NativeClientState::HostReady) {
        return NativeCoordinatorResult::InvalidState;
    }

    plan->proxyId = proxyId;
    plan->generation = entry->generation;
    plan->decision = entry->decision;
    plan->role = RoleForDecision(entry->decision);
    plan->logicalRoute = entry->route;

    if (entry->decision == ClientRouteDecision::Reject) {
        return NativeCoordinatorResult::RouteRejected;
    }

    entry->state = NativeClientState::Connecting;
    return NativeCoordinatorResult::Success;
}

NativeCoordinatorResult NativeClientCoordinator::MarkConnected(
    const ClientBridgePlan& plan) noexcept {
    if (plan.proxyId == 0 || plan.generation == 0) {
        return NativeCoordinatorResult::InvalidArgument;
    }

    ExclusiveLock guard(&lock_);
    auto* entry = FindEntry(plan.proxyId);
    if (entry == nullptr) {
        return NativeCoordinatorResult::NotRegistered;
    }

    if (entry->generation != plan.generation) {
        return NativeCoordinatorResult::OperationSuperseded;
    }

    if (entry->decision != plan.decision ||
        RoleForDecision(entry->decision) != plan.role ||
        !ClientRoutesEqual(entry->route, plan.logicalRoute)) {
        return NativeCoordinatorResult::OperationSuperseded;
    }

    if (entry->state != NativeClientState::Connecting) {
        return NativeCoordinatorResult::InvalidState;
    }

    entry->state = NativeClientState::Connected;
    return NativeCoordinatorResult::Success;
}

NativeCoordinatorResult NativeClientCoordinator::Reset(
    NativeProxyId proxyId) noexcept {
    if (proxyId == 0) {
        return NativeCoordinatorResult::InvalidArgument;
    }

    ExclusiveLock guard(&lock_);
    auto* entry = FindEntry(proxyId);
    if (entry == nullptr) {
        return NativeCoordinatorResult::NotRegistered;
    }

    ++entry->generation;
    if (entry->generation == 0) {
        ++entry->generation;
    }
    entry->state = NativeClientState::Registered;
    entry->decision = ClientRouteDecision::Reject;
    entry->route = ClientRoute{};
    return NativeCoordinatorResult::Success;
}

NativeCoordinatorResult NativeClientCoordinator::Unregister(
    NativeProxyId proxyId) noexcept {
    if (proxyId == 0) {
        return NativeCoordinatorResult::InvalidArgument;
    }

    ExclusiveLock guard(&lock_);
    auto* entry = FindEntry(proxyId);
    if (entry != nullptr) {
        *entry = Entry{};
    }

    return NativeCoordinatorResult::Success;
}

bool NativeClientCoordinator::TryGetSnapshot(
    NativeProxyId proxyId,
    NativeClientSnapshot* snapshot) const noexcept {
    if (proxyId == 0 || snapshot == nullptr) {
        return false;
    }

    *snapshot = NativeClientSnapshot{};
    SharedLock guard(&lock_);
    const auto* entry = FindEntry(proxyId);
    if (entry == nullptr) {
        return false;
    }

    snapshot->registered = true;
    snapshot->proxyId = entry->proxyId;
    snapshot->generation = entry->generation;
    snapshot->state = entry->state;
    snapshot->decision = entry->decision;
    snapshot->route = entry->route;
    return true;
}

NativeCoordinatorSnapshot NativeClientCoordinator::Snapshot() const noexcept {
    NativeCoordinatorSnapshot snapshot{};
    SharedLock guard(&lock_);

    for (const auto& entry : entries_) {
        if (!entry.occupied) {
            continue;
        }

        ++snapshot.registered;
        switch (entry.state) {
            case NativeClientState::HostReady:
                ++snapshot.hostReady;
                break;
            case NativeClientState::Connecting:
                ++snapshot.connecting;
                break;
            case NativeClientState::Connected:
                ++snapshot.connected;
                break;
            case NativeClientState::Registered:
            case NativeClientState::ClassifyingRoute:
                break;
        }
    }

    return snapshot;
}

NativeClientCoordinator::Entry* NativeClientCoordinator::FindEntry(
    NativeProxyId proxyId) noexcept {
    for (auto& entry : entries_) {
        if (entry.occupied && entry.proxyId == proxyId) {
            return &entry;
        }
    }

    return nullptr;
}

const NativeClientCoordinator::Entry* NativeClientCoordinator::FindEntry(
    NativeProxyId proxyId) const noexcept {
    for (const auto& entry : entries_) {
        if (entry.occupied && entry.proxyId == proxyId) {
            return &entry;
        }
    }

    return nullptr;
}

NativeProxyId NativeClientCoordinator::NextProxyId() noexcept {
    if (lastProxyId_ == std::numeric_limits<NativeProxyId>::max()) {
        return 0;
    }

    ++lastProxyId_;
    return lastProxyId_;
}

NativeClientCoordinator& ProcessNativeClientCoordinator() noexcept {
    static NativeClientCoordinator coordinator;
    return coordinator;
}

} // namespace godswar::network
