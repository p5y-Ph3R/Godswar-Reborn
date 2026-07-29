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
using godswar::network::SecureLegacyCommandOperation;
using godswar::network::SecureServerPrefaceStatus;
using godswar::network::SecureServerPrefaceView;
using godswar::network::TryDecodeSecureFrameHeader;
using godswar::network::TryDecodeSecureServerPreface;
using godswar::network::TryEncodeSecureClientPreface;
using godswar::network::TryEncodeSecureFrameHeader;
using godswar::network::TryGetNextSecureSequence;
using godswar::network::TryDecodeSecureLegacyCommandOperation;
using godswar::network::TryCreateSecureLegacyCommandOperation;
using godswar::network::TryEncodeSecureLegacyCommandOperation;

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

void CheckLegacyCommandOperation() {
    SecureLegacyCommandOperation operation{};
    operation.packetBytes = 32;
    operation.opcode = 0x1234;
    const std::uint8_t operationId[] = {
        0x00, 0x11, 0x22, 0x33,
        0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xAA, 0xBB,
        0xCC, 0xDD, 0xEE, 0xFF,
    };
    std::memcpy(
        operation.operationId,
        operationId,
        sizeof(operationId));

    std::uint8_t encoded[
        godswar::network::
            SecureLegacyCommandOperationPayloadBytes]{};
    Check(
        TryEncodeSecureLegacyCommandOperation(
            operation,
            encoded,
            sizeof(encoded)),
        "legacy command operation encoding failed");
    const std::uint8_t expected[] = {
        0x01, 0x00, 0x00, 0x20,
        0x12, 0x34, 0x00, 0x00,
        0x00, 0x11, 0x22, 0x33,
        0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xAA, 0xBB,
        0xCC, 0xDD, 0xEE, 0xFF,
    };
    Check(
        std::memcmp(encoded, expected, sizeof(expected)) == 0,
        "legacy command operation golden bytes changed");

    SecureLegacyCommandOperation decoded{};
    Check(
        TryDecodeSecureLegacyCommandOperation(
            encoded,
            sizeof(encoded),
            &decoded) &&
            decoded.packetBytes == operation.packetBytes &&
            decoded.opcode == operation.opcode &&
            std::memcmp(
                decoded.operationId,
                operationId,
                sizeof(operationId)) == 0,
        "legacy command operation round trip failed");

    std::uint8_t header[16]{};
    Check(
        TryEncodeSecureFrameHeader(
            SecureFrameHeader{
                sizeof(encoded),
                SecureFrameType::LegacyCommandOperation,
                1},
            SecureEndpointRole::Game,
            SecureFrameDirection::ClientToServer,
            header,
            sizeof(header)),
        "game command-operation frame was rejected");
    Check(
        !TryEncodeSecureFrameHeader(
            SecureFrameHeader{
                sizeof(encoded),
                SecureFrameType::LegacyCommandOperation,
                1},
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            header,
            sizeof(header)),
        "login command-operation frame was accepted");

    encoded[1] = 1;
    Check(
        !TryDecodeSecureLegacyCommandOperation(
            encoded,
            sizeof(encoded),
            &decoded),
        "command-operation flags were accepted");
    encoded[1] = 0;
    std::memset(encoded + 8, 0, 16);
    Check(
        !TryDecodeSecureLegacyCommandOperation(
            encoded,
            sizeof(encoded),
            &decoded),
        "zero command-operation UUID was accepted");

    SecureLegacyCommandOperation generatedOne{};
    SecureLegacyCommandOperation generatedTwo{};
    Check(
        TryCreateSecureLegacyCommandOperation(
            8,
            0x2711,
            &generatedOne) &&
            TryCreateSecureLegacyCommandOperation(
                8,
                0x2711,
                &generatedTwo) &&
            (generatedOne.operationId[6] & 0xF0U) == 0x40U &&
            (generatedOne.operationId[8] & 0xC0U) == 0x80U &&
            std::memcmp(
                generatedOne.operationId,
                generatedTwo.operationId,
                sizeof(generatedOne.operationId)) != 0,
        "shim CSPRNG did not create distinct canonical UUIDs");
}

} // namespace

int RunSecureClientProtocolTests() {
    Failures = 0;
    CheckPrefaces();
    CheckFrames();
    CheckSequences();
    CheckLegacyCommandOperation();
    return Failures;
}
