#include "SecureUdpProtectedProtocolTests.h"

#include "../src/SecureUdpBindingProtocol.h"
#include "../src/SecureUdpProtectedProtocol.h"

#include <Windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::SecureUdpDirection;
using godswar::network::SecureUdpProtectedHeader;
using godswar::network::SecureUdpProtectedMaximumBytes;
using godswar::network::SecureUdpProtectedMessageType;
using godswar::network::TryDecodeSecureUdpBindingPacket;
using godswar::network::TryDeriveSecureUdpEpochKey;
using godswar::network::TryEncodeSecureUdpClientHello;
using godswar::network::TryInspectSecureUdpProtectedDatagram;
using godswar::network::TryOpenSecureUdpProtectedDatagram;
using godswar::network::TrySealSecureUdpProtectedDatagram;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

std::uint8_t HexNibble(char value) {
    if (value >= '0' && value <= '9') {
        return static_cast<std::uint8_t>(value - '0');
    }
    if (value >= 'A' && value <= 'F') {
        return static_cast<std::uint8_t>(value - 'A' + 10);
    }
    return 0xFF;
}

template<std::size_t Size>
bool ParseHex(
    const char* source,
    std::array<std::uint8_t, Size>* destination) {
    if (source == nullptr || destination == nullptr ||
        std::strlen(source) != Size * 2) {
        return false;
    }
    for (std::size_t index = 0; index < Size; ++index) {
        const auto high = HexNibble(source[index * 2]);
        const auto low = HexNibble(source[index * 2 + 1]);
        if (high > 0x0F || low > 0x0F) {
            return false;
        }
        (*destination)[index] =
            static_cast<std::uint8_t>((high << 4U) | low);
    }
    return true;
}

void CheckBindingHelloGolden() {
    std::array<std::uint8_t, 16> connection{};
    std::array<std::uint8_t, 16> nonce{};
    for (std::size_t index = 0; index < 16; ++index) {
        connection[index] =
            static_cast<std::uint8_t>(index + 1);
        nonce[index] =
            static_cast<std::uint8_t>(0xA0 + index);
    }
    std::array<std::uint8_t, 128> hello{};
    Check(
        TryEncodeSecureUdpClientHello(
            connection.data(),
            connection.size(),
            nonce.data(),
            nonce.size(),
            hello.data(),
            hello.size()),
        "native UDP ClientHello encodes");

    std::array<std::uint8_t, 128> expected{};
    Check(
        ParseHex(
            "475753550030010001000080"
            "0102030405060708090A0B0C0D0E0F10"
            "000000000000000000000000"
            "0030000000000000"
            "A0A1A2A3A4A5A6A7A8A9AAABACADAEAF"
            "0000000000000000"
            "000000000000000000000000000000000000000000000000"
            "0000000000000000000000000000000000000000000000000000000000000000",
            &expected),
        "binding golden fixture parses");
    Check(
        std::memcmp(
            hello.data(),
            expected.data(),
            hello.size()) == 0,
        "native UDP ClientHello matches server golden");

    godswar::network::SecureUdpBindingPacket decoded{};
    Check(
        TryDecodeSecureUdpBindingPacket(
            hello.data(),
            hello.size(),
            &decoded),
        "native UDP ClientHello decodes");
}

