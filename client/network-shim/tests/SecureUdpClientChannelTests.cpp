#include "SecureUdpClientChannelTests.h"

#include "../src/SecureClientRuntimeInternal.h"
#include "../src/SecureUdpClientChannel.h"
#include "../src/SecureUdpClientWorker.h"

#include <Windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::SecureUdpBindingDatagramBytes;
using godswar::network::SecureUdpBindingGrant;
using godswar::network::SecureUdpClientChannel;
using godswar::network::SecureUdpClientChannelState;
using godswar::network::SecureUdpClientWorker;
using godswar::network::SecureUdpClientWorkerState;
using godswar::network::SecureUdpDirection;
using godswar::network::SecureUdpProtectedHeader;
using godswar::network::SecureUdpProtectedMaximumBytes;
using godswar::network::SecureUdpProtectedMessageType;
using godswar::network::SecureUdpReplayWindow;
using godswar::network::TryDecodeSecureUdpBindingGrant;
using godswar::network::TryOpenSecureUdpProtectedDatagram;
using godswar::network::TrySealSecureUdpProtectedDatagram;

constexpr std::uint32_t ServerId = 0x01020304;
constexpr std::uint16_t UdpPort = 7444;
constexpr std::uint64_t TestUnixMilliseconds = 100'000;
constexpr std::uint64_t TestMonotonicMilliseconds = 1'000;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void WriteUInt16(std::uint8_t* output, std::uint16_t value) {
    output[0] = static_cast<std::uint8_t>(value >> 8U);
    output[1] = static_cast<std::uint8_t>(value);
}

void WriteUInt32(std::uint8_t* output, std::uint32_t value) {
    output[0] = static_cast<std::uint8_t>(value >> 24U);
    output[1] = static_cast<std::uint8_t>(value >> 16U);
    output[2] = static_cast<std::uint8_t>(value >> 8U);
    output[3] = static_cast<std::uint8_t>(value);
}

void WriteUInt64(std::uint8_t* output, std::uint64_t value) {
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
        value[index] = static_cast<std::uint8_t>(index);
    }
    return value;
}

std::array<std::uint8_t, 16> Nonce(std::uint8_t base = 0xA0) {
    std::array<std::uint8_t, 16> value{};
    for (std::size_t index = 0; index < value.size(); ++index) {
        value[index] = static_cast<std::uint8_t>(base + index);
    }
    return value;
}

SecureUdpBindingGrant Grant(
    std::uint64_t expiry = TestUnixMilliseconds + 60'000,
    std::uint16_t port = UdpPort) {
    std::array<std::uint8_t, 72> bytes{};
    const auto connection = ConnectionId();
    const auto key = ProofKey();
    std::memcpy(bytes.data(), "GWUG", 4);
    WriteUInt16(bytes.data() + 4, 1);
    WriteUInt16(bytes.data() + 8, port);
    WriteUInt32(bytes.data() + 12, ServerId);
    WriteUInt64(bytes.data() + 16, expiry);
    std::memcpy(bytes.data() + 24, connection.data(), connection.size());
    std::memcpy(bytes.data() + 40, key.data(), key.size());

    SecureUdpBindingGrant grant;
    Check(
        TryDecodeSecureUdpBindingGrant(
            bytes.data(),
            bytes.size(),
            &grant),
        "channel test grant decodes");
    return grant;
}

std::array<std::uint8_t, 128> Challenge(
    const std::array<std::uint8_t, 128>& hello) {
    auto challenge = hello;
    challenge[8] = 2;
    WriteUInt32(challenge.data() + 28, 0x11223344);
    WriteUInt64(challenge.data() + 64, 123456);
    for (std::size_t index = 0; index < 32; ++index) {
        challenge[96 + index] =
            static_cast<std::uint8_t>(0x40 + index);
    }
    return challenge;
}

bool BindThroughProof(
    SecureUdpClientChannel* channel,
    std::array<std::uint8_t, 16> nonce) {
    std::array<std::uint8_t, 128> hello{};
    std::size_t helloBytes = 0;
    if (!channel->TryBuildBindingHello(
            hello.data(),
            hello.size(),
            &helloBytes) ||
        helloBytes != hello.size() ||
        std::memcmp(
            hello.data() + 48,
            nonce.data(),
            nonce.size()) != 0) {
        return false;
    }
    const auto challenge = Challenge(hello);
    std::array<std::uint8_t, 128> proof{};
    std::size_t proofBytes = 0;
    return channel->TryHandleBindingChallenge(
            challenge.data(),
            challenge.size(),
            proof.data(),
            proof.size(),
            &proofBytes) &&
        proofBytes == proof.size() &&
        proof[8] == 4;
}

