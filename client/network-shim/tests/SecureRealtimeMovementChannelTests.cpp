#include "SecureRealtimeMovementChannelTests.h"

#include "../src/SecureRealtimeMovementProtocol.h"
#include "../src/SecureUdpClientChannel.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using namespace godswar::network;

constexpr std::uint32_t ServerId = 0x01020304;
constexpr std::uint64_t UnixNow = 100'000;
constexpr std::uint64_t MonotonicNow = 1'000;
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
        output[7 - index] = static_cast<std::uint8_t>(value);
        value >>= 8U;
    }
}

std::array<std::uint8_t, 16> ConnectionId() {
    std::array<std::uint8_t, 16> value{};
    for (std::size_t index = 0; index < value.size(); ++index) {
        value[index] = static_cast<std::uint8_t>(0x10 + index);
    }
    return value;
}

std::array<std::uint8_t, 32> ProofKey() {
    std::array<std::uint8_t, 32> value{};
    for (std::size_t index = 0; index < value.size(); ++index) {
        value[index] = static_cast<std::uint8_t>(index + 1);
    }
    return value;
}

std::array<std::uint8_t, 16> Nonce() {
    std::array<std::uint8_t, 16> value{};
    for (std::size_t index = 0; index < value.size(); ++index) {
        value[index] = static_cast<std::uint8_t>(0xA0 + index);
    }
    return value;
}

