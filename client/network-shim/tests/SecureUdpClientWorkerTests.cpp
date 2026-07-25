#include "SecureUdpClientWorkerTests.h"

#include "../src/SecureClientRuntimeInternal.h"
#include "../src/SecureClientSession.h"
#include "../src/SecureUdpBindingProtocol.h"
#include "../src/SecureUdpClientWorker.h"
#include "../src/WinSockRuntime.h"

#include <WinSock2.h>
#include <Windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::SecureUdpBindingGrant;
using godswar::network::SecureUdpBindingPacket;
using godswar::network::SecureUdpBindingPacketType;
using godswar::network::SecureUdpClientWorker;
using godswar::network::SecureUdpClientWorkerState;
using godswar::network::SecureClientSession;
using godswar::network::SecureUdpDirection;
using godswar::network::SecureUdpProtectedHeader;
using godswar::network::SecureUdpProtectedMaximumBytes;
using godswar::network::SecureUdpProtectedMessageType;
using godswar::network::NativeBridgeFailure;
using godswar::network::NativeBridgeState;
using godswar::network::NativeClientBridgeSnapshot;
using godswar::network::TryDecodeSecureUdpBindingGrant;
using godswar::network::TryDecodeSecureUdpBindingPacket;
using godswar::network::TrySealSecureUdpProtectedDatagram;

constexpr std::uint32_t ServerId = 0x01020304;

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

SecureUdpBindingGrant Grant(
    std::uint16_t port,
    std::uint64_t expiry) {
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
        "loopback worker grant decodes");
    return grant;
}

struct LoopbackServerContext final {
    SOCKET socketValue = INVALID_SOCKET;
    volatile LONG success = 0;
};

bool ReceiveFrom(
    SOCKET socketValue,
    std::uint8_t* destination,
    int destinationBytes,
    sockaddr_storage* remote,
    int* remoteBytes,
    int expectedBytes) {
    *remoteBytes = sizeof(*remote);
    return recvfrom(
               socketValue,
               reinterpret_cast<char*>(destination),
               destinationBytes,
               0,
               reinterpret_cast<sockaddr*>(remote),
               remoteBytes) == expectedBytes;
}

DWORD WINAPI RunLoopbackServer(void* value) {
    auto* context = static_cast<LoopbackServerContext*>(value);
    if (context == nullptr ||
        context->socketValue == INVALID_SOCKET) {
        return ERROR_INVALID_PARAMETER;
    }

    std::array<std::uint8_t, 128> hello{};
    sockaddr_storage remote{};
    int remoteBytes = 0;
    SecureUdpBindingPacket decodedHello{};
    if (!ReceiveFrom(
            context->socketValue,
            hello.data(),
            static_cast<int>(hello.size()),
            &remote,
            &remoteBytes,
            static_cast<int>(hello.size())) ||
        !TryDecodeSecureUdpBindingPacket(
            hello.data(),
            hello.size(),
            &decodedHello) ||
        decodedHello.type !=
            SecureUdpBindingPacketType::ClientHello) {
        return ERROR_INVALID_DATA;
    }

    auto challenge = hello;
    challenge[8] = static_cast<std::uint8_t>(
        SecureUdpBindingPacketType::ServerChallenge);
    WriteUInt32(challenge.data() + 28, 1);
    WriteUInt64(challenge.data() + 64, 123456);
    for (std::size_t index = 0; index < 32; ++index) {
        challenge[96 + index] =
            static_cast<std::uint8_t>(0x40 + index);
    }
    if (sendto(
            context->socketValue,
            reinterpret_cast<const char*>(challenge.data()),
            static_cast<int>(challenge.size()),
            0,
            reinterpret_cast<const sockaddr*>(&remote),
            remoteBytes) != static_cast<int>(challenge.size())) {
        return ERROR_WRITE_FAULT;
    }

    std::array<std::uint8_t, 128> proof{};
    sockaddr_storage proofRemote{};
    int proofRemoteBytes = 0;
    SecureUdpBindingPacket decodedProof{};
    if (!ReceiveFrom(
            context->socketValue,
            proof.data(),
            static_cast<int>(proof.size()),
            &proofRemote,
            &proofRemoteBytes,
            static_cast<int>(proof.size())) ||
        !TryDecodeSecureUdpBindingPacket(
            proof.data(),
            proof.size(),
            &decodedProof) ||
        decodedProof.type !=
            SecureUdpBindingPacketType::
                AuthenticatedClientProof) {
        return ERROR_INVALID_DATA;
    }

    std::array<std::uint8_t, 32> payload{};
    std::memcpy(
        payload.data(),
        decodedHello.clientNonce,
        sizeof(decodedHello.clientNonce));
    WriteUInt64(payload.data() + 16, 1);
    WriteUInt64(payload.data() + 24, 1'700'000'000'000);
    SecureUdpProtectedHeader header{};
    header.keyEpoch = 1;
    header.messageType =
        SecureUdpProtectedMessageType::BindingConfirm;
    header.payloadBytes =
        static_cast<std::uint16_t>(payload.size());
    const auto connection = ConnectionId();
    const auto key = ProofKey();
    std::array<std::uint8_t, SecureUdpProtectedMaximumBytes>
        confirmation{};
    std::size_t confirmationBytes = 0;
    if (!TrySealSecureUdpProtectedDatagram(
            key.data(),
            key.size(),
            connection.data(),
            connection.size(),
            ServerId,
            SecureUdpDirection::ServerToClient,
            header,
            payload.data(),
            payload.size(),
            confirmation.data(),
            confirmation.size(),
            &confirmationBytes) ||
        sendto(
            context->socketValue,
            reinterpret_cast<const char*>(
                confirmation.data()),
            static_cast<int>(confirmationBytes),
            0,
            reinterpret_cast<const sockaddr*>(&proofRemote),
            proofRemoteBytes) !=
            static_cast<int>(confirmationBytes)) {
        return ERROR_WRITE_FAULT;
    }

    std::array<
        std::uint8_t,
        SecureUdpProtectedMaximumBytes + 2> oversized{};
    if (sendto(
            context->socketValue,
            reinterpret_cast<const char*>(oversized.data()),
            static_cast<int>(oversized.size()),
            0,
            reinterpret_cast<const sockaddr*>(&proofRemote),
            proofRemoteBytes) !=
        static_cast<int>(oversized.size())) {
        return ERROR_WRITE_FAULT;
    }

    InterlockedExchange(&context->success, 1);
    return ERROR_SUCCESS;
}

