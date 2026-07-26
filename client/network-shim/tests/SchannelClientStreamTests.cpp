#define SECURITY_WIN32

#include "SchannelClientStreamTests.h"

#include "../src/SchannelClientStream.h"
#include "../src/SchannelClientStreamPostHandshake.h"
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
using godswar::network::GetSchannelCredentialFlags;
using godswar::network::HasRequiredSchannelStreamAttributes;
using godswar::network::SchannelAlpnOfferBytes;
using godswar::network::SchannelClientStream;
using godswar::network::SchannelClientFailure;
using godswar::network::SchannelRevocationPolicy;
using godswar::network::SocketHandle;
using godswar::network::TryBuildSchannelAlpnOffer;
using godswar::network::EnsureWinSock;
namespace schannel_detail =
    godswar::network::schannel_detail;

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

    Check(
        HasRequiredSchannelStreamAttributes(
            ISC_RET_CONFIDENTIALITY |
            ISC_RET_STREAM) &&
            HasRequiredSchannelStreamAttributes(
                ISC_RET_CONFIDENTIALITY |
                ISC_RET_INTEGRITY |
                ISC_RET_STREAM) &&
            !HasRequiredSchannelStreamAttributes(
                ISC_RET_CONFIDENTIALITY) &&
            !HasRequiredSchannelStreamAttributes(
                ISC_RET_STREAM) &&
            !HasRequiredSchannelStreamAttributes(0),
        "required Schannel stream attributes changed");

    const DWORD strictFlags = GetSchannelCredentialFlags(
        SchannelRevocationPolicy::Strict);
    const DWORD developmentFlags = GetSchannelCredentialFlags(
        SchannelRevocationPolicy::
            AllowMissingSourceForDevelopment);
    Check(
        (strictFlags &
            SCH_CRED_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT) != 0 &&
            (strictFlags &
                SCH_CRED_IGNORE_NO_REVOCATION_CHECK) == 0 &&
            (developmentFlags &
                SCH_CRED_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT) != 0 &&
            (developmentFlags &
                SCH_CRED_IGNORE_NO_REVOCATION_CHECK) != 0 &&
            GetSchannelCredentialFlags(
                static_cast<SchannelRevocationPolicy>(0xFF)) == 0,
        "Schannel revocation policy flags are not environment-scoped");
}

