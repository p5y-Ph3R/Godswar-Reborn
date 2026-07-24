#include "AvatarPreviewGate.h"
#include "FileSha256.h"

#include <Windows.h>

#include <climits>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace {

constexpr std::uintptr_t OriginImageBase = 0x00400000;
constexpr DWORD OriginTimestamp = 0x52AA79CA;
constexpr DWORD OriginImageSize = 0x011DE000;
constexpr DWORD OriginEntryPointRva = 0x003BF68D;

constexpr std::uint8_t SupportedOriginSha256[] = {
    0xE0, 0xF5, 0xBC, 0x95, 0x1C, 0x6E, 0x37, 0x55,
    0x0F, 0x4D, 0x9C, 0xC1, 0xE2, 0x5B, 0xFD, 0xCB,
    0x4F, 0x02, 0x04, 0x66, 0xAD, 0xD8, 0x54, 0xDC,
    0x2E, 0x7E, 0xA0, 0x4E, 0x0D, 0x22, 0xF8, 0x1C,
};

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
constexpr std::uint8_t SecondaryGuardHook[] = {
    0xE9, 0x59, 0x2D, 0x3D, 0x00, 0x90,
};
constexpr std::uint8_t PrimaryGuardHook[] = {
    0xE9, 0x29, 0xE8, 0x3C, 0x00, 0x90,
};
constexpr std::uint8_t AvatarPreloadHook[] = {
    0xE9, 0x8B, 0x1E, 0x50, 0x00,
};
constexpr std::uint8_t AvatarPreloadCave[] = {
    0x9C, 0x60, 0x83, 0x3D, 0x4C, 0x5F, 0x57, 0x01,
    0x02, 0x75, 0x5C, 0x81, 0x3D, 0x6C, 0x5F, 0x57,
    0x01, 0x04, 0x5A, 0x9E, 0x00, 0x75, 0x50, 0x83,
    0x3D, 0x48, 0x5F, 0x57, 0x01, 0x00, 0x74, 0x47,
    0xB9, 0x04, 0x5A, 0x9E, 0x00, 0xE8, 0xF0, 0x3E,
    0xAA, 0xFF, 0xA1, 0x88, 0x60, 0x57, 0x01, 0x85,
    0xC0, 0x74, 0x34, 0xA1, 0x8C, 0x60, 0x57, 0x01,
    0x85, 0xC0, 0x74, 0x2B, 0xA1, 0x90, 0x60, 0x57,
    0x01, 0x85, 0xC0, 0x74, 0x22, 0xA1, 0x9C, 0x60,
    0x57, 0x01, 0x85, 0xC0, 0x74, 0x19, 0xA1, 0xA0,
    0x60, 0x57, 0x01, 0x85, 0xC0, 0x74, 0x10, 0xA1,
    0xA4, 0x60, 0x57, 0x01, 0x85, 0xC0, 0x74, 0x07,
    0xC6, 0x05, 0x70, 0x5F, 0x57, 0x01, 0x01, 0x61,
    0x9D, 0x68, 0xA0, 0x39, 0x95, 0x00, 0xE9, 0x02,
    0xE1, 0xAF, 0xFF,
};
constexpr std::uint8_t AvatarTimeoutGuardHook[] = {
    0xE9, 0x64, 0xDB, 0x3C, 0x00, 0x90,
};
constexpr std::uint8_t AvatarTimeoutGuardCave[] = {
    0xA1, 0x88, 0x60, 0x57, 0x01, 0x85, 0xC0, 0x74,
    0x38, 0xA1, 0x8C, 0x60, 0x57, 0x01, 0x85, 0xC0,
    0x74, 0x2F, 0xA1, 0x90, 0x60, 0x57, 0x01, 0x85,
    0xC0, 0x74, 0x26, 0xA1, 0x9C, 0x60, 0x57, 0x01,
    0x85, 0xC0, 0x74, 0x1D, 0xA1, 0xA0, 0x60, 0x57,
    0x01, 0x85, 0xC0, 0x74, 0x14, 0xA1, 0xA4, 0x60,
    0x57, 0x01, 0x85, 0xC0, 0x74, 0x0B, 0x8B, 0x0D,
    0xA0, 0x60, 0x57, 0x01, 0xE9, 0x5C, 0x24, 0xC3,
    0xFF, 0xBF, 0x02, 0x00, 0x00, 0x00, 0xC6, 0x05,
    0x66, 0x5C, 0x57, 0x01, 0x01, 0x89, 0x3D, 0x50,
    0x5F, 0x57, 0x01, 0xE9, 0x73, 0x24, 0xC3, 0xFF,
};

