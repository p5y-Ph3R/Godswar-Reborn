#include "DevelopmentTlsCertificate.h"

#include <bcrypt.h>

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace godswar::network {
namespace {

constexpr std::size_t RootBytes = 1042;
constexpr std::uint8_t RootSha256[] = {
    0x91, 0x1E, 0x3C, 0xF4, 0x44, 0xB6, 0x31, 0xAA,
    0xB9, 0xED, 0xCC, 0x59, 0x80, 0xDF, 0x65, 0x24,
    0x3C, 0xAA, 0xC4, 0x2B, 0x90, 0x00, 0xC5, 0xE2,
    0x41, 0x0C, 0x7D, 0xAD, 0xFE, 0xB5, 0x4D, 0xED,
};
constexpr char RootBase64[] =
    "MIIEDjCCAnagAwIBAgIISpQnwEugYtYwDQYJKoZIhvcNAQELBQAwJTEjMCEG"
    "A1UEAxMaUmVib3JuIERldmVsb3BtZW50IFJvb3QgQ0EwHhcNMjYwNzI1MDIy"
    "MTI3WhcNMjYwODEwMDIyMTI3WjAlMSMwIQYDVQQDExpSZWJvcm4gRGV2ZWxv"
    "cG1lbnQgUm9vdCBDQTCCAaIwDQYJKoZIhvcNAQEBBQADggGPADCCAYoCggGB"
    "ALX2oU1A2TItIFKNmFw1hf1jsWr224RWHJnuySG4P/MKTSvt/lZNT+3yBMrP"
    "KgMNIQbkfRKQEcucBiCmx6kSPAQ1f2F8jkVEPeebyuaHSs1u07phYIYjluvf"
    "EOlw37Pys6UkejgtC+09t+3v57MM9G6rlOiSF7Vqw0iI7Wi5WIGTR8fNcmZv"
    "FcF7+D6WLgZUkBmOX0IehN9wTb5w1Cl8fl0vQKpfW27z0wmH/9jg1wYv6NCM"
    "FgfAYfVn1pu58M2IgMxXj0Yj3JyFuIYgxKMxRnkx1qT6oxhbchpft+PleS3o"
    "sIT6DJWB99zm9HFpHEBLMaPk3CS2BBw/RULlpTMsvecdqYqTgnrruHxzNKHFq"
    "tCmqPbjEH5EAzjhfnzoJW6DaYykRV95KIDo5EmFdERTMv4In+JrodN/12Wem"
    "NXZKNWiatfWfwlCiqAh0M5WOrSiMnATM4c68v8oJhaPpC3eXaV4gxWli+TwK"
    "8aQ9WTf3OU2ld/QmKkjLOgn5idmwi3d3QIDAQABo0IwQDAPBgNVHRMBAf8E"
    "BTADAQH/MA4GA1UdDwEB/wQEAwIBBjAdBgNVHQ4EFgQUGSrtCAyRWhkiBCeY"
    "EP8W8cgwqDgwDQYJKoZIhvcNAQELBQADggGBAAWGo8llnOmtuhPMZKWfqA6Q"
    "NJc72QHNnh/SKpPeXOjVzvqvEzQmQ5R4Gf2LLn3/3DDx0NsU2iacGoB/DPhI"
    "w/Mxg8YlRbA7SLkr09xNZMe5kr6VVbPmA/FJJgqZ1z0z3wvUt3fabHjKBgRt"
    "81j975FeVDtXH5N1mMiaNMYRqV2MjfQDCZItPZv3jUOLZ2D9rmvyL/AYUAXW"
    "SiydNTX3aNFLlvcEaLj/q/qbHIc9CMB9py/jNpCsuk8I0ruPp4UAflqKiFpB"
    "upYpqzXeNIyCrmbfsPemyhgyhZ5q4Xa3TEC5YCFfDXTw83gdErc/1JSc5mlI"
    "gthESwbtJeojFmIHhVi1CLInKy4gPl+QQ7/nYGLeID5pf+aAAWFX9UavrDvd"
    "F2IhtGRexy2dWKAurjt/6YIgEsm6YDuYpYtzK/LKWqWz8Wj96WrrMOfL8NQa"
    "vfh9yhdXhdP6Ew8BqsFvX7tqovbRvLJArh8C4WthtXBLlV4zzfIAzCFfoUM0"
    "ukQU6tjZ0w==";

bool DecodeRoot(
    std::uint8_t* destination,
    DWORD* destinationBytes) noexcept {
    if (destination == nullptr ||
        destinationBytes == nullptr ||
        *destinationBytes != RootBytes ||
        !CryptStringToBinaryA(
            RootBase64,
            0,
            CRYPT_STRING_BASE64,
            destination,
            destinationBytes,
            nullptr,
            nullptr) ||
        *destinationBytes != RootBytes) {
        return false;
    }

    std::uint8_t digest[sizeof(RootSha256)]{};
    DWORD digestBytes = sizeof(digest);
    const bool valid =
        CryptHashCertificate2(
            BCRYPT_SHA256_ALGORITHM,
            0,
            nullptr,
            destination,
            *destinationBytes,
            digest,
            &digestBytes) &&
        digestBytes == sizeof(RootSha256) &&
        std::memcmp(
            digest,
            RootSha256,
            sizeof(RootSha256)) == 0;
    SecureZeroMemory(digest, sizeof(digest));
    return valid;
}

bool HasExactAnchor(
    PCCERT_CHAIN_CONTEXT chain,
    PCCERT_CONTEXT root) noexcept {
    if (chain == nullptr ||
        root == nullptr ||
        chain->cChain == 0 ||
        chain->rgpChain == nullptr) {
        return false;
    }

    const auto* simple = chain->rgpChain[0];
    if (simple == nullptr ||
        simple->cElement == 0 ||
        simple->rgpElement == nullptr) {
        return false;
    }
    const auto* element =
        simple->rgpElement[simple->cElement - 1];
    const auto* anchor =
        element == nullptr ? nullptr : element->pCertContext;
    return anchor != nullptr &&
        anchor->cbCertEncoded == root->cbCertEncoded &&
        std::memcmp(
            anchor->pbCertEncoded,
            root->pbCertEncoded,
            root->cbCertEncoded) == 0;
}

} // namespace

bool IsEmbeddedDevelopmentTlsRootValid() noexcept {
    std::uint8_t rootBytes[RootBytes]{};
    DWORD decodedBytes = sizeof(rootBytes);
    const bool valid = DecodeRoot(rootBytes, &decodedBytes);
    SecureZeroMemory(rootBytes, sizeof(rootBytes));
    return valid;
}

DevelopmentTlsCertificateResult
ValidateDevelopmentTlsServerCertificate(
    PCCERT_CONTEXT certificate,
    const wchar_t* targetName) noexcept {
    if (certificate == nullptr ||
        targetName == nullptr ||
        *targetName == L'\0') {
        return DevelopmentTlsCertificateResult::InvalidArgument;
    }

    std::uint8_t rootBytes[RootBytes]{};
    DWORD decodedBytes = sizeof(rootBytes);
    if (!DecodeRoot(rootBytes, &decodedBytes)) {
        SecureZeroMemory(rootBytes, sizeof(rootBytes));
        return DevelopmentTlsCertificateResult::RootIdentity;
    }
    PCCERT_CONTEXT root = CertCreateCertificateContext(
        X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
        rootBytes,
        decodedBytes);
    SecureZeroMemory(rootBytes, sizeof(rootBytes));
    if (root == nullptr) {
        return DevelopmentTlsCertificateResult::RootDecode;
    }

    HCERTSTORE store = CertOpenStore(
        CERT_STORE_PROV_MEMORY,
        0,
        0,
        CERT_STORE_CREATE_NEW_FLAG,
        nullptr);
    if (store == nullptr ||
        !CertAddCertificateContextToStore(
            store,
            root,
            CERT_STORE_ADD_ALWAYS,
            nullptr)) {
        if (store != nullptr) {
            CertCloseStore(store, 0);
        }
        CertFreeCertificateContext(root);
        return DevelopmentTlsCertificateResult::StoreCreation;
    }

    CERT_CHAIN_ENGINE_CONFIG engineConfig{};
    engineConfig.cbSize = sizeof(engineConfig);
    engineConfig.hExclusiveRoot = store;
    engineConfig.dwUrlRetrievalTimeout = 0;
    HCERTCHAINENGINE engine = nullptr;
    if (!CertCreateCertificateChainEngine(
            &engineConfig,
            &engine)) {
        CertCloseStore(store, 0);
        CertFreeCertificateContext(root);
        return DevelopmentTlsCertificateResult::
            ChainEngineCreation;
    }

    LPSTR usage = const_cast<LPSTR>(
        szOID_PKIX_KP_SERVER_AUTH);
    CERT_CHAIN_PARA chainParameters{};
    chainParameters.cbSize = sizeof(chainParameters);
    chainParameters.RequestedUsage.dwType =
        USAGE_MATCH_TYPE_AND;
    chainParameters.RequestedUsage.Usage
        .cUsageIdentifier = 1;
    chainParameters.RequestedUsage.Usage
        .rgpszUsageIdentifier = &usage;
    PCCERT_CHAIN_CONTEXT chain = nullptr;
    const bool built = CertGetCertificateChain(
        engine,
        certificate,
        nullptr,
        certificate->hCertStore,
        &chainParameters,
        CERT_CHAIN_CACHE_ONLY_URL_RETRIEVAL |
            CERT_CHAIN_DISABLE_AUTH_ROOT_AUTO_UPDATE,
        nullptr,
        &chain) != FALSE;
    if (!built ||
        chain == nullptr ||
        chain->TrustStatus.dwErrorStatus !=
            CERT_TRUST_NO_ERROR) {
        if (chain != nullptr) {
            CertFreeCertificateChain(chain);
        }
        CertFreeCertificateChainEngine(engine);
        CertCloseStore(store, 0);
        CertFreeCertificateContext(root);
        return DevelopmentTlsCertificateResult::ChainBuild;
    }

    DevelopmentTlsCertificateResult result =
        DevelopmentTlsCertificateResult::Accepted;
    if (!HasExactAnchor(chain, root)) {
        result =
            DevelopmentTlsCertificateResult::WrongAnchor;
    } else {
        SSL_EXTRA_CERT_CHAIN_POLICY_PARA ssl{};
        ssl.cbSize = sizeof(ssl);
        ssl.dwAuthType = AUTHTYPE_SERVER;
        ssl.pwszServerName =
            const_cast<LPWSTR>(targetName);
        CERT_CHAIN_POLICY_PARA policy{};
        policy.cbSize = sizeof(policy);
        policy.pvExtraPolicyPara = &ssl;
        CERT_CHAIN_POLICY_STATUS status{};
        status.cbSize = sizeof(status);
        if (!CertVerifyCertificateChainPolicy(
                CERT_CHAIN_POLICY_SSL,
                chain,
                &policy,
                &status) ||
            status.dwError != 0) {
            result =
                DevelopmentTlsCertificateResult::
                    PolicyRejected;
        }
    }

    CertFreeCertificateChain(chain);
    CertFreeCertificateChainEngine(engine);
    CertCloseStore(store, 0);
    CertFreeCertificateContext(root);
    return result;
}

} // namespace godswar::network
