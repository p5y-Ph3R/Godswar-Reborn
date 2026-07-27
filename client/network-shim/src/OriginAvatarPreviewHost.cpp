#include "AvatarPreviewGate.h"
#include "FileSha256.h"
#include "SecureClientManifestBuildContract.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace {

constexpr std::uintptr_t OriginImageBase = 0x00400000;
constexpr DWORD OriginTimestamp = 0x52AA79CA;
constexpr DWORD OriginImageSize = 0x011DE000;
constexpr DWORD OriginEntryPointRva = 0x003BF68D;

constexpr std::uintptr_t ResourceSlotRvas[] = {
    0x01176088,
    0x0117608C,
    0x01176090,
    0x0117609C,
    0x011760A0,
    0x011760A4,
};

constexpr std::uint8_t LifecycleHook[] = {
    0xE9, 0x36, 0x1E, 0x50, 0x00,
};
constexpr std::uint8_t LifecycleCave[] = {
    0xC6, 0x05, 0x70, 0x5F, 0x57, 0x01, 0x00, 0x68,
    0x04, 0x5A, 0x9E, 0x00, 0xE9, 0xB9, 0xE1, 0xAF,
    0xFF,
};
constexpr std::uint8_t SecondaryGuardHook[] = {
    0xE9, 0x59, 0x2D, 0x3D, 0x00, 0x90,
};
constexpr std::uint8_t SecondaryGuardCave[] = {
    0xA1, 0x88, 0x60, 0x57, 0x01, 0x85, 0xC0, 0x74,
    0x38, 0xA1, 0x8C, 0x60, 0x57, 0x01, 0x85, 0xC0,
    0x74, 0x2F, 0xA1, 0x90, 0x60, 0x57, 0x01, 0x85,
    0xC0, 0x74, 0x26, 0xA1, 0x9C, 0x60, 0x57, 0x01,
    0x85, 0xC0, 0x74, 0x1D, 0xA1, 0xA0, 0x60, 0x57,
    0x01, 0x85, 0xC0, 0x74, 0x14, 0xA1, 0xA4, 0x60,
    0x57, 0x01, 0x85, 0xC0, 0x74, 0x0B, 0x8B, 0x44,
    0x24, 0x50, 0x33, 0xDB, 0xE9, 0x67, 0xD2, 0xC2,
    0xFF, 0xE9, 0x5D, 0xDA, 0xC2, 0xFF,
};
constexpr std::uint8_t PrimaryGuardHook[] = {
    0xE9, 0x29, 0xE8, 0x3C, 0x00, 0x90,
};
constexpr std::uint8_t PrimaryGuardCave[] = {
    0xA1, 0x88, 0x60, 0x57, 0x01, 0x85, 0xC0, 0x74,
    0x36, 0xA1, 0x8C, 0x60, 0x57, 0x01, 0x85, 0xC0,
    0x74, 0x2D, 0xA1, 0x90, 0x60, 0x57, 0x01, 0x85,
    0xC0, 0x74, 0x24, 0xA1, 0x9C, 0x60, 0x57, 0x01,
    0x85, 0xC0, 0x74, 0x1B, 0xA1, 0xA0, 0x60, 0x57,
    0x01, 0x85, 0xC0, 0x74, 0x12, 0xA1, 0xA4, 0x60,
    0x57, 0x01, 0x85, 0xC0, 0x74, 0x09, 0x33, 0xFF,
    0x33, 0xC9, 0xE9, 0xA1, 0x17, 0xC3, 0xFF, 0xE9,
    0x79, 0x1E, 0xC3, 0xFF,
};
constexpr std::uint8_t TimeoutGuardHook[] = {
    0xE9, 0x64, 0xDB, 0x3C, 0x00, 0x90,
};
constexpr std::uint8_t TimeoutGuardCave[] = {
    0x83, 0x3D, 0x4C, 0x5F, 0x57, 0x01, 0x02, 0x75,
    0x12, 0xA1, 0xA0, 0x60, 0x57, 0x01, 0x85, 0xC0,
    0x74, 0x14, 0xA1, 0x8C, 0x60, 0x57, 0x01, 0x85,
    0xC0, 0x74, 0x0B, 0x8B, 0x0D, 0xA0, 0x60, 0x57,
    0x01, 0xE9, 0x77, 0x24, 0xC3, 0xFF, 0xBF, 0x02,
    0x00, 0x00, 0x00, 0xC6, 0x05, 0x66, 0x5C, 0x57,
    0x01, 0x01, 0x89, 0x3D, 0x50, 0x5F, 0x57, 0x01,
    0xE9, 0x8E, 0x24, 0xC3, 0xFF,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00,
};
constexpr std::uint8_t NativeInitializerPrefix[] = {
    0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8, 0x6A, 0xFF,
    0x68, 0x60, 0x78, 0x81, 0x00, 0x64, 0xA1, 0x00,
    0x00, 0x00, 0x00, 0x50, 0x83, 0xEC, 0x50, 0x53,
    0x56, 0x57, 0xA1, 0xB4, 0x79, 0x9C, 0x00, 0x33,
    0xC4, 0x50, 0x8D, 0x44, 0x24, 0x60,
};
constexpr std::uint8_t StateObjectVtablePrefix[] = {
    0xA0, 0x76, 0x46, 0x00,
    0x40, 0x72, 0x46, 0x00,
    0x80, 0x72, 0x46, 0x00,
};
constexpr std::uint8_t StateObjectVtablePointer[] = {
    0x3C, 0x20, 0x95, 0x00,
};