void CheckLoopbackBindingAndCancellation() {
    Check(
        godswar::network::EnsureWinSock(),
        "loopback worker initializes WinSock");
    SOCKET listener = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    Check(
        listener != INVALID_SOCKET,
        "loopback UDP fixture opens");
    if (listener == INVALID_SOCKET) {
        return;
    }

    DWORD receiveTimeout = 2'000;
    setsockopt(
        listener,
        SOL_SOCKET,
        SO_RCVTIMEO,
        reinterpret_cast<const char*>(&receiveTimeout),
        sizeof(receiveTimeout));
    sockaddr_in local{};
    local.sin_family = AF_INET;
    local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    Check(
        bind(
            listener,
            reinterpret_cast<const sockaddr*>(&local),
            sizeof(local)) == 0,
        "loopback UDP fixture binds");
    int localBytes = sizeof(local);
    Check(
        getsockname(
            listener,
            reinterpret_cast<sockaddr*>(&local),
            &localBytes) == 0,
        "loopback UDP fixture reports port");

    LoopbackServerContext context{};
    context.socketValue = listener;
    HANDLE serverThread = CreateThread(
        nullptr,
        0,
        RunLoopbackServer,
        &context,
        0,
        nullptr);
    Check(
        serverThread != nullptr,
        "loopback UDP server thread starts");
    if (serverThread == nullptr) {
        closesocket(listener);
        return;
    }

    std::uint64_t nowUnix = 0;
    Check(
        godswar::network::ReadSystemUnixMilliseconds(&nowUnix),
        "loopback worker reads clock");
    auto grant = Grant(ntohs(local.sin_port), nowUnix + 60'000);
    sockaddr_in tlsPeer{};
    tlsPeer.sin_family = AF_INET;
    tlsPeer.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    tlsPeer.sin_port = htons(443);
    SecureUdpClientWorker worker;
    Check(
        worker.Start(
            &grant,
            reinterpret_cast<const sockaddr*>(&tlsPeer),
            sizeof(tlsPeer)),
        "loopback worker starts from TLS peer");

    const auto deadline = GetTickCount64() + 3'000;
    while (GetTickCount64() < deadline &&
        worker.Snapshot().state !=
            SecureUdpClientWorkerState::Active) {
        Sleep(10);
    }
    Check(
        worker.Snapshot().state ==
            SecureUdpClientWorkerState::Active &&
            InterlockedCompareExchange(
                &context.success,
                0,
                0) == 1,
        "worker completes Hello/challenge/proof/AEAD confirm");
    const auto oversizedDeadline = GetTickCount64() + 2'000;
    while (GetTickCount64() < oversizedDeadline &&
        worker.Snapshot().oversizedDatagramsDropped == 0) {
        Sleep(10);
    }
    const auto afterOversized = worker.Snapshot();
    Check(
        afterOversized.state ==
                SecureUdpClientWorkerState::Active &&
            afterOversized.channel.state ==
                godswar::network::SecureUdpClientChannelState::Active &&
            afterOversized.oversizedDatagramsDropped == 1,
        "oversized datagram changed authenticated UDP state");
    NativeClientBridgeSnapshot healthyTls{};
    healthyTls.state = NativeBridgeState::Running;
    healthyTls.failure = NativeBridgeFailure::None;
    Check(
        SecureClientSession::ShouldContinueTlsBridge(
            healthyTls,
            &afterOversized),
        "oversized UDP datagram affected healthy TLS fallback");
    Check(
        worker.StopAndJoin(2'000),
        "active loopback worker cancels cleanly");
    Check(
        WaitForSingleObject(serverThread, 2'000) ==
            WAIT_OBJECT_0,
        "loopback server exits within bound");

    CloseHandle(serverThread);
    closesocket(listener);
}

} // namespace

int RunSecureUdpClientWorkerTests() {
    Failures = 0;
    CheckLoopbackBindingAndCancellation();
    return Failures;
}
