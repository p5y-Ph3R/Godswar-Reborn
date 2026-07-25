#pragma once

#include "../src/EndpointManifest.h"
#include "../src/SecureGameControl.h"

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace godswar::network::tests {

struct SecureGrantTestBytes final {
    std::uint8_t bytes[SecureGameGrantMaximumBytes]{};
    std::size_t byteCount = 0;
};

struct SecureGrantTestClock final {
    std::uint64_t now = 1'000;
    bool available = true;
};

inline void WriteTestUInt16(
    std::uint8_t* destination,
    std::uint16_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value >> 8U);
    destination[1] = static_cast<std::uint8_t>(value);
}

inline void WriteTestUInt32(
    std::uint8_t* destination,
    std::uint32_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value >> 24U);
    destination[1] = static_cast<std::uint8_t>(value >> 16U);
    destination[2] = static_cast<std::uint8_t>(value >> 8U);
    destination[3] = static_cast<std::uint8_t>(value);
}

inline void WriteTestUInt64(
    std::uint8_t* destination,
    std::uint64_t value) noexcept {
    for (std::size_t index = 0; index < 8; ++index) {
        destination[index] = static_cast<std::uint8_t>(
            value >> ((7U - index) * 8U));
    }
}

inline bool TestClock(
    void* rawContext,
    std::uint64_t* unixMilliseconds) noexcept {
    auto* context = static_cast<SecureGrantTestClock*>(rawContext);
    if (context == nullptr ||
        unixMilliseconds == nullptr ||
        !context->available) {
        return false;
    }
    *unixMilliseconds = context->now;
    return true;
}

inline SecureGrantTestBytes BuildSecureGrantTestBytes(
    const char* routeHost = "game-route.reborn.test",
    const char* tlsHost = "game.reborn.test",
    const char* audience = "reborn-game",
    std::uint16_t routePort = 7000,
    std::uint16_t tlsPort = 7443,
    std::uint32_t serverId = 42,
    std::uint64_t expiry = 60'000) noexcept {
    SecureGrantTestBytes result{};
    if (routeHost == nullptr ||
        tlsHost == nullptr ||
        audience == nullptr) {
        return result;
    }

    const std::size_t routeBytes = std::strlen(routeHost);
    const std::size_t tlsBytes = std::strlen(tlsHost);
    const std::size_t audienceBytes = std::strlen(audience);
    const std::size_t total =
        SecureGameGrantFixedBytes +
        routeBytes +
        tlsBytes +
        audienceBytes;
    if (routeBytes == 0 ||
        routeBytes > SecureGameRouteHostMaximumBytes ||
        tlsBytes == 0 ||
        tlsBytes > SecureGameTlsHostMaximumBytes ||
        audienceBytes == 0 ||
        audienceBytes > SecureGameAudienceMaximumBytes ||
        total > sizeof(result.bytes)) {
        return result;
    }

    result.bytes[0] = 1;
    result.bytes[1] = static_cast<std::uint8_t>(routeBytes);
    result.bytes[2] = static_cast<std::uint8_t>(tlsBytes);
    result.bytes[3] = static_cast<std::uint8_t>(audienceBytes);
    WriteTestUInt16(result.bytes + 4, routePort);
    WriteTestUInt16(result.bytes + 6, tlsPort);
    WriteTestUInt32(result.bytes + 8, serverId);
    WriteTestUInt64(result.bytes + 12, expiry);
    for (std::size_t index = 0;
         index < SecureGameGrantIdBytes;
         ++index) {
        result.bytes[20 + index] =
            static_cast<std::uint8_t>(index + 1);
    }
    for (std::size_t index = 0;
         index < SecureGameTicketBytes;
         ++index) {
        result.bytes[36 + index] =
            static_cast<std::uint8_t>(index + 0x20);
    }

    std::size_t cursor = SecureGameGrantFixedBytes;
    std::memcpy(result.bytes + cursor, routeHost, routeBytes);
    cursor += routeBytes;
    std::memcpy(result.bytes + cursor, tlsHost, tlsBytes);
    cursor += tlsBytes;
    std::memcpy(result.bytes + cursor, audience, audienceBytes);
    result.byteCount = total;
    return result;
}

inline SecureGameGrant DecodeSecureGrantForTest(
    const SecureGrantTestBytes& bytes) noexcept {
    SecureGameGrant grant;
    static_cast<void>(TryDecodeSecureGameGrant(
        bytes.bytes,
        bytes.byteCount,
        &grant));
    return grant;
}

inline EndpointManifest BuildSecureGrantTestManifest() noexcept {
    EndpointManifest manifest{};
    constexpr char login[] = "login-route.reborn.test";
    constexpr char tlsLogin[] = "login.reborn.test";
    constexpr char suffix[] = "reborn.test";
    constexpr char audience[] = "reborn-game";
    manifest.logicalLoginPort = 5999;
    manifest.tlsLoginPort = 6599;
    std::memcpy(
        manifest.logicalLoginHost.bytes,
        login,
        sizeof(login) - 1);
    manifest.logicalLoginHost.length =
        static_cast<std::uint16_t>(sizeof(login) - 1);
    std::memcpy(
        manifest.tlsLoginHost.bytes,
        tlsLogin,
        sizeof(tlsLogin) - 1);
    manifest.tlsLoginHost.length =
        static_cast<std::uint16_t>(sizeof(tlsLogin) - 1);
    manifest.gameSuffixCount = 1;
    std::memcpy(
        manifest.gameSuffixes[0].bytes,
        suffix,
        sizeof(suffix) - 1);
    manifest.gameSuffixes[0].length =
        static_cast<std::uint16_t>(sizeof(suffix) - 1);
    manifest.audienceCount = 1;
    std::memcpy(
        manifest.audiences[0].bytes,
        audience,
        sizeof(audience) - 1);
    manifest.audiences[0].length =
        static_cast<std::uint8_t>(sizeof(audience) - 1);
    manifest.serverIdCount = 1;
    manifest.serverIds[0] = 42;
    return manifest;
}

} // namespace godswar::network::tests
