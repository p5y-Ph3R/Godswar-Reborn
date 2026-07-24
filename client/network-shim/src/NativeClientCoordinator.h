#pragma once

#include "ClientRoute.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

enum class NativeClientState : std::uint8_t {
    Registered = 0,
    ClassifyingRoute = 1,
    HostReady = 2,
    Connecting = 3,
    Connected = 4,
};

enum class NativeCoordinatorResult : std::uint8_t {
    Success = 0,
    InvalidArgument = 1,
    CapacityReached = 2,
    IdExhausted = 3,
    NotRegistered = 4,
    InvalidState = 5,
    RouteRejected = 6,
    OperationSuperseded = 7,
};

struct NativeClientSnapshot final {
    bool registered = false;
    NativeProxyId proxyId = 0;
    std::uint64_t generation = 0;
    NativeClientState state = NativeClientState::Registered;
    ClientRouteDecision decision = ClientRouteDecision::Reject;
    ClientRoute route{};
};

struct NativeCoordinatorSnapshot final {
    std::size_t capacity = NativeClientRegistryCapacity;
    std::size_t registered = 0;
    std::size_t hostReady = 0;
    std::size_t connecting = 0;
    std::size_t connected = 0;
};

class NativeClientCoordinator final {
public:
    NativeClientCoordinator() noexcept;
    explicit NativeClientCoordinator(ClientRoutePolicy policy) noexcept;

    NativeClientCoordinator(const NativeClientCoordinator&) = delete;
    NativeClientCoordinator& operator=(const NativeClientCoordinator&) =
        delete;

    NativeCoordinatorResult Register(NativeProxyId* proxyId) noexcept;
    NativeCoordinatorResult SetHost(
        NativeProxyId proxyId,
        const char* host,
        std::uint16_t port) noexcept;
    NativeCoordinatorResult BeginConnect(
        NativeProxyId proxyId,
        ClientBridgePlan* plan) noexcept;
    NativeCoordinatorResult MarkConnected(
        const ClientBridgePlan& plan) noexcept;
    NativeCoordinatorResult Reset(NativeProxyId proxyId) noexcept;
    NativeCoordinatorResult Unregister(NativeProxyId proxyId) noexcept;

    bool TryGetSnapshot(
        NativeProxyId proxyId,
        NativeClientSnapshot* snapshot) const noexcept;
    NativeCoordinatorSnapshot Snapshot() const noexcept;

private:
    struct Entry final {
        bool occupied = false;
        NativeProxyId proxyId = 0;
        std::uint64_t generation = 0;
        NativeClientState state = NativeClientState::Registered;
        ClientRouteDecision decision = ClientRouteDecision::Reject;
        ClientRoute route{};
    };

    Entry* FindEntry(NativeProxyId proxyId) noexcept;
    const Entry* FindEntry(NativeProxyId proxyId) const noexcept;
    NativeProxyId NextProxyId() noexcept;

    mutable SRWLOCK lock_{};
    ClientRoutePolicy policy_{};
    NativeProxyId lastProxyId_ = 0;
    Entry entries_[NativeClientRegistryCapacity]{};
};

NativeClientCoordinator& ProcessNativeClientCoordinator() noexcept;

} // namespace godswar::network
