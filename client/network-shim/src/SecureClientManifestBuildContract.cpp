#include "SecureClientManifestBuildContract.h"

#include "SecureClientManifestDevelopmentKeys.generated.h"
#include "SecureClientOriginIdentity.generated.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

constexpr std::uint64_t MaximumCandidateBytes =
    16ULL * 1024ULL * 1024ULL;
constexpr char ContractMagic[8] = {
    'G', 'W', 'K', 'E', 'Y', '0', '2', '\0',
};
constexpr SecureClientManifestBuildContract MakeContract() noexcept {
    SecureClientManifestBuildContract contract{};
    for (std::size_t index = 0; index < sizeof(ContractMagic); ++index) {
        contract.magic[index] = ContractMagic[index];
    }
    contract.version = 2;
    contract.structureBytes =
        sizeof(SecureClientManifestBuildContract);
    contract.environment =
        static_cast<std::uint8_t>(
            EndpointManifestEnvironment::Development);
    contract.currentKeyId = 0xD001;
    contract.nextKeyId = 0xD002;
    contract.compiledMinimumSequence = 1;
    for (std::size_t index = 0; index < 32; ++index) {
        contract.currentKey.x[index] =
            development_manifest_keys::CurrentX[index];
        contract.currentKey.y[index] =
            development_manifest_keys::CurrentY[index];
        contract.nextKey.x[index] =
            development_manifest_keys::NextX[index];
        contract.nextKey.y[index] =
            development_manifest_keys::NextY[index];
        contract.originSha256[index] =
            secure_client_origin_identity::Sha256[index];
    }
    return contract;
}

#pragma section(".gwkey", read)
__declspec(allocate(".gwkey"))
const SecureClientManifestBuildContract EmbeddedContract =
    MakeContract();

bool RangeWithin(
    std::size_t offset,
    std::size_t length,
    std::size_t total) noexcept {
    return offset <= total &&
        length <= total - offset;
}

} // namespace

const SecureClientManifestBuildContract&
GetSecureClientManifestBuildContract() noexcept {
    return EmbeddedContract;
}

bool IsValidSecureClientManifestBuildContract(
    const SecureClientManifestBuildContract& contract) noexcept {
    const std::uint8_t zero[sizeof(contract.reserved)]{};
    std::uint8_t originCombined = 0;
    for (const auto value : contract.originSha256) {
        originCombined |= value;
    }
    return std::memcmp(
               contract.magic,
               ContractMagic,
               sizeof(ContractMagic)) == 0 &&
        contract.version == 2 &&
        contract.structureBytes == sizeof(contract) &&
        contract.environment ==
            static_cast<std::uint8_t>(
                EndpointManifestEnvironment::Development) &&
        std::memcmp(
            contract.reserved,
            zero,
            sizeof(zero)) == 0 &&
        contract.currentKeyId == 0xD001 &&
        contract.nextKeyId == 0xD002 &&
        contract.compiledMinimumSequence != 0 &&
        originCombined != 0;
}

