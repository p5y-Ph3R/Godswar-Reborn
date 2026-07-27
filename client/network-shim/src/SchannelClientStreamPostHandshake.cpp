#define SECURITY_WIN32
#define SCHANNEL_USE_BLACKLISTS

#include "SchannelClientStreamInternal.h"
#include "SchannelClientStreamPostHandshake.h"

#include "DevelopmentTlsCertificate.h"

#include <cstring>

namespace godswar::network {
namespace schannel_detail {
namespace {

bool RequiresComplete(SECURITY_STATUS status) noexcept {
    return status == SEC_I_COMPLETE_NEEDED ||
        status == SEC_I_COMPLETE_AND_CONTINUE;
}

bool RequiresContinue(SECURITY_STATUS status) noexcept {
    return status == SEC_I_CONTINUE_NEEDED ||
        status == SEC_I_COMPLETE_AND_CONTINUE;
}

} // namespace

bool IsTls13PostHandshakeRequest(
    DWORD negotiatedProtocol,
    SECURITY_STATUS status) noexcept {
    return negotiatedProtocol == SP_PROT_TLS1_3_CLIENT &&
        status == SEC_I_RENEGOTIATE;
}

bool AreTls13PostHandshakeParametersUnchanged(
    DWORD establishedProtocol,
    DWORD establishedCipherSuite,
    DWORD currentProtocol,
    DWORD currentCipherSuite) noexcept {
    return establishedProtocol == SP_PROT_TLS1_3_CLIENT &&
        currentProtocol == establishedProtocol &&
        currentCipherSuite == establishedCipherSuite &&
        IsAcceptedSchannelProtocolAndCipher(
            currentProtocol,
            currentCipherSuite);
}

bool IsAcceptedTls13PostHandshakeAlpn(
    bool establishedAlpnValidated,
    SECURITY_STATUS queryStatus,
    const SecPkgContext_ApplicationProtocol&
        queriedAlpn) noexcept {
    if (!establishedAlpnValidated ||
        queryStatus != SEC_E_OK) {
        return false;
    }

    const bool expected =
        queriedAlpn.ProtoNegoStatus ==
            SecApplicationProtocolNegotiationStatus_Success &&
        queriedAlpn.ProtoNegoExt ==
            SecApplicationProtocolNegotiationExt_ALPN &&
        queriedAlpn.ProtocolIdSize == sizeof(RequiredAlpn) &&
        std::memcmp(
            queriedAlpn.ProtocolId,
            RequiredAlpn,
            sizeof(RequiredAlpn)) == 0;
    const bool clearedBySchannel =
        queriedAlpn.ProtoNegoStatus ==
            SecApplicationProtocolNegotiationStatus_None &&
        queriedAlpn.ProtoNegoExt ==
            SecApplicationProtocolNegotiationExt_None &&
        queriedAlpn.ProtocolIdSize == 0;
    return expected || clearedBySchannel;
}

bool IsAcceptedSchannelCertificate(
    PCCERT_CONTEXT certificate) noexcept {
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
        std::strcmp(publicKeyOid, szOID_RSA_RSA) != 0 ||
        std::strcmp(signatureOid, szOID_RSA_SHA256RSA) != 0) {
        return false;
    }

    const DWORD bits = CertGetPublicKeyLength(
        X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
        const_cast<PCERT_PUBLIC_KEY_INFO>(&publicKey));
    return bits >= 2048;
}

} // namespace schannel_detail