bool CreateServerDatagram(
    SecureUdpProtectedMessageType type,
    const std::uint8_t* payload,
    std::size_t payloadBytes,
    std::uint32_t epoch,
    std::uint64_t sequence,
    std::array<std::uint8_t, SecureUdpProtectedMaximumBytes>*
        datagram,
    std::size_t* datagramBytes) {
    const auto connection = ConnectionId();
    const auto key = ProofKey();
    SecureUdpProtectedHeader header{};
    header.keyEpoch = epoch;
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

bool Confirm(
    SecureUdpClientChannel* channel,
    const std::array<std::uint8_t, 16>& nonce,
    std::uint64_t revision,
    std::uint32_t epoch,
    std::uint64_t sequence,
    std::uint64_t now) {
    std::array<std::uint8_t, 32> payload{};
    std::memcpy(payload.data(), nonce.data(), nonce.size());
    WriteUInt64(payload.data() + 16, revision);
    WriteUInt64(payload.data() + 24, 1'700'000'000'000);
    std::array<std::uint8_t, SecureUdpProtectedMaximumBytes>
        datagram{};
    std::size_t datagramBytes = 0;
    return CreateServerDatagram(
            SecureUdpProtectedMessageType::BindingConfirm,
            payload.data(),
            payload.size(),
            epoch,
            sequence,
            &datagram,
            &datagramBytes) &&
        channel->TryHandleProtectedDatagram(
            datagram.data(),
            datagramBytes,
            now);
}

bool BuildPingPlaintext(
    SecureUdpClientChannel* channel,
    std::uint64_t pingId,
    std::uint64_t now,
    std::array<std::uint8_t, 16>* plaintext) {
    std::array<std::uint8_t, SecureUdpProtectedMaximumBytes>
        datagram{};
    std::size_t datagramBytes = 0;
    if (!channel->TryBuildPing(
            pingId,
            now,
            datagram.data(),
            datagram.size(),
            &datagramBytes)) {
        return false;
    }

    const auto connection = ConnectionId();
    const auto key = ProofKey();
    SecureUdpProtectedHeader header{};
    std::size_t plaintextBytes = 0;
    return TryOpenSecureUdpProtectedDatagram(
            key.data(),
            key.size(),
            connection.data(),
            connection.size(),
            ServerId,
            SecureUdpDirection::ClientToServer,
            datagram.data(),
            datagramBytes,
            &header,
            plaintext->data(),
            plaintext->size(),
            &plaintextBytes) &&
        plaintextBytes == plaintext->size();
}

bool Pong(
    SecureUdpClientChannel* channel,
    const std::array<std::uint8_t, 16>& ping,
    std::uint32_t epoch,
    std::uint64_t sequence,
    std::uint64_t now) {
    std::array<std::uint8_t, 32> payload{};
    std::memcpy(payload.data(), ping.data(), ping.size());
    WriteUInt64(payload.data() + 16, 1'700'000'000'100);
    WriteUInt64(payload.data() + 24, 1'700'000'000'101);
    std::array<std::uint8_t, SecureUdpProtectedMaximumBytes>
        datagram{};
    std::size_t datagramBytes = 0;
    return CreateServerDatagram(
            SecureUdpProtectedMessageType::Pong,
            payload.data(),
            payload.size(),
            epoch,
            sequence,
            &datagram,
            &datagramBytes) &&
        channel->TryHandleProtectedDatagram(
            datagram.data(),
            datagramBytes,
            now);
}

void CheckReplayWindow() {
    SecureUdpReplayWindow window;
    Check(
        window.CommitAuthenticated(100),
        "replay first sequence accepts");
    Check(
        window.CommitAuthenticated(98) &&
            window.CommitAuthenticated(99),
        "reordered sequences accept");
    Check(
        !window.CouldAccept(99) &&
            !window.CommitAuthenticated(99),
        "duplicate sequence rejects");
    Check(
        window.HighestSequence() == 100 &&
            (window.AcknowledgmentMask() & 0x3) == 0x3,
        "reordered packets produce bounded ACK mask");
    Check(
        window.CouldAccept(0),
        "unseen packet inside 128-sequence window remains eligible");
    Check(
        window.CommitAuthenticated(228) &&
            !window.CouldAccept(100),
        "128-packet window evicts stale sequence");
}

void CheckChannelLifecycleAndNetworkEffects() {
    SecureUdpClientChannel channel;
    auto grant = Grant();
    const auto nonce = Nonce();
    Check(
        channel.Initialize(
            &grant,
            nonce.data(),
            nonce.size(),
            TestUnixMilliseconds,
            TestMonotonicMilliseconds),
        "channel initializes from TLS grant");
    Check(
        !grant.IsValid() &&
            BindThroughProof(&channel, nonce) &&
            Confirm(&channel, nonce, 1, 1, 0, 1'100),
        "binding proof receives protected confirmation");
    Check(
        channel.Snapshot().state ==
                SecureUdpClientChannelState::Active &&
            channel.Snapshot().bindingRevision == 1,
        "channel becomes active only after confirmation");
    const auto authenticatedBeforeWrongSemantic =
        channel.Snapshot().authenticatedPackets;
    Check(
        !Confirm(
            &channel,
            Nonce(0xE0),
            2,
            1,
            1,
            1'200) &&
            channel.Snapshot().authenticatedPackets ==
                authenticatedBeforeWrongSemantic,
        "valid AEAD with wrong state semantics commits no replay state");

    std::array<std::uint8_t, 16> pingOne{};
    Check(
        BuildPingPlaintext(&channel, 1, 6'100, &pingOne) &&
            Pong(&channel, pingOne, 1, 1, 6'220),
        "keepalive Pong handles emulated latency");
    const auto afterFirst = channel.Snapshot();
    Check(
        afterFirst.lastRoundTripMilliseconds == 120,
        "first emulated RTT records");
    Check(
        !Pong(&channel, pingOne, 1, 1, 6'230) &&
            channel.Snapshot().replayedPackets == 1,
        "duplicated Pong rejects through replay window");

    std::array<std::uint8_t, 16> pingTwo{};
    Check(
        BuildPingPlaintext(&channel, 2, 11'100, &pingTwo) &&
            Pong(&channel, pingTwo, 1, 2, 11'400),
        "second keepalive handles higher jitter");
    Check(
        channel.Snapshot().jitterMilliseconds == 45,
        "RTT jitter uses bounded rolling estimate");

    std::array<std::uint8_t, 16> lostPing{};
    std::array<std::uint8_t, 16> replacementPing{};
    Check(
        BuildPingPlaintext(&channel, 3, 16'100, &lostPing) &&
            !channel.KeepaliveDue(21'100) &&
            channel.KeepaliveDue(26'100),
        "pending keepalive applies congestion-aware backoff");
    Check(
            BuildPingPlaintext(
                &channel,
                4,
                21'100,
                &replacementPing) &&
            channel.Snapshot().lostPings == 1,
        "burst loss does not grow a pending queue");
    Check(
        Pong(&channel, replacementPing, 2, 0, 21'200) &&
            channel.Snapshot().receiveEpoch == 2,
        "authenticated exact-next epoch promotes");

    std::array<std::uint8_t, 16> oldEpochPing{};
    Check(
        BuildPingPlaintext(
            &channel,
            5,
            26'100,
            &oldEpochPing) &&
            Pong(&channel, oldEpochPing, 1, 3, 26'200),
        "reordered previous-epoch packet accepts during overlap");
    std::array<std::uint8_t, 16> stalePing{};
    Check(
        BuildPingPlaintext(
            &channel,
            6,
            31'100,
            &stalePing) &&
            !Pong(&channel, stalePing, 1, 4, 31'201),
        "previous epoch rejects after bounded overlap");

    const auto sameEndpointNonce = Nonce(0xC0);
    Check(
        channel.BeginRebind(
            sameEndpointNonce.data(),
            sameEndpointNonce.size()) &&
            BindThroughProof(&channel, sameEndpointNonce) &&
            Confirm(
                &channel,
                sameEndpointNonce,
                1,
                2,
                1,
                31'300),
        "fresh proof permits idempotent same-revision bind");
    const auto changedEndpointNonce = Nonce(0xD0);
    Check(
        channel.BeginRebind(
            changedEndpointNonce.data(),
            changedEndpointNonce.size()) &&
            BindThroughProof(&channel, changedEndpointNonce) &&
            Confirm(
                &channel,
                changedEndpointNonce,
                2,
                2,
                2,
                31'400) &&
            channel.Snapshot().bindingRevision == 2,
        "fresh endpoint proof advances binding revision once");
}

void CheckEpochOverlapTimestampSaturation() {
    SecureUdpClientChannel channel;
    auto grant = Grant();
    const auto nonce = Nonce();
    Check(
        channel.Initialize(
            &grant,
            nonce.data(),
            nonce.size(),
            TestUnixMilliseconds,
            1) &&
            BindThroughProof(&channel, nonce) &&
            Confirm(&channel, nonce, 1, 1, 0, 100),
        "timestamp saturation channel binds");

    const auto maximum = UINT64_MAX;
    std::array<std::uint8_t, 16> nextEpochPing{};
    Check(
        BuildPingPlaintext(
            &channel,
            10,
            maximum - 1'000,
            &nextEpochPing) &&
            Pong(
                &channel,
                nextEpochPing,
                2,
                0,
                maximum - 900),
        "next receive epoch promotes near monotonic maximum");

    std::array<std::uint8_t, 16> overlapPing{};
    Check(
        BuildPingPlaintext(
            &channel,
            11,
            maximum - 800,
            &overlapPing) &&
            Pong(
                &channel,
                overlapPing,
                1,
                1,
                maximum - 1),
        "saturated previous-epoch overlap does not wrap early");

    std::array<std::uint8_t, 16> expiredPing{};
    Check(
        BuildPingPlaintext(
            &channel,
            12,
            maximum - 700,
            &expiredPing) &&
            !Pong(
                &channel,
                expiredPing,
                1,
                2,
                maximum),
        "previous epoch expires at saturated overlap deadline");
}

void CheckExpiryRetryAndCancellableWorker() {
    SecureUdpClientChannel expired;
    auto expiredGrant = Grant(TestUnixMilliseconds);
    const auto nonce = Nonce();
    Check(
        !expired.Initialize(
            &expiredGrant,
            nonce.data(),
            nonce.size(),
            TestUnixMilliseconds,
            TestMonotonicMilliseconds),
        "expired TLS UDP offer rejects");
    Check(
        SecureUdpClientWorker::BindingRetryDelayMilliseconds(1) ==
                250 &&
            SecureUdpClientWorker::BindingRetryDelayMilliseconds(2) ==
                500 &&
            SecureUdpClientWorker::BindingRetryDelayMilliseconds(3) ==
                1'000 &&
            SecureUdpClientWorker::BindingRetryDelayMilliseconds(9) ==
                1'000,
        "binding retry uses capped exponential schedule");

    std::uint64_t nowUnix = 0;
    Check(
        godswar::network::ReadSystemUnixMilliseconds(&nowUnix),
        "worker test reads system clock");
    auto workerGrant = Grant(nowUnix + 60'000, 9);
    sockaddr_in peer{};
    peer.sin_family = AF_INET;
    peer.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    peer.sin_port = htons(443);
    SecureUdpClientWorker worker;
    Check(
        worker.Start(
            &workerGrant,
            reinterpret_cast<const sockaddr*>(&peer),
            sizeof(peer)),
        "bounded loopback-only worker starts");
    Check(
        worker.StopAndJoin(2'000),
        "nonblocking UDP worker cancels and joins");
    const auto snapshot = worker.Snapshot();
    Check(
        snapshot.state == SecureUdpClientWorkerState::Stopped ||
            snapshot.state ==
                SecureUdpClientWorkerState::TlsFallback,
        "worker cancellation reaches a terminal UDP-only state");
}

} // namespace

int RunSecureUdpClientChannelTests() {
    Failures = 0;
    CheckReplayWindow();
    CheckChannelLifecycleAndNetworkEffects();
    CheckEpochOverlapTimestampSaturation();
    CheckExpiryRetryAndCancellableWorker();
    return Failures;
}