SecureUdpBindingGrant ChannelGrant() {
    std::array<std::uint8_t, SecureUdpBindingGrantBytes> bytes{};
    const auto connection = ConnectionId();
    const auto key = ProofKey();
    std::memcpy(bytes.data(), "GWUG", 4);
    Write16(bytes.data() + 4, 1);
    Write16(bytes.data() + 8, 7444);
    Write16(
        bytes.data() + 10,
        static_cast<std::uint16_t>(
            SecureUdpBindingCapability::
                AuthoritativeMovement));
    Write32(bytes.data() + 12, ServerId);
    Write64(bytes.data() + 16, UnixNow + 60'000);
    std::memcpy(
        bytes.data() + 24,
        connection.data(),
        connection.size());
    std::memcpy(
        bytes.data() + 40,
        key.data(),
        key.size());
    SecureUdpBindingGrant grant;
    Check(
        TryDecodeSecureUdpBindingGrant(
            bytes.data(),
            bytes.size(),
            &grant) &&
            grant.HasCapability(
                SecureUdpBindingCapability::
                    AuthoritativeMovement),
        "authoritative channel grant did not decode");
    return grant;
}

bool SealServerDatagram(
    SecureUdpProtectedMessageType type,
    const void* payload,
    std::size_t payloadBytes,
    std::uint64_t sequence,
    std::array<
        std::uint8_t,
        SecureUdpProtectedMaximumBytes>* datagram,
    std::size_t* datagramBytes) {
    const auto connection = ConnectionId();
    const auto key = ProofKey();
    SecureUdpProtectedHeader header{};
    header.keyEpoch = 1;
    header.sequence = sequence;
    header.messageType = type;
    header.payloadBytes =
        static_cast<std::uint16_t>(payloadBytes);
    return TrySealSecureUdpProtectedDatagram(
        key.data(),
        key.size(),
        connection.data(),
        connection.size(),
        ServerId,
        SecureUdpDirection::ServerToClient,
        header,
        payload,
        payloadBytes,
        datagram->data(),
        datagram->size(),
        datagramBytes);
}

bool PrepareChannel(
    SecureUdpClientChannel* channel,
    const std::array<std::uint8_t, 16>& nonce) {
    auto grant = ChannelGrant();
    if (!channel->Initialize(
            &grant,
            nonce.data(),
            nonce.size(),
            UnixNow,
            MonotonicNow)) {
        return false;
    }
    std::array<std::uint8_t, 128> hello{};
    std::size_t helloBytes = 0;
    if (!channel->TryBuildBindingHello(
            hello.data(),
            hello.size(),
            &helloBytes)) {
        return false;
    }
    auto challenge = hello;
    challenge[8] = 2;
    Write32(challenge.data() + 28, 0x11223344);
    Write64(challenge.data() + 64, 123456);
    for (std::size_t index = 0; index < 32; ++index) {
        challenge[96 + index] =
            static_cast<std::uint8_t>(0x40 + index);
    }
    std::array<std::uint8_t, 128> proof{};
    std::size_t proofBytes = 0;
    if (!channel->TryHandleBindingChallenge(
            challenge.data(),
            challenge.size(),
            proof.data(),
            proof.size(),
            &proofBytes)) {
        return false;
    }

    return proofBytes == proof.size();
}

bool ConfirmChannel(
    SecureUdpClientChannel* channel,
    const std::array<std::uint8_t, 16>& nonce,
    std::uint64_t protectedSequence,
    std::uint64_t now) {
    std::array<std::uint8_t, 32> confirmation{};
    std::memcpy(
        confirmation.data(),
        nonce.data(),
        nonce.size());
    Write64(confirmation.data() + 16, 1);
    Write64(
        confirmation.data() + 24,
        1'700'000'000'000ULL);
    std::array<
        std::uint8_t,
        SecureUdpProtectedMaximumBytes> datagram{};
    std::size_t datagramBytes = 0;
    return SealServerDatagram(
            SecureUdpProtectedMessageType::BindingConfirm,
            confirmation.data(),
            confirmation.size(),
            protectedSequence,
            &datagram,
            &datagramBytes) &&
        channel->TryHandleProtectedDatagram(
            datagram.data(),
            datagramBytes,
            now);
}

bool ActivateChannel(
    SecureUdpClientChannel* channel,
    const std::array<std::uint8_t, 16>& nonce) {
    return PrepareChannel(channel, nonce) &&
        ConfirmChannel(
            channel,
            nonce,
            1,
            MonotonicNow + 10);
}

SecureRealtimeMovementInput MovementInput() {
    SecureRealtimeMovementInput input{};
    input.transportEpoch = 7;
    input.inputId = 9;
    input.clientMonotonicMilliseconds = 1'020;
    input.worldGeneration = 4;
    input.legacyState = 0x31323334;
    input.x = 1.5F;
    input.z = -2.25F;
    input.auxiliary = 0.5F;
    input.mapId = 3;
    return input;
}

SecureRealtimePositionSnapshot PositionSnapshot() {
    SecureRealtimePositionSnapshot snapshot{};
    snapshot.flags = static_cast<std::uint8_t>(
        SecureRealtimePositionSnapshotFlag::Keyframe);
    snapshot.transportEpoch = 7;
    snapshot.serverTick = 1;
    snapshot.revision = 1;
    snapshot.snapshotSequence = 1;
    snapshot.worldGeneration = 4;
    snapshot.legacyState = 0x31323334;
    snapshot.x = 1.5F;
    snapshot.z = -2.25F;
    snapshot.auxiliary = 0.5F;
    snapshot.mapId = 3;
    return snapshot;
}

void CheckProtectedChannelMovement() {
    SecureUdpClientChannel channel;
    const auto nonce = Nonce();
    Check(
        ActivateChannel(&channel, nonce),
        "movement channel did not activate");

    const auto input = MovementInput();
    std::array<
        std::uint8_t,
        SecureUdpProtectedMaximumBytes> datagram{};
    std::size_t datagramBytes = 0;
    Check(
        channel.TryBuildMovementInput(
            input,
            MonotonicNow + 20,
            datagram.data(),
            datagram.size(),
            &datagramBytes),
        "active channel did not build MovementInput");

    const auto connection = ConnectionId();
    const auto key = ProofKey();
    std::array<
        std::uint8_t,
        SecureRealtimeMovementInputBytes> plaintext{};
    SecureUdpProtectedHeader opened{};
    std::size_t plaintextBytes = 0;
    SecureRealtimeMovementInput decoded{};
    Check(
        TryOpenSecureUdpProtectedDatagram(
            key.data(),
            key.size(),
            connection.data(),
            connection.size(),
            ServerId,
            SecureUdpDirection::ClientToServer,
            datagram.data(),
            datagramBytes,
            &opened,
            plaintext.data(),
            plaintext.size(),
            &plaintextBytes) &&
            opened.messageType ==
                SecureUdpProtectedMessageType::MovementInput &&
            TryDecodeSecureRealtimeMovementInput(
                plaintext.data(),
                plaintextBytes,
                SecureRealtimeMovementSource::Udp,
                &decoded) &&
            decoded.inputId == input.inputId,
        "protected MovementInput did not open exactly");

    const auto snapshot = PositionSnapshot();
    std::array<
        std::uint8_t,
        SecureRealtimePositionSnapshotBytes> snapshotBytes{};
    Check(
        TryEncodeSecureRealtimePositionSnapshot(
            snapshot,
            snapshotBytes.data(),
            snapshotBytes.size()) &&
            SealServerDatagram(
                SecureUdpProtectedMessageType::PositionSnapshot,
                snapshotBytes.data(),
                snapshotBytes.size(),
                2,
                &datagram,
                &datagramBytes) &&
            channel.TryHandleProtectedDatagram(
                datagram.data(),
                datagramBytes,
                MonotonicNow + 30),
        "authenticated PositionSnapshot was rejected");
    SecureRealtimePositionSnapshot received{};
    Check(
        channel.TryTakePositionSnapshot(&received) &&
            received.snapshotSequence == 1 &&
            !channel.TryTakePositionSnapshot(&received),
        "position snapshot mailbox was not consume-once");

    Check(
        SealServerDatagram(
            SecureUdpProtectedMessageType::PositionSnapshot,
            snapshotBytes.data(),
            snapshotBytes.size(),
            3,
            &datagram,
            &datagramBytes) &&
            !channel.TryHandleProtectedDatagram(
                datagram.data(),
                datagramBytes,
                MonotonicNow + 40),
        "stale snapshot sequence was accepted");
}

void CheckSnapshotBeforeConfirmation() {
    SecureUdpClientChannel channel;
    const auto nonce = Nonce();
    Check(
        PrepareChannel(&channel, nonce),
        "reordered baseline channel preparation failed");

    const auto snapshot = PositionSnapshot();
    std::array<
        std::uint8_t,
        SecureRealtimePositionSnapshotBytes> snapshotBytes{};
    std::array<
        std::uint8_t,
        SecureUdpProtectedMaximumBytes> datagram{};
    std::size_t datagramBytes = 0;
    SecureRealtimePositionSnapshot received{};
    Check(
        TryEncodeSecureRealtimePositionSnapshot(
            snapshot,
            snapshotBytes.data(),
            snapshotBytes.size()) &&
            SealServerDatagram(
                SecureUdpProtectedMessageType::PositionSnapshot,
                snapshotBytes.data(),
                snapshotBytes.size(),
                2,
                &datagram,
                &datagramBytes) &&
            channel.TryHandleProtectedDatagram(
                datagram.data(),
                datagramBytes,
                MonotonicNow + 5) &&
            !channel.TryTakePositionSnapshot(&received),
        "pre-confirmation keyframe was not retained privately");
    Check(
        ConfirmChannel(
            &channel,
            nonce,
            1,
            MonotonicNow + 10) &&
            channel.TryTakePositionSnapshot(&received) &&
            received.snapshotSequence == 1,
        "retained keyframe was not exposed after confirmation");
}

} // namespace

int RunSecureRealtimeMovementChannelTests() {
    Failures = 0;
    CheckProtectedChannelMovement();
    CheckSnapshotBeforeConfirmation();
    return Failures;
}
