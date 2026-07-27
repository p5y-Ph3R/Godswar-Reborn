#pragma once

#include <Windows.h>
#include <wincrypt.h>

#include <cstdint>

namespace godswar::network {

enum class DevelopmentTlsCertificateResult : std::uint8_t {
    Accepted = 0,
    InvalidArgument,
    RootDecode,
    RootIdentity,
    StoreCreation,
    ChainEngineCreation,
    ChainBuild,
    WrongAnchor,
    PolicyRejected,
};

bool IsEmbeddedDevelopmentTlsRootValid() noexcept;

DevelopmentTlsCertificateResult
ValidateDevelopmentTlsServerCertificate(
    PCCERT_CONTEXT certificate,
    const wchar_t* targetName) noexcept;

} // namespace godswar::network