void CheckProtectedGolden() {
    std::array<std::uint8_t, 32> secret{};
    std::array<std::uint8_t, 16> connection{};
    for (std::size_t index = 0; index < secret.size(); ++index) {
        secret[index] = static_cast<std::uint8_t>(index);
    }
    for (std::size_t index = 0; index < connection.size(); ++index) {
        connection[index] =
            static_cast<std::uint8_t>(0x10 + index);
    }

    std::array<std::uint8_t, 32> expectedKey{};
    Check(
        ParseHex(
            "C27A8E9BF928AE027A3915F49E942F9273CE975F27CD775CC2E7ED894A00D5FA",
            &expectedKey),
        "protected golden key fixture parses");
    std::array<std::uint8_t, 32> key{};
    Check(
        TryDeriveSecureUdpEpochKey(
            secret.data(),
            secret.size(),
            connection.data(),
            connection.size(),
            0x01020304,
            SecureUdpDirection::ClientToServer,
            1,
            key.data(),
            key.size()) &&
            std::memcmp(
                key.data(),
                expectedKey.data(),
                key.size()) == 0,
        "Windows CNG HKDF matches server golden");

    std::array<std::uint8_t, 16> ping{};
    Check(
        ParseHex(
            "000000000000000100000000075BCD15",
            &ping),
        "protected golden Ping parses");
    SecureUdpProtectedHeader header{};
    header.keyEpoch = 1;
    header.messageType =
        SecureUdpProtectedMessageType::Ping;
    header.payloadBytes =
        static_cast<std::uint16_t>(ping.size());
    std::array<std::uint8_t, 96> encoded{};
    std::size_t written = 0;
    Check(
        TrySealSecureUdpProtectedDatagram(
            secret.data(),
            secret.size(),
            connection.data(),
            connection.size(),
            0x01020304,
            SecureUdpDirection::ClientToServer,
            header,
            ping.data(),
            ping.size(),
            encoded.data(),
            encoded.size(),
            &written) &&
            written == encoded.size(),
        "native AES-GCM golden Ping seals");

    std::array<std::uint8_t, 96> expected{};
    Check(
        ParseHex(
            "475753500040010001000060"
            "101112131415161718191A1B1C1D1E1F"
            "000000010000000000000000"
            "0000000000000000000000000000000000000000"
            "01000010"
            "36486AB35FD8E6650AB613A49B881EDD"
            "7D174FF3A7946AA12C991108036242C6",
            &expected),
        "protected golden datagram fixture parses");
    Check(
        std::memcmp(
            encoded.data(),
            expected.data(),
            expected.size()) == 0,
        "native protected datagram matches server golden");

    std::array<std::uint8_t, 16> plaintext{};
    SecureUdpProtectedHeader opened{};
    std::size_t plaintextBytes = 0;
    Check(
        TryOpenSecureUdpProtectedDatagram(
            secret.data(),
            secret.size(),
            connection.data(),
            connection.size(),
            0x01020304,
            SecureUdpDirection::ClientToServer,
            expected.data(),
            expected.size(),
            &opened,
            plaintext.data(),
            plaintext.size(),
            &plaintextBytes) &&
            plaintextBytes == ping.size() &&
            std::memcmp(
                plaintext.data(),
                ping.data(),
                ping.size()) == 0,
        "native protected datagram opens server golden");

    SecureZeroMemory(secret.data(), secret.size());
    SecureZeroMemory(key.data(), key.size());
    SecureZeroMemory(plaintext.data(), plaintext.size());
}

void CheckBoundsTamperAndAcks() {
    std::array<std::uint8_t, 96> golden{};
    Check(
        ParseHex(
            "475753500040010001000060"
            "101112131415161718191A1B1C1D1E1F"
            "000000010000000000000000"
            "0000000000000000000000000000000000000000"
            "01000010"
            "36486AB35FD8E6650AB613A49B881EDD"
            "7D174FF3A7946AA12C991108036242C6",
            &golden),
        "tamper fixture parses");
    std::array<std::uint8_t, 16> connection{};
    for (std::size_t index = 0; index < connection.size(); ++index) {
        connection[index] =
            static_cast<std::uint8_t>(0x10 + index);
    }

    for (std::size_t length = 0; length < golden.size(); ++length) {
        SecureUdpProtectedHeader ignored{};
        Check(
            !TryInspectSecureUdpProtectedDatagram(
                connection.data(),
                connection.size(),
                golden.data(),
                length,
                &ignored),
            "protected truncation rejects");
    }
    std::array<std::uint8_t, SecureUdpProtectedMaximumBytes + 1>
        oversized{};
    SecureUdpProtectedHeader ignored{};
    Check(
        !TryInspectSecureUdpProtectedDatagram(
            connection.data(),
            connection.size(),
            oversized.data(),
            oversized.size(),
            &ignored),
        "protected path-MTU overflow rejects");

    auto invalidAck = golden;
    invalidAck[43] = 1;
    invalidAck[51] = 1;
    invalidAck[59] = 2;
    Check(
        !TryInspectSecureUdpProtectedDatagram(
            connection.data(),
            connection.size(),
            invalidAck.data(),
            invalidAck.size(),
            &ignored),
        "ack mask underflow rejects");

    std::array<std::uint8_t, 32> secret{};
    for (std::size_t index = 0; index < secret.size(); ++index) {
        secret[index] = static_cast<std::uint8_t>(index);
    }
    std::array<std::uint8_t, 16> plaintext{};
    for (std::size_t offset = 0; offset < golden.size(); ++offset) {
        auto mutated = golden;
        mutated[offset] ^= 1;
        SecureUdpProtectedHeader header{};
        std::size_t openedBytes = 0;
        Check(
            !TryOpenSecureUdpProtectedDatagram(
                secret.data(),
                secret.size(),
                connection.data(),
                connection.size(),
                0x01020304,
                SecureUdpDirection::ClientToServer,
                mutated.data(),
                mutated.size(),
                &header,
                plaintext.data(),
                plaintext.size(),
                &openedBytes),
            "protected header/cipher/tag mutation rejects");
    }
    SecureZeroMemory(secret.data(), secret.size());
    SecureZeroMemory(plaintext.data(), plaintext.size());
}

} // namespace

int RunSecureUdpProtectedProtocolTests() {
    Failures = 0;
    CheckBindingHelloGolden();
    CheckProtectedGolden();
    CheckBoundsTamperAndAcks();
    return Failures;
}
