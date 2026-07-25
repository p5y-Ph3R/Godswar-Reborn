#pragma once

int RunSecureManifestProbe(
    const wchar_t* candidatePath,
    const wchar_t* manifestPath) noexcept;
int RunSecureCandidateContractProbe(
    const wchar_t* candidatePath) noexcept;
