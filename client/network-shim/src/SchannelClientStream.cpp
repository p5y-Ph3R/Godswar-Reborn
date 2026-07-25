#define SECURITY_WIN32
#define SCHANNEL_USE_BLACKLISTS

#include "SchannelClientStream.h"
#include "SchannelClientStreamInternal.h"

#include "WinSockRuntime.h"

#include <winternl.h>
#include <security.h>
#include <schannel.h>
#include <wincrypt.h>
#include <ws2tcpip.h>

#include <algorithm>
#include <cstring>
#include <new>

namespace godswar::network {
namespace {

constexpr std::uint8_t RequiredAlpn[] = {
    'g', 'o', 'd', 's', 'w', 'a', 'r',
    '-', 's', 'h', 'i', 'm', '/', '1',
};
constexpr DWORD TlsEcdheRsaAes128GcmSha256 = 0xC02F;
constexpr DWORD TlsEcdheRsaAes256GcmSha384 = 0xC030;
constexpr DWORD TlsAes128GcmSha256 = 0x1301;
constexpr DWORD TlsAes256GcmSha384 = 0x1302;
constexpr std::size_t PlaintextCapacity = 16 * 1024;
constexpr std::size_t MaximumHandshakeTokenBytes = 64 * 1024;
constexpr unsigned MaximumHandshakeSteps = 64;

bool IsAllAsciiDnsName(const wchar_t* value) noexcept {
    if (value == nullptr) {
        return false;
    }

    std::size_t length = 0;
    std::size_t labelLength = 0;
    while (length <= 253 && value[length] != L'\0') {
        const wchar_t character = value[length];
        if (character == L'.') {
            if (labelLength == 0 ||
                value[length - 1] == L'-') {
                return false;
            }
            labelLength = 0;
        } else {
            const bool isLower =
                character >= L'a' && character <= L'z';
            const bool isDigit =
                character >= L'0' && character <= L'9';
            if ((!isLower && !isDigit && character != L'-') ||
                (labelLength == 0 && character == L'-')) {
                return false;
            }
            ++labelLength;
            if (labelLength > 63) {
                return false;
            }
        }
        ++length;
    }

    IN_ADDR ipv4{};
    return InetPtonW(AF_INET, value, &ipv4) != 1 &&
        length >= 1 &&
        length <= 253 &&
        value[length] == L'\0' &&
        labelLength >= 1 &&
        value[length - 1] != L'-';
}

DWORD RemainingMilliseconds(ULONGLONG deadline) noexcept {
    const ULONGLONG now = GetTickCount64();
    if (deadline <= now) {
        return 0;
    }

    const ULONGLONG remaining = deadline - now;
    return remaining >= static_cast<ULONGLONG>(INFINITE)
        ? INFINITE - 1
        : static_cast<DWORD>(remaining);
}

bool IsSuccessStatus(SECURITY_STATUS status) noexcept {
    return status == SEC_E_OK ||
        status == SEC_I_CONTINUE_NEEDED ||
        status == SEC_I_COMPLETE_NEEDED ||
        status == SEC_I_COMPLETE_AND_CONTINUE;
}

bool RequiresContinue(SECURITY_STATUS status) noexcept {
    return status == SEC_I_CONTINUE_NEEDED ||
        status == SEC_I_COMPLETE_AND_CONTINUE;
}

bool RequiresComplete(SECURITY_STATUS status) noexcept {
    return status == SEC_I_COMPLETE_NEEDED ||
        status == SEC_I_COMPLETE_AND_CONTINUE;
}

bool IsAcceptedCertificate(PCCERT_CONTEXT certificate) noexcept {
    if (certificate == nullptr ||
        certificate->pCertInfo == nullptr) {
        return false;
    }

    const auto& publicKey =
        certificate->pCertInfo->SubjectPublicKeyInfo;
    const char* publicKeyOid =
        publicKey.Algorithm.pszObjId;
    const char* signatureOid =
        certificate->pCertInfo->SignatureAlgorithm.pszObjId;
    if (publicKeyOid == nullptr ||
        signatureOid == nullptr ||
        std::strcmp(publicKeyOid, szOID_RSA_RSA) != 0) {
        return false;
    }

    const bool acceptedSignature =
        std::strcmp(signatureOid, szOID_RSA_SHA256RSA) == 0;
    if (!acceptedSignature) {
        return false;
    }

    const DWORD bits = CertGetPublicKeyLength(
        X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
        const_cast<PCERT_PUBLIC_KEY_INFO>(&publicKey));
    return bits >= 2048;
}

} // namespace

bool IsValidSchannelTargetName(const wchar_t* targetName) noexcept {
    return IsAllAsciiDnsName(targetName);
}

bool TryBuildSchannelAlpnOffer(
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (destination == nullptr ||
        destinationBytes < SchannelAlpnOfferBytes) {
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    std::memset(output, 0, SchannelAlpnOfferBytes);
    const ULONG protocolListsSize = 6U +
        static_cast<ULONG>(1 + sizeof(RequiredAlpn));
    const auto extension =
        SecApplicationProtocolNegotiationExt_ALPN;
    const unsigned short protocolListSize =
        static_cast<unsigned short>(1 + sizeof(RequiredAlpn));
    std::memcpy(output, &protocolListsSize, sizeof(protocolListsSize));
    std::memcpy(output + 4, &extension, sizeof(extension));
    std::memcpy(
        output + 8,
        &protocolListSize,
        sizeof(protocolListSize));
    output[10] = static_cast<std::uint8_t>(sizeof(RequiredAlpn));
    std::memcpy(output + 11, RequiredAlpn, sizeof(RequiredAlpn));
    return true;
}

bool IsAcceptedSchannelProtocolAndCipher(
    DWORD protocol,
    DWORD cipherSuite) noexcept {
    if (protocol == SP_PROT_TLS1_2_CLIENT) {
        return cipherSuite == TlsEcdheRsaAes128GcmSha256 ||
            cipherSuite == TlsEcdheRsaAes256GcmSha384;
    }
    if (protocol == SP_PROT_TLS1_3_CLIENT) {
        return cipherSuite == TlsAes128GcmSha256 ||
            cipherSuite == TlsAes256GcmSha384;
    }
    return false;
}

SchannelClientStream::SchannelClientStream(
    SocketHandle&& socket) noexcept
    : state_(new (std::nothrow) State(
          static_cast<SocketHandle&&>(socket))) {
}

SchannelClientStream::~SchannelClientStream() noexcept {
    Stop();
    delete state_;
    state_ = nullptr;
}

bool SchannelClientStream::IsValid() const noexcept {
    return state_ != nullptr && state_->configured;
}

bool SchannelClientStream::Establish(
    const wchar_t* targetName,
    DWORD timeoutMilliseconds) noexcept {
    if (!IsValid() ||
        !IsValidSchannelTargetName(targetName) ||
        timeoutMilliseconds == 0 ||
        timeoutMilliseconds == INFINITE ||
        state_->IsEstablished() ||
        state_->IsStopped()) {
        if (state_ != nullptr) {
            state_->Fail(SchannelClientFailure::InvalidArgument);
        }
        return false;
    }

    const ULONGLONG deadline =
        GetTickCount64() + timeoutMilliseconds;
    const std::size_t targetLength = std::wcslen(targetName);
    std::memcpy(
        state_->targetName,
        targetName,
        (targetLength + 1) * sizeof(wchar_t));

    TLS_PARAMETERS tlsParameters{};
    tlsParameters.grbitDisabledProtocols =
        SP_PROT_PCT1_CLIENT |
        SP_PROT_SSL2_CLIENT |
        SP_PROT_SSL3_CLIENT |
        SP_PROT_TLS1_0_CLIENT |
        SP_PROT_TLS1_1_CLIENT |
        SP_PROT_DTLS1_X_CLIENT;

    SCH_CREDENTIALS credentials{};
    credentials.dwVersion = SCH_CREDENTIALS_VERSION;
    credentials.dwFlags =
        SCH_CRED_NO_DEFAULT_CREDS |
        SCH_CRED_AUTO_CRED_VALIDATION |
        SCH_CRED_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT |
        SCH_USE_STRONG_CRYPTO;
    credentials.cTlsParameters = 1;
    credentials.pTlsParameters = &tlsParameters;

    TimeStamp credentialExpiry{};
    SECURITY_STATUS status = AcquireCredentialsHandleW(
        nullptr,
        const_cast<wchar_t*>(UNISP_NAME_W),
        SECPKG_CRED_OUTBOUND,
        nullptr,
        &credentials,
        nullptr,
        nullptr,
        &state_->credentials,
        &credentialExpiry);
    if (status != SEC_E_OK) {
        state_->Fail(SchannelClientFailure::CredentialAcquisition);
        return false;
    }

    alignas(4) std::uint8_t alpn[SchannelAlpnOfferBytes]{};
    if (!TryBuildSchannelAlpnOffer(alpn, sizeof(alpn))) {
        state_->Fail(SchannelClientFailure::HandshakeProtocol);
        return false;
    }

    SecBuffer initialInputBuffer{};
    initialInputBuffer.BufferType = SECBUFFER_APPLICATION_PROTOCOLS;
    initialInputBuffer.cbBuffer =
        static_cast<unsigned long>(sizeof(alpn));
    initialInputBuffer.pvBuffer = alpn;
    SecBufferDesc initialInput{};
    initialInput.ulVersion = SECBUFFER_VERSION;
    initialInput.cBuffers = 1;
    initialInput.pBuffers = &initialInputBuffer;

    constexpr ULONG RequestedAttributes =
        ISC_REQ_REPLAY_DETECT |
        ISC_REQ_SEQUENCE_DETECT |
        ISC_REQ_CONFIDENTIALITY |
        ISC_REQ_INTEGRITY |
        ISC_REQ_ALLOCATE_MEMORY |
        ISC_REQ_STREAM;
    ULONG returnedAttributes = 0;
    TimeStamp contextExpiry{};
    bool firstCall = true;
    unsigned handshakeSteps = 0;

    while (true) {
        ++handshakeSteps;
        if (handshakeSteps > MaximumHandshakeSteps) {
            state_->Fail(SchannelClientFailure::HandshakeProtocol);
            return false;
        }
        if (RemainingMilliseconds(deadline) == 0) {
            state_->Fail(SchannelClientFailure::HandshakeDeadline);
            return false;
        }

        SecBuffer inputBuffers[2]{};
        SecBufferDesc input{};
        SecBufferDesc* inputPointer = &initialInput;
        if (!firstCall) {
            inputBuffers[0].BufferType = SECBUFFER_TOKEN;
            inputBuffers[0].cbBuffer = static_cast<unsigned long>(
                state_->encryptedInputBytes);
            inputBuffers[0].pvBuffer = state_->encryptedInput;
            inputBuffers[1].BufferType = SECBUFFER_EMPTY;
            input.ulVersion = SECBUFFER_VERSION;
            input.cBuffers = 2;
            input.pBuffers = inputBuffers;
            inputPointer = &input;
        }

        SecBuffer outputBuffers[2]{};
        outputBuffers[0].BufferType = SECBUFFER_TOKEN;
        outputBuffers[1].BufferType = SECBUFFER_ALERT;
        SecBufferDesc output{};
        output.ulVersion = SECBUFFER_VERSION;
        output.cBuffers = 2;
        output.pBuffers = outputBuffers;

        status = InitializeSecurityContextW(
            &state_->credentials,
            firstCall ? nullptr : &state_->context,
            state_->targetName,
            RequestedAttributes,
            0,
            0,
            inputPointer,
            0,
            &state_->context,
            &output,
            &returnedAttributes,
            &contextExpiry);

        if (RequiresComplete(status)) {
            const SECURITY_STATUS completeStatus =
                CompleteAuthToken(&state_->context, &output);
            if (completeStatus != SEC_E_OK) {
                for (auto& outputBuffer : outputBuffers) {
                    if (outputBuffer.pvBuffer != nullptr) {
                        FreeContextBuffer(outputBuffer.pvBuffer);
                        outputBuffer.pvBuffer = nullptr;
                    }
                }
                state_->Fail(SchannelClientFailure::HandshakeProtocol);
                return false;
            }
        }

        bool outputWritten = true;
        std::size_t outputBytes = 0;
        for (auto& outputBuffer : outputBuffers) {
            if (outputBuffer.pvBuffer == nullptr) {
                continue;
            }
            const std::size_t bufferBytes = outputBuffer.cbBuffer;
            const bool bounded =
                bufferBytes > 0 &&
                bufferBytes <=
                    MaximumHandshakeTokenBytes - outputBytes;
            if (outputWritten && bounded) {
                outputWritten = state_->RawWriteAll(
                    outputBuffer.pvBuffer,
                    bufferBytes,
                    deadline);
                outputBytes += bufferBytes;
            } else {
                outputWritten = false;
            }
            FreeContextBuffer(outputBuffer.pvBuffer);
            outputBuffer.pvBuffer = nullptr;
        }
        if (!outputWritten) {
            state_->Fail(
                RemainingMilliseconds(deadline) == 0
                    ? SchannelClientFailure::HandshakeDeadline
                    : SchannelClientFailure::HandshakeWrite);
            return false;
        }

        if (status == SEC_E_INCOMPLETE_MESSAGE) {
            firstCall = false;
            if (state_->encryptedInputBytes ==
                sizeof(state_->encryptedInput)) {
                state_->Fail(SchannelClientFailure::HandshakeProtocol);
                return false;
            }

            const DeadlineStreamResult read = state_->RawRead(
                state_->encryptedInput + state_->encryptedInputBytes,
                sizeof(state_->encryptedInput) -
                    state_->encryptedInputBytes,
                deadline);
            if (read.status != DeadlineStreamStatus::Success ||
                read.bytesTransferred == 0) {
                state_->Fail(
                    read.status == DeadlineStreamStatus::TimedOut
                        ? SchannelClientFailure::HandshakeDeadline
                        : SchannelClientFailure::HandshakeRead);
                return false;
            }
            state_->encryptedInputBytes += read.bytesTransferred;
            continue;
        }

        std::size_t extraBytes = 0;
        if (!firstCall &&
            inputBuffers[1].BufferType == SECBUFFER_EXTRA) {
            extraBytes = inputBuffers[1].cbBuffer;
            if (extraBytes > state_->encryptedInputBytes) {
                state_->Fail(SchannelClientFailure::HandshakeProtocol);
                return false;
            }
        }

        if (!IsSuccessStatus(status)) {
            state_->Fail(SchannelClientFailure::HandshakeProtocol);
            return false;
        }

        if (status == SEC_E_OK ||
            status == SEC_I_COMPLETE_NEEDED) {
            if (extraBytes > 0) {
                std::memmove(
                    state_->encryptedInput,
                    state_->encryptedInput +
                        state_->encryptedInputBytes - extraBytes,
                    extraBytes);
            }
            state_->encryptedInputBytes = extraBytes;
            break;
        }

        if (extraBytes > 0) {
            std::memmove(
                state_->encryptedInput,
                state_->encryptedInput +
                    state_->encryptedInputBytes - extraBytes,
                extraBytes);
        }
        state_->encryptedInputBytes = extraBytes;
        firstCall = false;

        if (extraBytes > 0) {
            continue;
        }

        if (RequiresContinue(status)) {
            if (state_->encryptedInputBytes ==
                sizeof(state_->encryptedInput)) {
                state_->Fail(SchannelClientFailure::HandshakeProtocol);
                return false;
            }

            const DeadlineStreamResult read = state_->RawRead(
                state_->encryptedInput + state_->encryptedInputBytes,
                sizeof(state_->encryptedInput) -
                    state_->encryptedInputBytes,
                deadline);
            if (read.status != DeadlineStreamStatus::Success ||
                read.bytesTransferred == 0) {
                state_->Fail(
                    read.status == DeadlineStreamStatus::TimedOut
                        ? SchannelClientFailure::HandshakeDeadline
                        : SchannelClientFailure::HandshakeRead);
                return false;
            }
            state_->encryptedInputBytes += read.bytesTransferred;
        }
    }

    constexpr ULONG RequiredAttributes =
        ISC_RET_CONFIDENTIALITY |
        ISC_RET_INTEGRITY |
        ISC_RET_STREAM;
    if ((returnedAttributes & RequiredAttributes) !=
        RequiredAttributes) {
        state_->Fail(SchannelClientFailure::ContextAttributes);
        return false;
    }

    SecPkgContext_ApplicationProtocol negotiatedAlpn{};
    if (QueryContextAttributesW(
            &state_->context,
            SECPKG_ATTR_APPLICATION_PROTOCOL,
            &negotiatedAlpn) != SEC_E_OK ||
        negotiatedAlpn.ProtoNegoStatus !=
            SecApplicationProtocolNegotiationStatus_Success ||
        negotiatedAlpn.ProtoNegoExt !=
            SecApplicationProtocolNegotiationExt_ALPN ||
        negotiatedAlpn.ProtocolIdSize != sizeof(RequiredAlpn) ||
        std::memcmp(
            negotiatedAlpn.ProtocolId,
            RequiredAlpn,
            sizeof(RequiredAlpn)) != 0) {
        state_->Fail(SchannelClientFailure::AlpnPolicy);
        return false;
    }

    SecPkgContext_ConnectionInfo connection{};
    SecPkgContext_CipherInfo cipher{};
    cipher.dwVersion = SECPKGCONTEXT_CIPHERINFO_V1;
    if (QueryContextAttributesW(
            &state_->context,
            SECPKG_ATTR_CONNECTION_INFO,
            &connection) != SEC_E_OK ||
        QueryContextAttributesW(
            &state_->context,
            SECPKG_ATTR_CIPHER_INFO,
            &cipher) != SEC_E_OK ||
        !IsAcceptedSchannelProtocolAndCipher(
            connection.dwProtocol,
            cipher.dwCipherSuite)) {
        state_->Fail(SchannelClientFailure::TlsPolicy);
        return false;
    }
    PCCERT_CONTEXT certificate = nullptr;
    const SECURITY_STATUS certificateStatus =
        QueryContextAttributesW(
            &state_->context,
            SECPKG_ATTR_REMOTE_CERT_CONTEXT,
            &certificate);
    const bool acceptedCertificate =
        certificateStatus == SEC_E_OK &&
        IsAcceptedCertificate(certificate);
    if (certificate != nullptr) {
        CertFreeCertificateContext(certificate);
    }
    if (!acceptedCertificate) {
        state_->Fail(SchannelClientFailure::CertificatePolicy);
        return false;
    }

    if (QueryContextAttributesW(
            &state_->context,
            SECPKG_ATTR_STREAM_SIZES,
            &state_->streamSizes) != SEC_E_OK ||
        state_->streamSizes.cbMaximumMessage == 0 ||
        state_->streamSizes.cbMaximumMessage >
            PlaintextCapacity ||
        static_cast<std::size_t>(state_->streamSizes.cbHeader) +
                state_->streamSizes.cbMaximumMessage +
                state_->streamSizes.cbTrailer >
            sizeof(state_->encryptedOutput) ||
        RemainingMilliseconds(deadline) == 0) {
        state_->Fail(
            RemainingMilliseconds(deadline) == 0
                ? SchannelClientFailure::HandshakeDeadline
                : SchannelClientFailure::ContextAttributes);
        return false;
    }

    state_->MarkEstablished(
        connection.dwProtocol,
        cipher.dwCipherSuite);
    return true;
}

} // namespace godswar::network