bool ReadSecureClientManifestBuildContract(
    const wchar_t* candidatePath,
    SecureClientManifestBuildContract* contract) noexcept {
    if (candidatePath == nullptr || contract == nullptr) {
        return false;
    }
    *contract = SecureClientManifestBuildContract{};

    const HANDLE file = CreateFileW(
        candidatePath,
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL |
            FILE_FLAG_OPEN_REPARSE_POINT |
            FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }

    FILE_ATTRIBUTE_TAG_INFO attributes{};
    LARGE_INTEGER length{};
    bool succeeded = GetFileInformationByHandleEx(
            file,
            FileAttributeTagInfo,
            &attributes,
            sizeof(attributes)) != FALSE &&
        (attributes.FileAttributes &
            (FILE_ATTRIBUTE_DIRECTORY |
                FILE_ATTRIBUTE_REPARSE_POINT)) == 0 &&
        GetFileSizeEx(file, &length) != FALSE &&
        length.QuadPart >=
            static_cast<LONGLONG>(sizeof(IMAGE_DOS_HEADER)) &&
        length.QuadPart <=
            static_cast<LONGLONG>(MaximumCandidateBytes);
    const auto bytes = succeeded
        ? static_cast<std::size_t>(length.QuadPart)
        : 0;
    void* raw = succeeded
        ? HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, bytes)
        : nullptr;
    if (raw == nullptr) {
        succeeded = false;
    }

    DWORD read = 0;
    if (succeeded) {
        succeeded = ReadFile(
                file,
                raw,
                static_cast<DWORD>(bytes),
                &read,
                nullptr) != FALSE &&
            read == bytes;
    }
    CloseHandle(file);

    if (succeeded) {
        const auto* fileBytes =
            static_cast<const std::uint8_t*>(raw);
        const auto* dos =
            reinterpret_cast<const IMAGE_DOS_HEADER*>(fileBytes);
        const auto ntOffset =
            dos->e_lfanew >= 0
                ? static_cast<std::size_t>(dos->e_lfanew)
                : bytes;
        succeeded = dos->e_magic == IMAGE_DOS_SIGNATURE &&
            RangeWithin(
                ntOffset,
                sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER),
                bytes);
        const IMAGE_FILE_HEADER* fileHeader = nullptr;
        std::size_t sectionOffset = 0;
        if (succeeded) {
            const auto signature =
                *reinterpret_cast<const DWORD*>(
                    fileBytes + ntOffset);
            fileHeader =
                reinterpret_cast<const IMAGE_FILE_HEADER*>(
                    fileBytes + ntOffset + sizeof(DWORD));
            sectionOffset =
                ntOffset + sizeof(DWORD) +
                sizeof(IMAGE_FILE_HEADER) +
                fileHeader->SizeOfOptionalHeader;
            succeeded = signature == IMAGE_NT_SIGNATURE &&
                fileHeader->Machine == IMAGE_FILE_MACHINE_I386 &&
                fileHeader->NumberOfSections != 0 &&
                fileHeader->NumberOfSections <= 96 &&
                RangeWithin(
                    sectionOffset,
                    static_cast<std::size_t>(
                        fileHeader->NumberOfSections) *
                        sizeof(IMAGE_SECTION_HEADER),
                    bytes);
        }

        unsigned matches = 0;
        if (succeeded) {
            const auto* sections =
                reinterpret_cast<const IMAGE_SECTION_HEADER*>(
                    fileBytes + sectionOffset);
            for (unsigned index = 0;
                 index < fileHeader->NumberOfSections;
                 ++index) {
                const char expected[IMAGE_SIZEOF_SHORT_NAME] = {
                    '.', 'g', 'w', 'k', 'e', 'y', '\0', '\0',
                };
                const auto& section = sections[index];
                if (std::memcmp(
                        section.Name,
                        expected,
                        sizeof(expected)) != 0) {
                    continue;
                }
                ++matches;
                const auto offset =
                    static_cast<std::size_t>(
                        section.PointerToRawData);
                if ((section.Characteristics &
                        IMAGE_SCN_MEM_READ) == 0 ||
                    (section.Characteristics &
                        IMAGE_SCN_MEM_WRITE) != 0 ||
                    !RangeWithin(
                        offset,
                        sizeof(*contract),
                        bytes)) {
                    succeeded = false;
                    break;
                }
                std::memcpy(
                    contract,
                    fileBytes + offset,
                    sizeof(*contract));
            }
            succeeded = succeeded &&
                matches == 1 &&
                IsValidSecureClientManifestBuildContract(
                    *contract);
        }
    }

    if (raw != nullptr) {
        SecureZeroMemory(raw, bytes);
        HeapFree(GetProcessHeap(), 0, raw);
    }
    if (!succeeded) {
        *contract = SecureClientManifestBuildContract{};
    }
    return succeeded;
}

} // namespace godswar::network