constexpr std::uintptr_t LifecycleHookRva = 0x000C14C5;
constexpr std::uintptr_t LifecycleCaveRva = 0x005C3300;
constexpr std::uintptr_t SecondaryGuardHookRva = 0x001F05C2;
constexpr std::uintptr_t SecondaryGuardCaveRva = 0x005C3320;
constexpr std::uintptr_t PrimaryGuardHookRva = 0x001F4A82;
constexpr std::uintptr_t PrimaryGuardCaveRva = 0x005C32B0;
constexpr std::uintptr_t TimeoutGuardHookRva = 0x001F58B6;
constexpr std::uintptr_t TimeoutGuardCaveRva = 0x005C341F;
constexpr std::uintptr_t NativeInitializerRva = 0x00067280;
constexpr std::uintptr_t StateObjectRva = 0x005E5A04;
constexpr std::uintptr_t StateObjectVtableRva = 0x0055203C;
constexpr std::uintptr_t CurrentStateRva = 0x01175F4C;
constexpr std::uintptr_t RegisteredStateObjectRva = 0x01175F6C;
constexpr std::uintptr_t StateManagerRva = 0x01175F48;
constexpr std::uintptr_t StateManagerVtableOneRva = 0x00550104;
constexpr std::uintptr_t StateManagerVtableTwoRva = 0x005501BC;
constexpr std::uintptr_t StateManagerDispatchOneRva = 0x00004A20;
constexpr std::uintptr_t StateManagerDispatchTwoRva = 0x0000D6C0;
constexpr std::int32_t CharacterSelectionState = 2;
constexpr std::uint16_t AfterLoginRecordLength = 44;
constexpr std::uint16_t AfterLoginOpcode = 0x2876;
constexpr std::uint16_t CharacterPreviewLength = 188;
constexpr std::uint16_t CharacterPreviewOpcode = 0x2712;
constexpr std::uint8_t CharacterPreviewCount = 1;

static_assert(sizeof(LifecycleCave) == 17);
static_assert(sizeof(SecondaryGuardCave) == 70);
static_assert(sizeof(PrimaryGuardCave) == 68);
static_assert(sizeof(TimeoutGuardCave) == 96);

bool HasReadableProtection(DWORD protection) noexcept {
    if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
        return false;
    }

    switch (protection & 0xFF) {
        case PAGE_READONLY:
        case PAGE_READWRITE:
        case PAGE_WRITECOPY:
        case PAGE_EXECUTE_READ:
        case PAGE_EXECUTE_READWRITE:
        case PAGE_EXECUTE_WRITECOPY:
            return true;
        default:
            return false;
    }
}

bool HasExecutableProtection(DWORD protection) noexcept {
    if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
        return false;
    }

    switch (protection & 0xFF) {
        case PAGE_EXECUTE:
        case PAGE_EXECUTE_READ:
        case PAGE_EXECUTE_READWRITE:
        case PAGE_EXECUTE_WRITECOPY:
            return true;
        default:
            return false;
    }
}

