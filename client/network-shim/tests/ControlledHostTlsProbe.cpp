#include "ControlledHostTlsProbe.h"

#include "../src/ExternalTcpConnector.h"
#include "../src/SchannelClientStream.h"
#include "../src/SecureOuterStream.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cwchar>

namespace {

constexpr wchar_t ConnectHost[] = L"localhost";
constexpr wchar_t TlsHost[] = L"login.reborn.test";
constexpr std::uint16_t TlsPort = 6599;
constexpr DWORD ConnectDeadlineMilliseconds = 5'000;

int HexNibble(wchar_t value) noexcept {
    if (value >= L'0' && value <= L'9') {
        return value - L'0';
    }
    if (value >= L'A' && value <= L'F') {
        return value - L'A' + 10;
    }
    if (value >= L'a' && value <= L'f') {
        return value - L'a' + 10;
    }
    return -1;
}

bool DecodeSha256(
    const wchar_t* source,
    std::uint8_t* destination,
    std::size_t destinationBytes) noexcept {
    if (source == nullptr ||
        destination == nullptr ||
        destinationBytes != 32 ||
        std::wcslen(source) != destinationBytes * 2) {
        return false;
    }

    for (std::size_t index = 0; index < destinationBytes; ++index) {
        const auto high = HexNibble(source[index * 2]);
        const auto low = HexNibble(source[index * 2 + 1]);
        if (high < 0 || low < 0) {
            return false;
        }
        destination[index] = static_cast<std::uint8_t>(
            (high << 4) | low);
    }
    return true;
}

} // namespace

int RunControlledHostTlsProbe(const wchar_t* originSha256) noexcept {
    using namespace godswar::network;

    std::uint8_t origin[32]{};
    if (!DecodeSha256(originSha256, origin, sizeof(origin))) {
        std::fprintf(
            stderr,
            "FAIL controlled-host probe invalid Origin SHA-256.\n");
        return 2;
    }

    SocketHandle socket;
    ExternalTcpConnectSnapshot tcp{};
    if (!ConnectExternalTcp(
            ConnectHost,
            TlsPort,
            ConnectDeadlineMilliseconds,
            &socket,
            &tcp)) {
        std::fprintf(
            stderr,
            "FAIL controlled-host TCP failure=%u native=%d.\n",
            static_cast<unsigned>(tcp.failure),
            tcp.nativeError);
        SecureZeroMemory(origin, sizeof(origin));
        return 1;
    }

    SchannelClientStream tls(
        static_cast<SocketHandle&&>(socket));
    if (!tls.Establish(
            TlsHost,
            SchannelRevocationPolicy::
                PinnedRootForDevelopment)) {
        const auto snapshot = tls.Snapshot();
        std::fprintf(
            stderr,
            "FAIL controlled-host TLS failure=%u status=0x%08lX "
            "protocol=%lu cipher=%lu.\n",
            static_cast<unsigned>(snapshot.failure),
            static_cast<unsigned long>(snapshot.securityStatus),
            snapshot.negotiatedProtocol,
            snapshot.negotiatedCipherSuite);
        SecureZeroMemory(origin, sizeof(origin));
        return 1;
    }

    std::uint8_t instance[16]{};
    for (std::size_t index = 0; index < sizeof(instance); ++index) {
        instance[index] = static_cast<std::uint8_t>(index + 1);
    }
    SecureOuterStream outer(&tls);
    if (!outer.Establish(
            SecureEndpointRole::Login,
            instance,
            sizeof(instance),
            origin,
            sizeof(origin))) {
        const auto outerSnapshot = outer.Snapshot();
        const auto tlsSnapshot = tls.Snapshot();
        std::fprintf(
            stderr,
            "FAIL controlled-host preface failure=%u tls=%u.\n",
            static_cast<unsigned>(outerSnapshot.failure),
            static_cast<unsigned>(tlsSnapshot.failure));
        SecureZeroMemory(instance, sizeof(instance));
        SecureZeroMemory(origin, sizeof(origin));
        return 1;
    }

    const auto snapshot = tls.Snapshot();
    std::printf(
        "PASS controlled-host TLS and preface protocol=%lu cipher=%lu.\n",
        snapshot.negotiatedProtocol,
        snapshot.negotiatedCipherSuite);
    SecureZeroMemory(instance, sizeof(instance));
    SecureZeroMemory(origin, sizeof(origin));
    return 0;
}