bool SchannelClientStream::State::
ValidateTls13PostHandshakeContext(
    ULONG returnedAttributes) noexcept {
    DWORD establishedProtocol = 0;
    DWORD establishedCipherSuite = 0;
    bool establishedAlpnValidated = false;
    AcquireSRWLockShared(&snapshotLock);
    establishedProtocol = negotiatedProtocol;
    establishedCipherSuite = negotiatedCipherSuite;
    establishedAlpnValidated = alpnValidated;
    ReleaseSRWLockShared(&snapshotLock);

    SecPkgContext_ApplicationProtocol negotiatedAlpn{};
    const SECURITY_STATUS alpnStatus =
        QueryContextAttributesW(
            &context,
            SECPKG_ATTR_APPLICATION_PROTOCOL,
            &negotiatedAlpn);
    if (!HasRequiredSchannelStreamAttributes(
            returnedAttributes) ||
        !schannel_detail::IsAcceptedTls13PostHandshakeAlpn(
            establishedAlpnValidated,
            alpnStatus,
            negotiatedAlpn)) {
        return false;
    }

    SecPkgContext_ConnectionInfo connection{};
    SecPkgContext_CipherInfo cipher{};
    cipher.dwVersion = SECPKGCONTEXT_CIPHERINFO_V1;
    if (QueryContextAttributesW(
            &context,
            SECPKG_ATTR_CONNECTION_INFO,
            &connection) != SEC_E_OK ||
        QueryContextAttributesW(
            &context,
            SECPKG_ATTR_CIPHER_INFO,
            &cipher) != SEC_E_OK ||
        !schannel_detail::
            AreTls13PostHandshakeParametersUnchanged(
                establishedProtocol,
                establishedCipherSuite,
                connection.dwProtocol,
                cipher.dwCipherSuite)) {
        return false;
    }

    PCCERT_CONTEXT certificate = nullptr;
    const SECURITY_STATUS certificateStatus =
        QueryContextAttributesW(
            &context,
            SECPKG_ATTR_REMOTE_CERT_CONTEXT,
            &certificate);
    const bool acceptedCertificate =
        certificateStatus == SEC_E_OK &&
        schannel_detail::IsAcceptedSchannelCertificate(
            certificate) &&
        (!manualCertificateValidation ||
            (developmentRootValidated &&
                ValidateDevelopmentTlsServerCertificate(
                    certificate,
                    targetName) ==
                    DevelopmentTlsCertificateResult::Accepted));
    if (certificate != nullptr) {
        CertFreeCertificateContext(certificate);
    }
    if (!acceptedCertificate) {
        return false;
    }

    SecPkgContext_StreamSizes updatedSizes{};
    if (QueryContextAttributesW(
            &context,
            SECPKG_ATTR_STREAM_SIZES,
            &updatedSizes) != SEC_E_OK ||
        updatedSizes.cbMaximumMessage == 0 ||
        updatedSizes.cbMaximumMessage >
            schannel_detail::PlaintextCapacity ||
        static_cast<std::size_t>(updatedSizes.cbHeader) +
                updatedSizes.cbMaximumMessage +
                updatedSizes.cbTrailer >
            schannel_detail::EncryptedOutputCapacity) {
        return false;
    }
    streamSizes = updatedSizes;
    return true;
}

