#define SECURITY_WIN32

#include "SchannelClientStreamTests.h"

#include "../src/SchannelClientStream.h"
#include "../src/WinSockRuntime.h"

#include <security.h>
#include <schannel.h>
#include <WS2tcpip.h>

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::IsAcceptedSchannelProtocolAndCipher;
using godswar::network::IsValidSchannelTargetName;
using godswar::network::SchannelAlpnOfferBytes;
using godswar::network::SchannelClientStream;
using godswar::network::SchannelClientFailure;
using godswar::network::SocketHandle;
using godswar::network::TryBuildSchannelAlpnOffer;
using godswar::network::EnsureWinSock;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void CheckTargetNames() {
    Check(
        IsValidSchannelTargetName(L"login.reborn.test"),
        "valid Schannel DNS target was rejected");
    Check(
        !IsValidSchannelTargetName(nullptr) &&
            !IsValidSchannelTargetName(L"") &&
            !IsValidSchannelTargetName(L"-bad.test") &&
            !IsValidSchannelTargetName(L"bad-.test") &&
            !IsValidSchannelTargetName(L"bad..test") &&
            !IsValidSchannelTargetName(L"UPPER.test") &&
            !IsValidSchannelTargetName(L"127.0.0.1"),
        "invalid Schannel target was accepted");

    wchar_t overlong[255]{};
    for (std::size_t index = 0; index < 254; ++index) {
        overlong[index] = L'a';
    }
    Check(
        !IsValidSchannelTargetName(overlong),
        "overlong Schannel target was accepted");
}

void CheckAlpn() {
    alignas(4) std::uint8_t encoded[SchannelAlpnOfferBytes]{};
    Check(
        TryBuildSchannelAlpnOffer(encoded, sizeof(encoded)),
        "ALPN offer encoding failed");

    ULONG listsSize = 0;
    SEC_APPLICATION_PROTOCOL_NEGOTIATION_EXT extension =
        SecApplicationProtocolNegotiationExt_None;
    unsigned short listSize = 0;
    std::memcpy(&listsSize, encoded, sizeof(listsSize));
    std::memcpy(&extension, encoded + 4, sizeof(extension));
    std::memcpy(&listSize, encoded + 8, sizeof(listSize));
    Check(
        listsSize == 21 &&
            extension ==
                SecApplicationProtocolNegotiationExt_ALPN &&
            listSize == 15 &&
            encoded[10] == 14 &&
            std::memcmp(
                encoded + 11,
                "godswar-shim/1",
                14) == 0,
        "ALPN offer layout changed");
}

void CheckPolicy() {
    Check(
        IsAcceptedSchannelProtocolAndCipher(
            SP_PROT_TLS1_2_CLIENT,
            0xC02F) &&
            IsAcceptedSchannelProtocolAndCipher(
                SP_PROT_TLS1_2_CLIENT,
                0xC030) &&
            IsAcceptedSchannelProtocolAndCipher(
                SP_PROT_TLS1_3_CLIENT,
                0x1301) &&
            IsAcceptedSchannelProtocolAndCipher(
                SP_PROT_TLS1_3_CLIENT,
                0x1302),
        "required TLS suites were rejected");
    Check(
        !IsAcceptedSchannelProtocolAndCipher(
            SP_PROT_TLS1_1_CLIENT,
            0xC02F) &&
            !IsAcceptedSchannelProtocolAndCipher(
                SP_PROT_TLS1_2_CLIENT,
                0xC02B) &&
            !IsAcceptedSchannelProtocolAndCipher(
                SP_PROT_TLS1_3_CLIENT,
                0x1303),
        "disallowed TLS policy was accepted");
}

void CheckInvalidSocket() {
    SchannelClientStream stream(SocketHandle{});
    Check(!stream.IsValid(), "invalid socket stream was valid");
    Check(
        !stream.Establish(L"login.reborn.test", 1),
        "invalid socket established Schannel");
}

void CheckSilentPeerDeadline() {
    if (!EnsureWinSock()) {
        Check(false, "WinSock initialization failed");
        return;
    }

    SocketHandle listener(socket(
        AF_INET,
        SOCK_STREAM,
        IPPROTO_TCP));
    if (!listener.IsValid()) {
        Check(false, "Schannel deadline listener was not created");
        return;
    }
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    if (bind(
            listener.Get(),
            reinterpret_cast<const sockaddr*>(&address),
            sizeof(address)) != 0 ||
        listen(listener.Get(), 1) != 0) {
        Check(false, "Schannel deadline listener did not bind");
        return;
    }
    int addressBytes = sizeof(address);
    if (getsockname(
            listener.Get(),
            reinterpret_cast<sockaddr*>(&address),
            &addressBytes) != 0) {
        Check(false, "Schannel deadline port was unavailable");
        return;
    }

    SocketHandle client(socket(
        AF_INET,
        SOCK_STREAM,
        IPPROTO_TCP));
    if (!client.IsValid() ||
        connect(
            client.Get(),
            reinterpret_cast<const sockaddr*>(&address),
            sizeof(address)) != 0) {
        Check(false, "Schannel deadline client did not connect");
        return;
    }
    SocketHandle silentPeer(accept(
        listener.Get(),
        nullptr,
        nullptr));
    if (!silentPeer.IsValid()) {
        Check(false, "Schannel deadline peer was not accepted");
        return;
    }

    SchannelClientStream stream(
        static_cast<SocketHandle&&>(client));
    Check(stream.IsValid(), "connected Schannel stream was invalid");
    Check(
        !stream.Establish(L"login.reborn.test", 100),
        "silent peer bypassed the Schannel handshake deadline");
    const auto snapshot = stream.Snapshot();
    Check(
        snapshot.stopped &&
            snapshot.failure ==
                SchannelClientFailure::HandshakeDeadline,
        "silent Schannel peer did not produce a finite deadline failure");
}

} // namespace

int RunSchannelClientStreamTests(bool includeSocketChecks) {
    Failures = 0;
    CheckTargetNames();
    CheckAlpn();
    CheckPolicy();
    if (includeSocketChecks) {
        CheckInvalidSocket();
        CheckSilentPeerDeadline();
    }
    return Failures;
}