bool IsRangeAccessible(
    const void* address,
    std::size_t length,
    bool requireExecutable) noexcept {
    if (address == nullptr || length == 0) {
        return false;
    }

    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) == 0 ||
        memory.State != MEM_COMMIT) {
        return false;
    }

    const auto start = reinterpret_cast<std::uintptr_t>(address);
    const auto regionStart =
        reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
    const auto regionEnd = regionStart + memory.RegionSize;
    if (regionEnd < regionStart ||
        start < regionStart ||
        start > regionEnd ||
        length > regionEnd - start) {
        return false;
    }

    return requireExecutable
        ? HasExecutableProtection(memory.Protect)
        : HasReadableProtection(memory.Protect);
}

bool HasBytes(
    const std::uint8_t* image,
    std::uintptr_t rva,
    const std::uint8_t* expected,
    std::size_t length) noexcept {
    const auto* address = image + rva;
    return IsRangeAccessible(address, length, false) &&
        std::memcmp(address, expected, length) == 0;
}

bool IsOriginFileName(const wchar_t* path) noexcept {
    if (path == nullptr) {
        return false;
    }

    const wchar_t* name = path;
    for (const auto* cursor = path; *cursor != L'\0'; ++cursor) {
        if (*cursor == L'\\' || *cursor == L'/') {
            name = cursor + 1;
        }
    }

    return CompareStringOrdinal(
        name,
        -1,
        L"Origin.exe",
        -1,
        TRUE) == CSTR_EQUAL;
}

