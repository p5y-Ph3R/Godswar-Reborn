#include "SecureClientProtocolTests.h"

#include "../src/SecureClientProtocol.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>

namespace {

using godswar::network::SecureEndpointRole;
using godswar::network::SecureFrameDirection;
using godswar::network::SecureFrameHeader;
using godswar::network::SecureFrameType;
using godswar::network::SecureServerPrefaceStatus;
using godswar::network::SecureServerPrefaceView;
using godswar::network::TryDecodeSecureFrameHeader;
using godswar::network::TryDecodeSecureServerPreface;
using godswar::network::TryEncodeSecureClientPreface;
using godswar::network::TryEncodeSecureFrameHeader;
using godswar::network::TryGetNextSecureSequence;

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

void MakeServerPreface(
    std::uint8_t* output,
    SecureServerPrefaceStatus status,
    SecureEndpointRole role) {
    std::memset(
        output,
        0,
        godswar::network::SecureServerPrefaceBytes);
    std::memcpy(output, "GWSS", 4);
    Write16(output + 4, 40);
    Write16(output + 6, 1);
    Write16(output + 8, 0);
    output[10] = static_cast<std::uint8_t>(status);
    output[11] = static_cast<std::uint8_t>(role);
    Write32(output + 16, 16 * 1024);
    Write16(output + 20, 30);
    Write16(output + 22, 90);
    if (status == SecureServerPrefaceStatus::Ok) {
        output[24] = 1;
    }
}

void CheckPrefaces() {
    std::uint8_t instance[16]{};
    instance[0] = 0x11;
    std::uint8_t hash[32]{};
    for (std::size_t index = 0; index < sizeof(hash); ++index) {
        hash[index] = static_cast<std::uint8_t>(index);
    }
    std::uint8_t encoded[72]{};
    Check(
        TryEncodeSecureClientPreface(
            SecureEndpointRole::Login,
            instance,
            sizeof(instance),
            hash,
            sizeof(hash),
            encoded,
            sizeof(encoded)),
        "client preface encoding failed");
    Check(
        std::memcmp(encoded, "GWSC", 4) == 0 &&
            encoded[4] == 0 &&
            encoded[5] == 72 &&
            encoded[6] == 0 &&
            encoded[7] == 1 &&
            encoded[12] == 1 &&
            encoded[22] == 0x40 &&
            encoded[24] == 0x11 &&
            std::memcmp(encoded + 40, hash, sizeof(hash)) == 0,
        "client preface golden bytes changed");

    std::memset(instance, 0, sizeof(instance));
    Check(
        !TryEncodeSecureClientPreface(
            SecureEndpointRole::Login,
            instance,
            sizeof(instance),
            hash,
            sizeof(hash),
            encoded,
            sizeof(encoded)),
        "zero client-instance ID was accepted");

    std::uint8_t server[40]{};
    MakeServerPreface(
        server,
        SecureServerPrefaceStatus::Ok,
        SecureEndpointRole::Login);
    SecureServerPrefaceView decoded{};
    Check(
        TryDecodeSecureServerPreface(
            server,
            sizeof(server),
            SecureEndpointRole::Login,
            &decoded) &&
            decoded.status == SecureServerPrefaceStatus::Ok &&
            decoded.connectionId[0] == 1,
        "server preface decoding failed");

    server[13] = 1;
    Check(
        !TryDecodeSecureServerPreface(
            server,
            sizeof(server),
            SecureEndpointRole::Login,
            &decoded),
        "nonzero server-preface reserved byte was accepted");
    MakeServerPreface(
        server,
        SecureServerPrefaceStatus::ServerBusy,
        SecureEndpointRole::Login);
    server[24] = 1;
    Check(
        !TryDecodeSecureServerPreface(
            server,
            sizeof(server),
            SecureEndpointRole::Login,
            &decoded),
        "rejection preface accepted nonzero connection ID");
}

void CheckFrames() {
    SecureFrameHeader header{
        3,
        SecureFrameType::LegacyBytes,
        1};
    std::uint8_t encoded[16]{};
    Check(
        TryEncodeSecureFrameHeader(
            header,
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            encoded,
            sizeof(encoded)),
        "frame-header encoding failed");
    const std::uint8_t expected[] = {
        0, 0, 0, 3,
        1, 0,
        0, 0,
        0, 0, 0, 0, 0, 0, 0, 1,
    };
    Check(
        std::memcmp(encoded, expected, sizeof(expected)) == 0,
        "frame-header golden bytes changed");

    SecureFrameHeader decoded{};
    Check(
        TryDecodeSecureFrameHeader(
            encoded,
            sizeof(encoded),
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            1,
            &decoded) &&
            decoded.payloadBytes == 3 &&
            decoded.type == SecureFrameType::LegacyBytes &&
            decoded.sequence == 1,
        "frame-header decoding failed");
    Check(
        !TryDecodeSecureFrameHeader(
            encoded,
            sizeof(encoded),
            SecureEndpointRole::Login,
            static_cast<SecureFrameDirection>(99),
            1,
            &decoded),
        "unknown frame direction was accepted");

    encoded[7] = 1;
    Check(
        !TryDecodeSecureFrameHeader(
            encoded,
            sizeof(encoded),
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            1,
            &decoded),
        "nonzero frame flags were accepted");
    encoded[7] = 0;
    Check(
        !TryDecodeSecureFrameHeader(
            encoded,
            sizeof(encoded),
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            2,
            &decoded),
        "unexpected frame sequence was accepted");

    Check(
        !TryEncodeSecureFrameHeader(
            SecureFrameHeader{8, SecureFrameType::Ping, 1},
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            encoded,
            sizeof(encoded)),
        "client-to-server Ping was accepted");
    Check(
        TryEncodeSecureFrameHeader(
            SecureFrameHeader{8, SecureFrameType::Pong, 1},
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            encoded,
            sizeof(encoded)),
        "valid client-to-server Pong was rejected");
    Check(
        !TryEncodeSecureFrameHeader(
            SecureFrameHeader{0, SecureFrameType::LegacyBytes, 1},
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            encoded,
            sizeof(encoded)),
        "empty LegacyBytes frame was accepted");
    Check(
        !TryEncodeSecureFrameHeader(
            SecureFrameHeader{
                16 * 1024 + 1,
                SecureFrameType::LegacyBytes,
                1},
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            encoded,
            sizeof(encoded)),
        "oversized LegacyBytes frame was accepted");
}

void CheckSequences() {
    std::uint64_t next = 0;
    Check(
        TryGetNextSecureSequence(1, &next) && next == 2,
        "secure sequence did not increment");
    Check(
        !TryGetNextSecureSequence(0, &next),
        "zero secure sequence incremented");
    Check(
        !TryGetNextSecureSequence(
            (std::numeric_limits<std::uint64_t>::max)(),
            &next),
        "maximum secure sequence wrapped");
}

} // namespace

int RunSecureClientProtocolTests() {
    Failures = 0;
    CheckPrefaces();
    CheckFrames();
    CheckSequences();
    return Failures;
}
