#pragma once

#include "NativeNetworkLimits.h"

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace godswar::network {

using NativeProxyId = std::uint64_t;

enum class ClientRouteDecision : std::uint8_t {
    PassThrough = 0,
    Login = 1,
    Game = 2,
    Reject = 3,
};

enum class ClientEndpointRole : std::uint8_t {
    None = 0,
    Login = 1,
    Game = 2,
};

struct ClientRoute final {
    char host[NativeRouteHostCapacity]{};
    std::uint16_t hostLength = 0;
    std::uint16_t port = 0;
};

inline bool TryCopyClientRoute(
    const char* host,
    std::uint16_t port,
    ClientRoute* route) noexcept {
    if (host == nullptr || port == 0 || route == nullptr) {
        return false;
    }

    std::size_t length = 0;
    while (length < NativeRouteHostCapacity && host[length] != '\0') {
        ++length;
    }

    if (length == 0 || length > NativeRouteHostMaximumBytes) {
        return false;
    }

    ClientRoute candidate{};
    std::memcpy(candidate.host, host, length);
    candidate.host[length] = '\0';
    candidate.hostLength = static_cast<std::uint16_t>(length);
    candidate.port = port;
    *route = candidate;
    return true;
}

inline bool ClientRoutesEqual(
    const ClientRoute& left,
    const ClientRoute& right) noexcept {
    return left.port == right.port &&
        left.hostLength == right.hostLength &&
        left.hostLength <= NativeRouteHostMaximumBytes &&
        std::memcmp(left.host, right.host, left.hostLength) == 0;
}

inline bool IsKnownClientRouteDecision(
    ClientRouteDecision decision) noexcept {
    switch (decision) {
        case ClientRouteDecision::PassThrough:
        case ClientRouteDecision::Login:
        case ClientRouteDecision::Game:
        case ClientRouteDecision::Reject:
            return true;
    }

    return false;
}

inline ClientEndpointRole RoleForDecision(
    ClientRouteDecision decision) noexcept {
    switch (decision) {
        case ClientRouteDecision::Login:
            return ClientEndpointRole::Login;
        case ClientRouteDecision::Game:
            return ClientEndpointRole::Game;
        case ClientRouteDecision::PassThrough:
        case ClientRouteDecision::Reject:
            return ClientEndpointRole::None;
    }

    return ClientEndpointRole::None;
}

using ClientRouteClassifier = ClientRouteDecision (*)(
    void* context,
    NativeProxyId proxyId,
    const ClientRoute& route) noexcept;

struct ClientRoutePolicy final {
    void* context = nullptr;
    ClientRouteClassifier classify = nullptr;
};

inline ClientRouteDecision DisabledClientRouteClassifier(
    void*,
    NativeProxyId,
    const ClientRoute&) noexcept {
    return ClientRouteDecision::PassThrough;
}

inline ClientRoutePolicy DisabledClientRoutePolicy() noexcept {
    return ClientRoutePolicy{
        nullptr,
        DisabledClientRouteClassifier,
    };
}

struct ClientBridgePlan final {
    NativeProxyId proxyId = 0;
    std::uint64_t generation = 0;
    ClientRouteDecision decision = ClientRouteDecision::Reject;
    ClientEndpointRole role = ClientEndpointRole::None;
    ClientRoute logicalRoute{};
};

} // namespace godswar::network