constexpr std::uintptr_t LifecycleHookRva = 0x000C14C5;
constexpr std::uintptr_t SecondaryGuardHookRva = 0x001F05C2;
constexpr std::uintptr_t PrimaryGuardHookRva = 0x001F4A82;
constexpr std::uintptr_t AvatarPreloadHookRva = 0x000C14D6;
constexpr std::uintptr_t AvatarPreloadCaveRva = 0x005C3366;
constexpr std::uintptr_t AvatarTimeoutGuardHookRva = 0x001F58B6;
constexpr std::uintptr_t AvatarTimeoutGuardCaveRva = 0x005C341F;
constexpr std::uintptr_t CurrentStateRva = 0x01175F4C;
constexpr std::uintptr_t PendingStateRva = 0x01175F50;
constexpr std::uintptr_t TransitionLatchRva = 0x01175C66;
constexpr std::int32_t CharacterSelectionState = 2;
constexpr std::uint16_t AfterLoginRecordLength = 44;
constexpr std::uint16_t AfterLoginOpcode = 0x2876;
constexpr std::uint16_t CharacterPreviewLength = 188;
constexpr std::uint16_t CharacterPreviewOpcode = 0x2712;
constexpr std::uint8_t CharacterPreviewCount = 1;

static_assert(sizeof(AvatarPreloadCave) == 115);
static_assert(sizeof(AvatarTimeoutGuardCave) == 88);

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

bool HasWritableProtection(DWORD protection) noexcept {
    if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
        return false;
    }

    switch (protection & 0xFF) {
        case PAGE_READWRITE:
        case PAGE_WRITECOPY:
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
    if (start < regionStart ||
        start > regionEnd ||
        length > regionEnd - start) {
        return false;
    }

    return requireExecutable
        ? HasExecutableProtection(memory.Protect)
        : HasReadableProtection(memory.Protect);
}

bool IsRangeWritable(const void* address, std::size_t length) noexcept {
    if (address == nullptr || length == 0) {
        return false;
    }

    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) == 0 ||
        memory.State != MEM_COMMIT ||
        !HasWritableProtection(memory.Protect)) {
        return false;
    }

    const auto start = reinterpret_cast<std::uintptr_t>(address);
    const auto regionStart =
        reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
    const auto regionEnd = regionStart + memory.RegionSize;
    return start >= regionStart &&
        start <= regionEnd &&
        length <= regionEnd - start;
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

} // namespace