bool HasSupportedPeIdentity(const std::uint8_t* image) noexcept {
    if (reinterpret_cast<std::uintptr_t>(image) != OriginImageBase ||
        !IsRangeAccessible(image, sizeof(IMAGE_DOS_HEADER), false)) {
        return false;
    }

    __try {
        const auto* dos =
            reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE ||
            dos->e_lfanew < static_cast<LONG>(sizeof(IMAGE_DOS_HEADER))) {
            return false;
        }

        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(
            image + dos->e_lfanew);
        if (!IsRangeAccessible(nt, sizeof(*nt), false)) {
            return false;
        }

        return nt->Signature == IMAGE_NT_SIGNATURE &&
            nt->FileHeader.Machine == IMAGE_FILE_MACHINE_I386 &&
            nt->FileHeader.TimeDateStamp == OriginTimestamp &&
            nt->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC &&
            nt->OptionalHeader.ImageBase == OriginImageBase &&
            nt->OptionalHeader.SizeOfImage == OriginImageSize &&
            nt->OptionalHeader.AddressOfEntryPoint == OriginEntryPointRva;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool HasSupportedRuntimeImage(const std::uint8_t* image) noexcept {
    return HasSupportedPeIdentity(image) &&
        HasBytes(
            image,
            LifecycleHookRva,
            LifecycleHook,
            sizeof(LifecycleHook)) &&
        HasBytes(
            image,
            LifecycleCaveRva,
            LifecycleCave,
            sizeof(LifecycleCave)) &&
        HasBytes(
            image,
            SecondaryGuardHookRva,
            SecondaryGuardHook,
            sizeof(SecondaryGuardHook)) &&
        HasBytes(
            image,
            SecondaryGuardCaveRva,
            SecondaryGuardCave,
            sizeof(SecondaryGuardCave)) &&
        HasBytes(
            image,
            PrimaryGuardHookRva,
            PrimaryGuardHook,
            sizeof(PrimaryGuardHook)) &&
        HasBytes(
            image,
            PrimaryGuardCaveRva,
            PrimaryGuardCave,
            sizeof(PrimaryGuardCave)) &&
        HasBytes(
            image,
            TimeoutGuardHookRva,
            TimeoutGuardHook,
            sizeof(TimeoutGuardHook)) &&
        HasBytes(
            image,
            TimeoutGuardCaveRva,
            TimeoutGuardCave,
            sizeof(TimeoutGuardCave)) &&
        HasBytes(
            image,
            NativeInitializerRva,
            NativeInitializerPrefix,
            sizeof(NativeInitializerPrefix)) &&
        HasBytes(
            image,
            StateObjectVtableRva,
            StateObjectVtablePrefix,
            sizeof(StateObjectVtablePrefix)) &&
        HasBytes(
            image,
            StateObjectRva,
            StateObjectVtablePointer,
            sizeof(StateObjectVtablePointer));
}

bool HasUsableStateManager(
    const std::uint8_t* image,
    void* manager) noexcept {
    if (!IsRangeAccessible(manager, sizeof(void*), false)) {
        return false;
    }

    __try {
        auto** vtable = *static_cast<void***>(manager);
        void* expectedDispatch = nullptr;
        if (vtable == reinterpret_cast<void**>(
                const_cast<std::uint8_t*>(
                    image + StateManagerVtableOneRva))) {
            expectedDispatch = const_cast<std::uint8_t*>(
                image + StateManagerDispatchOneRva);
        } else if (vtable == reinterpret_cast<void**>(
                       const_cast<std::uint8_t*>(
                           image + StateManagerVtableTwoRva))) {
            expectedDispatch = const_cast<std::uint8_t*>(
                image + StateManagerDispatchTwoRva);
        } else {
            return false;
        }

        return IsRangeAccessible(
                   vtable,
                   4 * sizeof(void*),
                   false) &&
            vtable[3] == expectedDispatch &&
            IsRangeAccessible(expectedDispatch, 1, true);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

} // namespace

namespace godswar::network {

bool IsSupportedOriginAvatarHost() noexcept {
    const auto& contract =
        godswar::network::GetSecureClientManifestBuildContract();
    const auto module = GetModuleHandleW(nullptr);
    if (module == nullptr ||
        !godswar::network::
            IsValidSecureClientManifestBuildContract(contract)) {
        return false;
    }

    wchar_t path[4096]{};
    const auto pathLength = GetModuleFileNameW(
        module,
        path,
        static_cast<DWORD>(sizeof(path) / sizeof(path[0])));
    if (pathLength == 0 ||
        pathLength >= sizeof(path) / sizeof(path[0]) ||
        !IsOriginFileName(path)) {
        return false;
    }

    const auto* image =
        reinterpret_cast<const std::uint8_t*>(module);
    return HasSupportedRuntimeImage(image) &&
        FileMatchesSha256(
            path,
            contract.originSha256,
            sizeof(contract.originSha256));
}

bool AreOriginAvatarResourcesReady() noexcept {
    const auto module = GetModuleHandleW(nullptr);
    if (reinterpret_cast<std::uintptr_t>(module) != OriginImageBase) {
        return false;
    }

    const auto* image =
        reinterpret_cast<const std::uint8_t*>(module);
    for (const auto rva : ResourceSlotRvas) {
        auto* const* slot =
            reinterpret_cast<void* const*>(image + rva);
        if (!IsRangeAccessible(slot, sizeof(*slot), false)) {
            return false;
        }

        __try {
            if (*slot == nullptr) {
                return false;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    return true;
}

AvatarPreloadResult RequestOriginAvatarPreload() noexcept {
    const auto module = GetModuleHandleW(nullptr);
    if (reinterpret_cast<std::uintptr_t>(module) != OriginImageBase) {
        return AvatarPreloadResult::NotInvoked;
    }

    auto* const image = reinterpret_cast<std::uint8_t*>(module);
    if (!HasSupportedRuntimeImage(image)) {
        return AvatarPreloadResult::NotInvoked;
    }
    if (AreOriginAvatarResourcesReady()) {
        return AvatarPreloadResult::Ready;
    }

    auto* const currentState = reinterpret_cast<const std::int32_t*>(
        image + CurrentStateRva);
    auto* const registeredStateObject =
        reinterpret_cast<void* const*>(
            image + RegisteredStateObjectRva);
    auto* const stateManager =
        reinterpret_cast<void* const*>(image + StateManagerRva);
    auto* const expectedStateObject = image + StateObjectRva;
    auto* const expectedVtable = image + StateObjectVtableRva;
    auto* const initializerAddress = image + NativeInitializerRva;

    if (!IsRangeAccessible(
            currentState,
            sizeof(*currentState),
            false) ||
        !IsRangeAccessible(
            registeredStateObject,
            sizeof(*registeredStateObject),
            false) ||
        !IsRangeAccessible(
            stateManager,
            sizeof(*stateManager),
            false) ||
        !IsRangeAccessible(
            expectedStateObject,
            sizeof(void*),
            false) ||
        !IsRangeAccessible(
            initializerAddress,
            sizeof(NativeInitializerPrefix),
            true)) {
        return AvatarPreloadResult::NotInvoked;
    }

    void* observedManager = nullptr;
    __try {
        auto** observedVtable =
            *reinterpret_cast<void***>(expectedStateObject);
        if (*currentState != CharacterSelectionState ||
            *registeredStateObject != expectedStateObject ||
            observedVtable !=
                reinterpret_cast<void**>(expectedVtable) ||
            observedVtable[2] != initializerAddress) {
            return AvatarPreloadResult::NotInvoked;
        }
        observedManager = *stateManager;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return AvatarPreloadResult::NotInvoked;
    }

    if (!HasUsableStateManager(image, observedManager)) {
        return AvatarPreloadResult::NotInvoked;
    }

    __try {
        using NativeInitializer = void (__thiscall*)(void*);
        const auto initialize =
            reinterpret_cast<NativeInitializer>(initializerAddress);
        initialize(expectedStateObject);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return AvatarPreloadResult::InvokedNotReady;
    }

    return AreOriginAvatarResourcesReady()
        ? AvatarPreloadResult::Ready
        : AvatarPreloadResult::InvokedNotReady;
}

bool IsAfterLoginBootstrapMessage(const void* message) noexcept {
    constexpr std::size_t PacketOffset = sizeof(void*);
    constexpr std::size_t HashTerminatorOffset = 40;
    constexpr std::size_t VersionDigitOneOffset = 41;
    constexpr std::size_t VersionDigitTwoOffset = 42;
    constexpr std::size_t VersionTerminatorOffset = 43;
    constexpr std::size_t RequiredSize =
        PacketOffset + AfterLoginRecordLength;

    if (!IsRangeAccessible(message, RequiredSize, false)) {
        return false;
    }

    __try {
        const auto* packet =
            static_cast<const std::uint8_t*>(message) + PacketOffset;
        std::uint16_t length = 0;
        std::uint16_t opcode = 0;
        std::memcpy(&length, packet, sizeof(length));
        std::memcpy(&opcode, packet + sizeof(length), sizeof(opcode));

        return length == AfterLoginRecordLength &&
            opcode == AfterLoginOpcode &&
            packet[HashTerminatorOffset] == 0 &&
            packet[VersionDigitOneOffset] == '8' &&
            packet[VersionDigitTwoOffset] == '8' &&
            packet[VersionTerminatorOffset] == 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool IsCharacterPreviewMessage(const void* message) noexcept {
    constexpr std::size_t RequiredHeaderSize =
        sizeof(void*) +
        sizeof(std::uint16_t) +
        sizeof(std::uint16_t) +
        sizeof(std::uint8_t);
    if (!IsRangeAccessible(message, RequiredHeaderSize, false)) {
        return false;
    }

    __try {
        const auto* bytes =
            static_cast<const std::uint8_t*>(message);
        std::uint16_t length = 0;
        std::uint16_t opcode = 0;
        std::memcpy(&length, bytes + sizeof(void*), sizeof(length));
        std::memcpy(
            &opcode,
            bytes + sizeof(void*) + sizeof(length),
            sizeof(opcode));

        return length == CharacterPreviewLength &&
            opcode == CharacterPreviewOpcode &&
            bytes[
                sizeof(void*) +
                sizeof(length) +
                sizeof(opcode)] == CharacterPreviewCount;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void DestroyLegacyMessage(void* message) noexcept {
    if (!IsRangeAccessible(message, sizeof(void*), false)) {
        return;
    }

    __try {
        auto** vtable = *static_cast<void***>(message);
        if (!IsRangeAccessible(vtable, sizeof(void*), false) ||
            !IsRangeAccessible(vtable[0], 1, true)) {
            return;
        }

        using ScalarDeletingDestructor =
            void (__thiscall*)(void*, unsigned int);
        const auto destroy =
            reinterpret_cast<ScalarDeletingDestructor>(vtable[0]);
        destroy(message, 1);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        // A malformed object is leaked rather than risking a client crash.
    }
}

} // namespace godswar::network