bool SchannelClientStream::State::ContinueTls13PostHandshake(
    const SecBuffer* decryptBuffers,
    std::size_t decryptBufferCount,
    ULONGLONG deadline) noexcept {
    DWORD protocol = 0;
    AcquireSRWLockShared(&snapshotLock);
    protocol = negotiatedProtocol;
    ReleaseSRWLockShared(&snapshotLock);
    if (!schannel_detail::IsTls13PostHandshakeRequest(
            protocol,
            SEC_I_RENEGOTIATE)) {
        SecureZeroMemory(
            encryptedInput,
            encryptedInputBytes);
        encryptedInputBytes = 0;
        Fail(SchannelClientFailure::RenegotiationRejected);
        return false;
    }

    std::size_t continuationBytes = 0;
    if (!schannel_detail::TryPrepareSchannelPostHandshakeToken(
            decryptBuffers,
            decryptBufferCount,
            encryptedInput,
            encryptedInputBytes,
            sizeof(encryptedInput),
            &continuationBytes)) {
        SecureZeroMemory(
            encryptedInput,
            encryptedInputBytes);
        encryptedInputBytes = 0;
        Fail(SchannelClientFailure::PostHandshakeProtocol);
        return false;
    }
    encryptedInputBytes = continuationBytes;

    ULONG returnedAttributes = 0;
    std::size_t cumulativeOutputBytes = 0;
    bool extraFound = false;
    std::size_t retainedBytes = 0;
    for (unsigned step = 0;
         step < schannel_detail::MaximumPostHandshakeSteps;
         ++step) {
        if (schannel_detail::RemainingMilliseconds(deadline) == 0) {
            Fail(SchannelClientFailure::PostHandshakeDeadline);
            return false;
        }

        SecBuffer inputBuffers[2]{};
        inputBuffers[0].BufferType = SECBUFFER_TOKEN;
        inputBuffers[0].cbBuffer =
            static_cast<unsigned long>(encryptedInputBytes);
        inputBuffers[0].pvBuffer = encryptedInput;
        inputBuffers[1].BufferType = SECBUFFER_EMPTY;
        SecBufferDesc input{};
        input.ulVersion = SECBUFFER_VERSION;
        input.cBuffers = 2;
        input.pBuffers = inputBuffers;

        SecBuffer outputBuffers[2]{};
        outputBuffers[0].BufferType = SECBUFFER_TOKEN;
        outputBuffers[1].BufferType = SECBUFFER_ALERT;
        SecBufferDesc output{};
        output.ulVersion = SECBUFFER_VERSION;
        output.cBuffers = 2;
        output.pBuffers = outputBuffers;

        const SECURITY_STATUS status =
            InitializeSecurityContextW(
                &credentials,
                &context,
                targetName,
                schannel_detail::RequestedContextAttributes |
                    (manualCertificateValidation
                        ? ISC_REQ_MANUAL_CRED_VALIDATION
                        : 0),
                0,
                0,
                &input,
                0,
                &context,
                &output,
                &returnedAttributes,
                nullptr);
        RecordSecurityStatus(status);

        bool outputAccepted = true;
        if (schannel_detail::RequiresComplete(status)) {
            const SECURITY_STATUS completeStatus =
                CompleteAuthToken(&context, &output);
            if (completeStatus != SEC_E_OK) {
                RecordSecurityStatus(completeStatus);
                outputAccepted = false;
            }
        }
        for (auto& buffer : outputBuffers) {
            if (buffer.pvBuffer == nullptr) {
                continue;
            }
            const std::size_t bufferBytes = buffer.cbBuffer;
            const bool bounded =
                bufferBytes > 0 &&
                cumulativeOutputBytes <=
                    schannel_detail::
                        MaximumPostHandshakeOutputBytes &&
                bufferBytes <=
                    schannel_detail::
                        MaximumPostHandshakeOutputBytes -
                        cumulativeOutputBytes;
            if (outputAccepted && bounded) {
                outputAccepted = RawWriteAll(
                    buffer.pvBuffer,
                    bufferBytes,
                    deadline);
                cumulativeOutputBytes += bufferBytes;
            } else {
                outputAccepted = false;
            }
            FreeContextBuffer(buffer.pvBuffer);
            buffer.pvBuffer = nullptr;
        }
        if (!outputAccepted) {
            Fail(
                schannel_detail::RemainingMilliseconds(deadline) == 0
                    ? SchannelClientFailure::PostHandshakeDeadline
                    : SchannelClientFailure::PostHandshakeWrite);
            return false;
        }

        if (status == SEC_E_INCOMPLETE_MESSAGE) {
            if (encryptedInputBytes == sizeof(encryptedInput)) {
                Fail(SchannelClientFailure::PostHandshakeProtocol);
                return false;
            }
            const auto read = RawRead(
                encryptedInput + encryptedInputBytes,
                sizeof(encryptedInput) - encryptedInputBytes,
                deadline);
            if (read.status != DeadlineStreamStatus::Success ||
                read.bytesTransferred == 0) {
                Fail(
                    read.status == DeadlineStreamStatus::TimedOut
                        ? SchannelClientFailure::
                            PostHandshakeDeadline
                        : SchannelClientFailure::PostHandshakeRead);
                return false;
            }
            encryptedInputBytes += read.bytesTransferred;
            continue;
        }

        const bool final =
            status == SEC_E_OK ||
            status == SEC_I_COMPLETE_NEEDED;
        if (!final &&
            !schannel_detail::RequiresContinue(status)) {
            SecureZeroMemory(
                encryptedInput,
                encryptedInputBytes);
            encryptedInputBytes = 0;
            Fail(SchannelClientFailure::PostHandshakeProtocol);
            return false;
        }

        extraFound = false;
        retainedBytes = 0;
        if (!schannel_detail::TryRetainSchannelExtraBuffer(
                inputBuffers,
                2,
                encryptedInput,
                encryptedInputBytes,
                sizeof(encryptedInput),
                &extraFound,
                &retainedBytes)) {
            SecureZeroMemory(
                encryptedInput,
                encryptedInputBytes);
            encryptedInputBytes = 0;
            Fail(SchannelClientFailure::PostHandshakeProtocol);
            return false;
        }
        if (extraFound) {
            encryptedInputBytes = retainedBytes;
        } else {
            SecureZeroMemory(
                encryptedInput,
                encryptedInputBytes);
            encryptedInputBytes = 0;
        }

        if (final) {
            if (!ValidateTls13PostHandshakeContext(
                    returnedAttributes)) {
                Fail(SchannelClientFailure::PostHandshakePolicy);
                return false;
            }
            return true;
        }

        if (encryptedInputBytes == 0) {
            const auto read = RawRead(
                encryptedInput,
                sizeof(encryptedInput),
                deadline);
            if (read.status != DeadlineStreamStatus::Success ||
                read.bytesTransferred == 0) {
                Fail(
                    read.status == DeadlineStreamStatus::TimedOut
                        ? SchannelClientFailure::
                            PostHandshakeDeadline
                        : SchannelClientFailure::PostHandshakeRead);
                return false;
            }
            encryptedInputBytes = read.bytesTransferred;
        }
    }

    SecureZeroMemory(encryptedInput, encryptedInputBytes);
    encryptedInputBytes = 0;
    Fail(SchannelClientFailure::PostHandshakeLimit);
    return false;
}

} // namespace godswar::network
