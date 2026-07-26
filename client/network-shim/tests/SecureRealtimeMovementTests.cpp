#include "SecureRealtimeMovementTests.h"

#include "../src/SecureClientProtocol.h"
#include "../src/SecureRealtimeMovementProtocol.h"
#include "../src/SecureRealtimeMovementRouter.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>

namespace {

using namespace godswar::network;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void WriteLittle16(std::uint8_t* output, std::uint16_t value) {
    output[0] = static_cast<std::uint8_t>(value);
    output[1] = static_cast<std::uint8_t>(value >> 8U);
}

void WriteLittle32(std::uint8_t* output, std::uint32_t value) {
    output[0] = static_cast<std::uint8_t>(value);
    output[1] = static_cast<std::uint8_t>(value >> 8U);
    output[2] = static_cast<std::uint8_t>(value >> 16U);
    output[3] = static_cast<std::uint8_t>(value >> 24U);
}

std::uint32_t FloatBits(float value) {
    std::uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

std::array<std::uint8_t, SecureRealtimeLegacyMovementBytes>
LegacyMovement(
    std::uint32_t state = 0x31323334,
    float x = 1.5F,
    float z = -2.25F,
    float auxiliary = 0.5F) {
    std::array<
        std::uint8_t,
        SecureRealtimeLegacyMovementBytes> packet{};
    WriteLittle16(
        packet.data(),
        static_cast<std::uint16_t>(packet.size()));
    WriteLittle16(
        packet.data() + 2,
        SecureRealtimeLegacyMovementOpcode);
    WriteLittle32(packet.data() + 4, state);
    WriteLittle32(packet.data() + 8, FloatBits(x));
    WriteLittle32(packet.data() + 12, FloatBits(z));
    WriteLittle32(
        packet.data() + 16,
        FloatBits(auxiliary));
    return packet;
}

SecureRealtimeMovementInput CanonicalInput(
    std::uint8_t flags = 0) {
    SecureRealtimeMovementInput input{};
    input.flags = flags;
    input.transportEpoch = 0x01020304;
    input.inputId = 0x0102030405060708ULL;
    input.clientMonotonicMilliseconds =
        0x1112131415161718ULL;
    input.worldGeneration = 0x21222324;
    input.legacyState = 0x31323334;
    input.x = 1.5F;
    input.z = -2.25F;
    input.auxiliary = 0.5F;
    input.mapId = 0x2A;
    return input;
}

SecureRealtimePositionSnapshot CanonicalSnapshot(
    std::uint64_t sequence = 0x2122232425262728ULL) {
    SecureRealtimePositionSnapshot snapshot{};
    snapshot.flags = static_cast<std::uint8_t>(
        SecureRealtimePositionSnapshotFlag::Keyframe);
    snapshot.transportEpoch = 0x01020304;
    snapshot.serverTick = 0x0102030405060708ULL;
    snapshot.revision = 0x1112131415161718ULL;
    snapshot.snapshotSequence = sequence;
    snapshot.worldGeneration = 0x31323334;
    snapshot.legacyState = 0x41424344;
    snapshot.x = 1.5F;
    snapshot.z = -2.25F;
    snapshot.auxiliary = 0.5F;
    snapshot.mapId = 0x2A;
    return snapshot;
}

void CheckProtocolGoldenAndBounds() {
    const auto legacy = LegacyMovement();
    SecureRealtimeLegacyMovement parsed{};
    Check(
        TryParseSecureRealtimeLegacyMovement(
            legacy.data(),
            legacy.size(),
            &parsed) &&
            parsed.legacyState == 0x31323334 &&
            parsed.x == 1.5F &&
            parsed.z == -2.25F &&
            parsed.auxiliary == 0.5F,
        "legacy movement parser changed the 20-byte ABI");

    const auto canonical = CanonicalInput();
    std::array<
        std::uint8_t,
        SecureRealtimeMovementInputBytes> encoded{};
    Check(
        TryEncodeSecureRealtimeMovementInput(
            canonical,
            SecureRealtimeMovementSource::Udp,
            encoded.data(),
            encoded.size()) &&
            encoded[0] == 1 &&
            encoded[1] == 0 &&
            encoded[2] == 0 &&
            encoded[3] == 52 &&
            encoded[44] == 0x2A &&
            encoded[45] == 0 &&
            encoded[46] == 0 &&
            encoded[47] == 0 &&
            encoded[48] == 0x27 &&
            encoded[49] == 0xD2 &&
            encoded[50] == 0 &&
            encoded[51] == 20,
        "movement input golden offsets changed");
    constexpr std::array<
        std::uint8_t,
        SecureRealtimeMovementInputBytes> expectedInput{
        0x01, 0x00, 0x00, 0x34,
        0x01, 0x02, 0x03, 0x04,
        0x01, 0x02, 0x03, 0x04,
        0x05, 0x06, 0x07, 0x08,
        0x11, 0x12, 0x13, 0x14,
        0x15, 0x16, 0x17, 0x18,
        0x21, 0x22, 0x23, 0x24,
        0x31, 0x32, 0x33, 0x34,
        0x3F, 0xC0, 0x00, 0x00,
        0xC0, 0x10, 0x00, 0x00,
        0x3F, 0x00, 0x00, 0x00,
        0x2A, 0x00, 0x00, 0x00,
        0x27, 0xD2, 0x00, 0x14,
    };
    Check(
        std::memcmp(
            encoded.data(),
            expectedInput.data(),
            encoded.size()) == 0,
        "movement input differs from the managed golden vector");

    SecureRealtimeMovementInput decoded{};
    Check(
        TryDecodeSecureRealtimeMovementInput(
            encoded.data(),
            encoded.size(),
            SecureRealtimeMovementSource::Udp,
            &decoded) &&
            decoded.transportEpoch ==
                canonical.transportEpoch &&
            decoded.inputId == canonical.inputId &&
            decoded.worldGeneration ==
                canonical.worldGeneration &&
            decoded.legacyState == canonical.legacyState &&
            decoded.x == canonical.x &&
            decoded.z == canonical.z &&
            decoded.auxiliary == canonical.auxiliary &&
            decoded.mapId == canonical.mapId,
        "movement input did not round trip");

    auto tls = canonical;
    tls.flags = static_cast<std::uint8_t>(
        SecureRealtimeMovementInputFlag::CurrentWorld);
    Check(
        TryEncodeSecureRealtimeMovementInput(
            tls,
            SecureRealtimeMovementSource::TlsFallback,
            encoded.data(),
            encoded.size()) &&
            !TryDecodeSecureRealtimeMovementInput(
                encoded.data(),
                encoded.size(),
                SecureRealtimeMovementSource::Udp,
                &decoded) &&
            TryDecodeSecureRealtimeMovementInput(
                encoded.data(),
                encoded.size(),
                SecureRealtimeMovementSource::TlsFallback,
                &decoded),
        "CurrentWorld escaped the TLS-only boundary");
    auto tlsWithoutCurrentWorld = canonical;
    Check(
        TryEncodeSecureRealtimeMovementInput(
            tlsWithoutCurrentWorld,
            SecureRealtimeMovementSource::TlsFallback,
            encoded.data(),
            encoded.size()),
        "managed-compatible TLS movement without CurrentWorld was rejected");
    auto zeroClientClock = canonical;
    zeroClientClock.clientMonotonicMilliseconds = 0;
    Check(
        !TryEncodeSecureRealtimeMovementInput(
            zeroClientClock,
            SecureRealtimeMovementSource::Udp,
            encoded.data(),
            encoded.size()),
        "zero client monotonic clock was accepted");

    for (std::size_t bytes = 0;
         bytes < SecureRealtimeMovementInputBytes;
         ++bytes) {
        Check(
            !TryDecodeSecureRealtimeMovementInput(
                encoded.data(),
                bytes,
                SecureRealtimeMovementSource::TlsFallback,
                &decoded),
            "truncated movement input was accepted");
    }

    const auto snapshot = CanonicalSnapshot();
    std::array<
        std::uint8_t,
        SecureRealtimePositionSnapshotBytes> snapshotBytes{};
    Check(
        TryEncodeSecureRealtimePositionSnapshot(
            snapshot,
            snapshotBytes.data(),
            snapshotBytes.size()) &&
            snapshotBytes[0] == 1 &&
            snapshotBytes[1] == 1 &&
            snapshotBytes[2] == 0 &&
            snapshotBytes[3] == 64 &&
            snapshotBytes[60] == 0x2A &&
            snapshotBytes[61] == 0 &&
            snapshotBytes[62] == 0 &&
            snapshotBytes[63] == 0,
        "position snapshot golden offsets changed");
    constexpr std::array<
        std::uint8_t,
        SecureRealtimePositionSnapshotBytes> expectedSnapshot{
        0x01, 0x01, 0x00, 0x40,
        0x01, 0x02, 0x03, 0x04,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x02, 0x03, 0x04,
        0x05, 0x06, 0x07, 0x08,
        0x11, 0x12, 0x13, 0x14,
        0x15, 0x16, 0x17, 0x18,
        0x21, 0x22, 0x23, 0x24,
        0x25, 0x26, 0x27, 0x28,
        0x31, 0x32, 0x33, 0x34,
        0x41, 0x42, 0x43, 0x44,
        0x3F, 0xC0, 0x00, 0x00,
        0xC0, 0x10, 0x00, 0x00,
        0x3F, 0x00, 0x00, 0x00,
        0x2A, 0x00, 0x00, 0x00,
    };
    Check(
        std::memcmp(
            snapshotBytes.data(),
            expectedSnapshot.data(),
            snapshotBytes.size()) == 0,
        "position snapshot differs from the managed golden vector");

    SecureRealtimePositionSnapshot decodedSnapshot{};
    Check(
        TryDecodeSecureRealtimePositionSnapshot(
            snapshotBytes.data(),
            snapshotBytes.size(),
            &decodedSnapshot) &&
            decodedSnapshot.snapshotSequence ==
                snapshot.snapshotSequence &&
            decodedSnapshot.worldGeneration ==
                snapshot.worldGeneration &&
            decodedSnapshot.x == snapshot.x,
        "position snapshot did not round trip");

    auto invalidSnapshot = snapshot;
    invalidSnapshot.rejection =
        SecureRealtimeMovementRejection::Speed;
    Check(
        !TryEncodeSecureRealtimePositionSnapshot(
            invalidSnapshot,
            snapshotBytes.data(),
            snapshotBytes.size()),
        "rejection without Correction was accepted");
    invalidSnapshot.flags |= static_cast<std::uint8_t>(
        SecureRealtimePositionSnapshotFlag::Correction);
    Check(
        TryEncodeSecureRealtimePositionSnapshot(
            invalidSnapshot,
            snapshotBytes.data(),
            snapshotBytes.size()),
        "known rejected correction was refused");
    auto zeroServerTick = snapshot;
    zeroServerTick.serverTick = 0;
    Check(
        !TryEncodeSecureRealtimePositionSnapshot(
            zeroServerTick,
            snapshotBytes.data(),
            snapshotBytes.size()),
        "zero server tick was accepted");

    auto nanLegacy = legacy;
    WriteLittle32(
        nanLegacy.data() + 8,
        FloatBits(
            (std::numeric_limits<float>::quiet_NaN)()));
    Check(
        !TryParseSecureRealtimeLegacyMovement(
            nanLegacy.data(),
            nanLegacy.size(),
            &parsed),
        "non-finite legacy movement was accepted");

    std::array<std::uint8_t, SecureFrameHeaderBytes>
        frameHeader{};
    Check(
        TryEncodeSecureFrameHeader(
            SecureFrameHeader{
                static_cast<std::uint32_t>(
                    SecureRealtimeMovementInputBytes),
                SecureFrameType::RealtimeMovementInput,
                1},
            SecureEndpointRole::Game,
            SecureFrameDirection::ClientToServer,
            frameHeader.data(),
            frameHeader.size()) &&
            !TryEncodeSecureFrameHeader(
                SecureFrameHeader{
                    static_cast<std::uint32_t>(
                        SecureRealtimeMovementInputBytes - 1),
                    SecureFrameType::RealtimeMovementInput,
                    1},
                SecureEndpointRole::Game,
                SecureFrameDirection::ClientToServer,
                frameHeader.data(),
                frameHeader.size()) &&
            !TryEncodeSecureFrameHeader(
                SecureFrameHeader{
                    static_cast<std::uint32_t>(
                        SecureRealtimeMovementInputBytes),
                    SecureFrameType::RealtimeMovementInput,
                    1},
                SecureEndpointRole::Login,
                SecureFrameDirection::ClientToServer,
                frameHeader.data(),
                frameHeader.size()) &&
            !TryEncodeSecureFrameHeader(
                SecureFrameHeader{
                    static_cast<std::uint32_t>(
                        SecureRealtimeMovementInputBytes),
                    SecureFrameType::RealtimeMovementInput,
                    1},
                SecureEndpointRole::Game,
                SecureFrameDirection::ServerToClient,
                frameHeader.data(),
                frameHeader.size()),
        "TLS movement frame escaped its exact game C2S boundary");
}

void CheckRouterGateMailboxAndFallback() {
    SecureRealtimeMovementRouter disabled;
    Check(
        disabled.Configure(false),
        "disabled router configuration failed");
    const auto packet = LegacyMovement();
    Check(
        disabled.RouteLegacyPacket(
            packet.data(),
            static_cast<int>(packet.size()),
            1'100) ==
            SecureRealtimeMovementRouteResult::PassThrough,
        "missing capability did not preserve raw legacy");

    SecureRealtimeMovementRouter router;
    Check(
        router.IsValid() && router.Configure(true),
        "authoritative router configuration failed");
    Check(
        router.RouteLegacyPacket(
            packet.data(),
            static_cast<int>(packet.size()),
            1'100) ==
            SecureRealtimeMovementRouteResult::PassThrough,
        "router cut over before an authenticated baseline");

    auto baseline = CanonicalSnapshot(1);
    baseline.transportEpoch = 1;
    baseline.serverTick = 1;
    baseline.revision = 1;
    baseline.worldGeneration = 9;
    baseline.mapId = 4;
    Check(
        router.AcceptAuthenticatedSnapshot(
            baseline,
            1'200),
        "authenticated keyframe baseline was rejected");

    auto first = LegacyMovement(10, 1.0F, 2.0F, 3.0F);
    auto second = LegacyMovement(11, 4.0F, 5.0F, 6.0F);
    Check(
        router.RouteLegacyPacket(
            first.data(),
            static_cast<int>(first.size()),
            1'210) ==
                SecureRealtimeMovementRouteResult::Accepted &&
            router.RouteLegacyPacket(
                second.data(),
                static_cast<int>(second.size()),
                1'220) ==
                SecureRealtimeMovementRouteResult::Accepted,
        "ready router did not suppress valid movement");

    SecureRealtimeMovementInput movement{};
    Check(
        router.TryTakePending(&movement) &&
            movement.inputId == 2 &&
            movement.transportEpoch == 1 &&
            movement.flags == 0 &&
            movement.worldGeneration == 9 &&
            movement.mapId == 4 &&
            movement.legacyState == 11 &&
            movement.x == 4.0F &&
            router.Snapshot().pendingReplacements == 1,
        "capacity-one mailbox did not retain the newest sample");
    Check(
        router.RecordUdpSent(movement, 1'230) &&
            !router.UdpAcknowledgmentTimedOut(2'229) &&
            router.UdpAcknowledgmentTimedOut(2'230),
        "one-second gameplay ACK deadline changed");

    SecureRealtimeMovementInput retry{};
    bool hasRetry = false;
    Check(
        router.SwitchToTls(&retry, &hasRetry) &&
            hasRetry &&
            retry.inputId == movement.inputId &&
            retry.transportEpoch == 2 &&
            retry.flags == static_cast<std::uint8_t>(
                SecureRealtimeMovementInputFlag::CurrentWorld) &&
            !router.UdpAcknowledgmentTimedOut(
                (std::numeric_limits<std::uint64_t>::max)()),
        "UDP-to-TLS switch did not preserve ID and advance epoch");
    SecureRealtimeMovementInput noSecondRetry{};
    bool hasSecondRetry = true;
    Check(
        router.SwitchToTls(
            &noSecondRetry,
            &hasSecondRetry) &&
            !hasSecondRetry &&
            router.Snapshot().transportEpoch == 2,
        "repeated TLS switch advanced the epoch twice");

    Check(
        router.RouteLegacyPacket(
            first.data(),
            static_cast<int>(first.size()),
            2'240) ==
                SecureRealtimeMovementRouteResult::Accepted &&
            router.TryTakePending(&movement) &&
            movement.inputId == 3 &&
            movement.transportEpoch == 2 &&
            movement.flags == static_cast<std::uint8_t>(
                SecureRealtimeMovementInputFlag::CurrentWorld),
        "new fallback input did not use secure TLS ownership");
    router.Stop();
    Check(
        router.RouteLegacyPacket(
            first.data(),
            static_cast<int>(first.size()),
            2'250) ==
            SecureRealtimeMovementRouteResult::Rejected,
        "stopped cutover router silently downgraded to raw legacy");
}

} // namespace

int RunSecureRealtimeMovementTests() {
    Failures = 0;
    CheckProtocolGoldenAndBounds();
    CheckRouterGateMailboxAndFallback();
    return Failures;
}
