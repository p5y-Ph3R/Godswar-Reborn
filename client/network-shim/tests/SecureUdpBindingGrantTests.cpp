#include "SecureUdpBindingGrantTests.h"

#include "../src/SecureClientProtocol.h"
#include "../src/SecureUdpBindingGrant.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <utility>

namespace {

using godswar::network::SecureUdpBindingGrant;
using godswar::network::SecureUdpBindingGrantBytes;
using godswar::network::SecureUdpConnectionIdBytes;
using godswar::network::SecureUdpProofKeyBytes;
using godswar::network::SecureEndpointRole;
using godswar::network::SecureFrameDirection;
using godswar::network::SecureFrameHeader;
using godswar::network::SecureFrameHeaderBytes;
using godswar::network::SecureFrameType;
using godswar::network::TryDecodeSecureUdpBindingGrant;
using godswar::network::TryEncodeSecureFrameHeader;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void Write16(std::uint8_t* output, std::uint16_t value) {
    output[0] = static_cast<std::uint8_t>(value >> 8U);
    output[1] = static_cast<std::uint8_t>(value);
}

void Write32(std::uint8_t* output, std::uint32_t value) {
    output[0] = static_cast<std::uint8_t>(value >> 24U);
    output[1] = static_cast<std::uint8_t>(value >> 16U);
    output[2] = static_cast<std::uint8_t>(value >> 8U);
    output[3] = static_cast<std::uint8_t>(value);
}

void Write64(std::uint8_t* output, std::uint64_t value) {
    for (std::size_t index = 0; index < 8; ++index) {
        output[index] = static_cast<std::uint8_t>(
            value >> ((7U - index) * 8U));
    }
}

std::array<std::uint8_t, SecureUdpBindingGrantBytes>
CanonicalGrant() {
    std::array<std::uint8_t, SecureUdpBindingGrantBytes> bytes{};
    std::memcpy(bytes.data(), "GWUG", 4);
    Write16(bytes.data() + 4, 1);
    Write16(bytes.data() + 6, 0);
    Write16(bytes.data() + 8, 7444);
    Write32(bytes.data() + 12, 100);
    Write64(bytes.data() + 16, 1'900'000'000'123ULL);
    for (std::size_t index = 0;
         index < SecureUdpConnectionIdBytes;
         ++index) {
        bytes[24 + index] =
            static_cast<std::uint8_t>(index + 1);
    }
    for (std::size_t index = 0;
         index < SecureUdpProofKeyBytes;
         ++index) {
        bytes[40 + index] =
            static_cast<std::uint8_t>(0x80U + index);
    }
    return bytes;
}

void CheckCanonicalDecodeAndOwnership() {
    const auto bytes = CanonicalGrant();
    SecureUdpBindingGrant grant;
    std::uint8_t connectionId[SecureUdpConnectionIdBytes]{};
    std::uint8_t proofKey[SecureUdpProofKeyBytes]{};
    Check(
        TryDecodeSecureUdpBindingGrant(
            bytes.data(),
            bytes.size(),
            &grant) &&
            grant.IsValid() &&
            grant.UdpPort() == 7444 &&
            grant.ServerId() == 100 &&
            grant.ExpiryUnixMilliseconds() ==
                1'900'000'000'123ULL &&
            grant.TryCopyConnectionId(
                connectionId,
                sizeof(connectionId)) &&
            grant.TryCopyProofKey(proofKey, sizeof(proofKey)) &&
            std::memcmp(
                connectionId,
                bytes.data() + 24,
                sizeof(connectionId)) == 0 &&
            std::memcmp(
                proofKey,
                bytes.data() + 40,
                sizeof(proofKey)) == 0,
        "canonical UDP binding grant did not decode exactly");

    SecureUdpBindingGrant moved(std::move(grant));
    Check(
        moved.IsValid() &&
            !grant.IsValid() &&
            moved.ConnectionIdEquals(
                connectionId,
                sizeof(connectionId)),
        "UDP binding grant move retained duplicate ownership");

    connectionId[0] ^= 1;
    Check(
        !moved.ConnectionIdEquals(
            connectionId,
            sizeof(connectionId)),
        "changed TLS connection ID matched a UDP grant");
    moved.Clear();
    std::memset(proofKey, 0xCC, sizeof(proofKey));
    Check(
        !moved.TryCopyProofKey(proofKey, sizeof(proofKey)) &&
            proofKey[0] == 0 &&
            proofKey[sizeof(proofKey) - 1] == 0 &&
            moved.UdpPort() == 0,
        "cleared UDP grant exposed stale secret material");
}

void CheckStrictRejection() {
    const auto canonical = CanonicalGrant();
    for (std::size_t size = 0;
         size < SecureUdpBindingGrantBytes;
         ++size) {
        SecureUdpBindingGrant grant;
        Check(
            !TryDecodeSecureUdpBindingGrant(
                canonical.data(),
                size,
                &grant) &&
                !grant.IsValid(),
            "truncated UDP binding grant was accepted");
    }

    std::array<std::uint8_t, SecureUdpBindingGrantBytes + 1>
        oversized{};
    std::memcpy(
        oversized.data(),
        canonical.data(),
        canonical.size());
    SecureUdpBindingGrant grant;
    Check(
        !TryDecodeSecureUdpBindingGrant(
            oversized.data(),
            oversized.size(),
            &grant),
        "UDP binding grant trailing byte was accepted");

    Check(
        TryDecodeSecureUdpBindingGrant(
            canonical.data(),
            canonical.size(),
            &grant) &&
            grant.IsValid(),
        "stale-output rejection fixture did not decode");
    auto badMagic = canonical;
    badMagic[0] ^= 1;
    Check(
        !TryDecodeSecureUdpBindingGrant(
            badMagic.data(),
            badMagic.size(),
            &grant) &&
            !grant.IsValid(),
        "failed UDP grant decode retained an earlier proof key");

    constexpr std::size_t singleByteMutations[] = {
        0, 5, 7,
    };
    for (const auto offset : singleByteMutations) {
        auto changed = canonical;
        changed[offset] ^= 1;
        Check(
            !TryDecodeSecureUdpBindingGrant(
                changed.data(),
                changed.size(),
                &grant),
            "malformed UDP grant fixed field was accepted");
    }

    auto authoritative = canonical;
    Write16(
        authoritative.data() + 10,
        static_cast<std::uint16_t>(
            godswar::network::SecureUdpBindingCapability::
                AuthoritativeMovement));
    Check(
        TryDecodeSecureUdpBindingGrant(
            authoritative.data(),
            authoritative.size(),
            &grant) &&
            grant.HasCapability(
                godswar::network::SecureUdpBindingCapability::
                    AuthoritativeMovement),
        "known UDP grant capability was rejected");

    auto unknownCapability = canonical;
    Write16(unknownCapability.data() + 10, 2);
    Check(
        !TryDecodeSecureUdpBindingGrant(
            unknownCapability.data(),
            unknownCapability.size(),
            &grant),
        "unknown UDP grant capability was accepted");

    auto zeroPort = canonical;
    std::memset(zeroPort.data() + 8, 0, 2);
    Check(
        !TryDecodeSecureUdpBindingGrant(
            zeroPort.data(),
            zeroPort.size(),
            &grant),
        "zero UDP grant port was accepted");

    auto zeroServer = canonical;
    std::memset(zeroServer.data() + 12, 0, 4);
    Check(
        !TryDecodeSecureUdpBindingGrant(
            zeroServer.data(),
            zeroServer.size(),
            &grant),
        "zero UDP grant server ID was accepted");

    auto zeroExpiry = canonical;
    std::memset(zeroExpiry.data() + 16, 0, 8);
    Check(
        !TryDecodeSecureUdpBindingGrant(
            zeroExpiry.data(),
            zeroExpiry.size(),
            &grant),
        "zero UDP grant expiry was accepted");

    auto zeroConnection = canonical;
    std::memset(
        zeroConnection.data() + 24,
        0,
        SecureUdpConnectionIdBytes);
    Check(
        !TryDecodeSecureUdpBindingGrant(
            zeroConnection.data(),
            zeroConnection.size(),
            &grant),
        "zero UDP grant connection ID was accepted");

    auto zeroKey = canonical;
    std::memset(
        zeroKey.data() + 40,
        0,
        SecureUdpProofKeyBytes);
    Check(
        !TryDecodeSecureUdpBindingGrant(
            zeroKey.data(),
            zeroKey.size(),
            &grant),
        "zero UDP grant proof key was accepted");

    Check(
        !TryDecodeSecureUdpBindingGrant(
            nullptr,
            canonical.size(),
            &grant) &&
            !grant.IsValid() &&
            !TryDecodeSecureUdpBindingGrant(
                canonical.data(),
                canonical.size(),
                nullptr),
        "invalid UDP grant decoder arguments were accepted");
}

void CheckFrameBoundary() {
    std::uint8_t header[SecureFrameHeaderBytes]{};
    Check(
        TryEncodeSecureFrameHeader(
            SecureFrameHeader{
                static_cast<std::uint32_t>(
                    SecureUdpBindingGrantBytes),
                SecureFrameType::UdpBindingGrant,
                2},
            SecureEndpointRole::Game,
            SecureFrameDirection::ServerToClient,
            header,
            sizeof(header)),
        "canonical UDP binding grant frame was rejected");
    Check(
        !TryEncodeSecureFrameHeader(
            SecureFrameHeader{
                static_cast<std::uint32_t>(
                    SecureUdpBindingGrantBytes - 1),
                SecureFrameType::UdpBindingGrant,
                2},
            SecureEndpointRole::Game,
            SecureFrameDirection::ServerToClient,
            header,
            sizeof(header)) &&
            !TryEncodeSecureFrameHeader(
                SecureFrameHeader{
                    static_cast<std::uint32_t>(
                        SecureUdpBindingGrantBytes),
                    SecureFrameType::UdpBindingGrant,
                    2},
                SecureEndpointRole::Login,
                SecureFrameDirection::ServerToClient,
                header,
                sizeof(header)) &&
            !TryEncodeSecureFrameHeader(
                SecureFrameHeader{
                    static_cast<std::uint32_t>(
                        SecureUdpBindingGrantBytes),
                    SecureFrameType::UdpBindingGrant,
                    2},
                SecureEndpointRole::Game,
                SecureFrameDirection::ClientToServer,
                header,
                sizeof(header)),
        "UDP binding grant escaped its exact game server frame boundary");
}

} // namespace

int RunSecureUdpBindingGrantTests() {
    Failures = 0;
    CheckCanonicalDecodeAndOwnership();
    CheckStrictRejection();
    CheckFrameBoundary();
    return Failures;
}