void CheckPostHandshakePolicy() {
    Check(
        schannel_detail::IsTls13PostHandshakeRequest(
            SP_PROT_TLS1_3_CLIENT,
            SEC_I_RENEGOTIATE) &&
            !schannel_detail::IsTls13PostHandshakeRequest(
                SP_PROT_TLS1_2_CLIENT,
                SEC_I_RENEGOTIATE) &&
            !schannel_detail::IsTls13PostHandshakeRequest(
                SP_PROT_TLS1_3_CLIENT,
                SEC_E_OK),
        "legacy or non-continuation Schannel status was accepted");
    Check(
        schannel_detail::
            AreTls13PostHandshakeParametersUnchanged(
                SP_PROT_TLS1_3_CLIENT,
                0x1302,
                SP_PROT_TLS1_3_CLIENT,
                0x1302) &&
            !schannel_detail::
                AreTls13PostHandshakeParametersUnchanged(
                    SP_PROT_TLS1_2_CLIENT,
                    0xC030,
                    SP_PROT_TLS1_2_CLIENT,
                    0xC030) &&
            !schannel_detail::
                AreTls13PostHandshakeParametersUnchanged(
                    SP_PROT_TLS1_3_CLIENT,
                    0x1302,
                    SP_PROT_TLS1_3_CLIENT,
                    0x1301),
        "post-handshake TLS parameters could change");

    SecPkgContext_ApplicationProtocol expectedAlpn{};
    expectedAlpn.ProtoNegoStatus =
        SecApplicationProtocolNegotiationStatus_Success;
    expectedAlpn.ProtoNegoExt =
        SecApplicationProtocolNegotiationExt_ALPN;
    expectedAlpn.ProtocolIdSize =
        static_cast<unsigned char>(
            sizeof(schannel_detail::RequiredAlpn));
    std::memcpy(
        expectedAlpn.ProtocolId,
        schannel_detail::RequiredAlpn,
        sizeof(schannel_detail::RequiredAlpn));
    SecPkgContext_ApplicationProtocol clearedAlpn{};
    SecPkgContext_ApplicationProtocol wrongAlpn = expectedAlpn;
    wrongAlpn.ProtocolId[0] = 'x';
    Check(
        schannel_detail::IsAcceptedTls13PostHandshakeAlpn(
            true,
            SEC_E_OK,
            expectedAlpn) &&
            schannel_detail::IsAcceptedTls13PostHandshakeAlpn(
                true,
                SEC_E_OK,
                clearedAlpn) &&
            !schannel_detail::IsAcceptedTls13PostHandshakeAlpn(
                false,
                SEC_E_OK,
                clearedAlpn) &&
            !schannel_detail::IsAcceptedTls13PostHandshakeAlpn(
                true,
                SEC_E_INTERNAL_ERROR,
                clearedAlpn) &&
            !schannel_detail::IsAcceptedTls13PostHandshakeAlpn(
                true,
                SEC_E_OK,
                wrongAlpn),
        "post-handshake ALPN continuity policy changed");

    std::uint8_t encrypted[32]{};
    for (std::size_t index = 0; index < sizeof(encrypted); ++index) {
        encrypted[index] = static_cast<std::uint8_t>(index + 1);
    }
    const std::uint8_t expected[] = {
        9, 10, 11, 12, 13, 14, 15, 16,
    };
    SecBuffer buffers[4]{};
    buffers[0].BufferType = SECBUFFER_DATA;
    buffers[0].pvBuffer = encrypted;
    buffers[0].cbBuffer =
        static_cast<unsigned long>(sizeof(encrypted));
    buffers[1].BufferType = SECBUFFER_EXTRA;
    buffers[1].pvBuffer = encrypted + 8;
    buffers[1].cbBuffer =
        static_cast<unsigned long>(sizeof(expected));
    bool found = false;
    std::size_t retained = 0;
    Check(
        schannel_detail::TryRetainSchannelExtraBuffer(
            buffers,
            4,
            encrypted,
            sizeof(encrypted),
            sizeof(encrypted),
            &found,
            &retained) &&
            found &&
            retained == sizeof(expected) &&
            std::memcmp(
                encrypted,
                expected,
                sizeof(expected)) == 0,
        "valid Schannel post-handshake EXTRA was not retained");

    std::uint8_t noExtraInput[] = {1, 3, 5, 7};
    SecBuffer noExtraBuffers[2]{};
    noExtraBuffers[0].BufferType = SECBUFFER_DATA;
    noExtraBuffers[0].pvBuffer = noExtraInput;
    noExtraBuffers[0].cbBuffer =
        static_cast<unsigned long>(sizeof(noExtraInput));
    found = true;
    retained = 99;
    Check(
        schannel_detail::TryRetainSchannelExtraBuffer(
            noExtraBuffers,
            2,
            noExtraInput,
            sizeof(noExtraInput),
            sizeof(noExtraInput),
            &found,
            &retained) &&
            !found &&
            retained == 0 &&
            noExtraInput[0] == 1 &&
            noExtraInput[3] == 7,
        "valid no-EXTRA Schannel continuation was rejected");

    buffers[2] = buffers[1];
    Check(
        !schannel_detail::TryRetainSchannelExtraBuffer(
            buffers,
            4,
            encrypted,
            sizeof(encrypted),
            sizeof(encrypted),
            &found,
            &retained),
        "multiple Schannel EXTRA buffers were accepted");
    buffers[2].BufferType = SECBUFFER_EMPTY;
    buffers[1].pvBuffer = encrypted + sizeof(encrypted);
    buffers[1].cbBuffer = 1;
    Check(
        !schannel_detail::TryRetainSchannelExtraBuffer(
            buffers,
            4,
            encrypted,
            sizeof(encrypted),
            sizeof(encrypted),
            &found,
            &retained),
        "out-of-range Schannel EXTRA was accepted");
}

void CheckInvalidSocket() {
    SchannelClientStream stream(SocketHandle{});
    Check(!stream.IsValid(), "invalid socket stream was valid");
    Check(
        !stream.Establish(
            L"login.reborn.test",
            SchannelRevocationPolicy::Strict,
            1),
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
        !stream.Establish(
            L"login.reborn.test",
            SchannelRevocationPolicy::Strict,
            100),
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
    CheckPostHandshakePolicy();
    if (includeSocketChecks) {
        CheckInvalidSocket();
        CheckSilentPeerDeadline();
    }
    return Failures;
}