namespace godswar::network {

AvatarPreviewGate::AvatarPreviewGate() noexcept
    : AvatarPreviewGate(
        IsSupportedOriginAvatarHost(),
        AreOriginAvatarResourcesReady,
        DestroyLegacyMessage,
        RequestOriginAvatarPreload) {
}

AvatarPreviewGate::AvatarPreviewGate(
    bool enabled,
    AvatarReadinessProbe readinessProbe,
    LegacyMessageDisposer messageDisposer,
    AvatarPreloadRequester preloadRequester) noexcept
    : enabled_(
          enabled &&
          readinessProbe != nullptr &&
          messageDisposer != nullptr),
      readinessProbe_(readinessProbe),
      messageDisposer_(messageDisposer),
      preloadRequester_(preloadRequester),
      preloadRequested_(false),
      heldMessage_(nullptr) {
}

AvatarPreviewGate::~AvatarPreviewGate() noexcept {
    Reset();
}

bool AvatarPreviewGate::IsHolding() const noexcept {
    return heldMessage_ != nullptr;
}

void* AvatarPreviewGate::Filter(void* message) noexcept {
    if (!enabled_) {
        return message;
    }

    if (IsAfterLoginBootstrapMessage(message)) {
        TryRequestPreload();
        return message;
    }

    if (heldMessage_ != nullptr ||
        !IsCharacterPreviewMessage(message) ||
        readinessProbe_()) {
        return message;
    }

    TryRequestPreload();
    heldMessage_ = message;
    return nullptr;
}

void* AvatarPreviewGate::TryRelease() noexcept {
    if (heldMessage_ == nullptr) {
        return nullptr;
    }

    if (!readinessProbe_()) {
        TryRequestPreload();
        return nullptr;
    }

    auto* message = heldMessage_;
    heldMessage_ = nullptr;
    return message;
}

long AvatarPreviewGate::AdjustMessageCount(long legacyCount) const noexcept {
    if (heldMessage_ == nullptr ||
        legacyCount < 0 ||
        legacyCount == LONG_MAX) {
        return legacyCount;
    }

    return legacyCount + 1;
}

void AvatarPreviewGate::Reset() noexcept {
    auto* message = heldMessage_;
    heldMessage_ = nullptr;
    preloadRequested_ = false;
    if (message != nullptr && messageDisposer_ != nullptr) {
        messageDisposer_(message);
    }
}

void AvatarPreviewGate::TryRequestPreload() noexcept {
    if (!preloadRequested_ &&
        preloadRequester_ != nullptr &&
        preloadRequester_()) {
        preloadRequested_ = true;
    }
}

bool IsSupportedOriginAvatarHost() noexcept {
    const auto module = GetModuleHandleW(nullptr);
    if (module == nullptr) {
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
    return HasSupportedPeIdentity(image) &&
        FileMatchesSha256(
            path,
            SupportedOriginSha256,
            sizeof(SupportedOriginSha256)) &&
        HasBytes(
            image,
            LifecycleHookRva,
            LifecycleHook,
            sizeof(LifecycleHook)) &&
        HasBytes(
            image,
            SecondaryGuardHookRva,
            SecondaryGuardHook,
            sizeof(SecondaryGuardHook)) &&
        HasBytes(
            image,
            PrimaryGuardHookRva,
            PrimaryGuardHook,
            sizeof(PrimaryGuardHook)) &&
        HasBytes(
            image,
            AvatarPreloadHookRva,
            AvatarPreloadHook,
            sizeof(AvatarPreloadHook)) &&
        HasBytes(
            image,
            AvatarPreloadCaveRva,
            AvatarPreloadCave,
            sizeof(AvatarPreloadCave)) &&
        HasBytes(
            image,
            AvatarTimeoutGuardHookRva,
            AvatarTimeoutGuardHook,
            sizeof(AvatarTimeoutGuardHook)) &&
        HasBytes(
            image,
            AvatarTimeoutGuardCaveRva,
            AvatarTimeoutGuardCave,
            sizeof(AvatarTimeoutGuardCave));
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

bool RequestOriginAvatarPreload() noexcept {
    if (AreOriginAvatarResourcesReady()) {
        return true;
    }

    const auto module = GetModuleHandleW(nullptr);
    if (reinterpret_cast<std::uintptr_t>(module) != OriginImageBase) {
        return false;
    }

    auto* const image = reinterpret_cast<std::uint8_t*>(module);
    auto* const currentState = reinterpret_cast<const std::int32_t*>(
        image + CurrentStateRva);
    auto* const pendingState = reinterpret_cast<std::int32_t*>(
        image + PendingStateRva);
    auto* const transitionLatch = reinterpret_cast<std::uint8_t*>(
        image + TransitionLatchRva);

    if (!IsRangeAccessible(
            currentState,
            sizeof(*currentState),
            false) ||
        !IsRangeAccessible(
            pendingState,
            sizeof(*pendingState),
            false) ||
        !IsRangeWritable(
            pendingState,
            sizeof(*pendingState)) ||
        !IsRangeAccessible(
            transitionLatch,
            sizeof(*transitionLatch),
            false) ||
        !IsRangeWritable(
            transitionLatch,
            sizeof(*transitionLatch))) {
        return false;
    }

    __try {
        const auto observedCurrentState = *currentState;
        const auto observedPendingState = *pendingState;
        const auto transitionPending = *transitionLatch;
        // A same-state request is intentional recovery: it reruns the
        // character-selection registration hook when its resources are absent.
        static_cast<void>(observedCurrentState);

        if (transitionPending != 0) {
            return observedPendingState == CharacterSelectionState;
        }

        *pendingState = CharacterSelectionState;
        MemoryBarrier();
        *transitionLatch = 1;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
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
