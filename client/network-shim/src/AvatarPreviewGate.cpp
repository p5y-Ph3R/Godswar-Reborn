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
    0x75, 0x3B, 0xE4, 0x9F, 0xE9, 0x4B, 0x6F, 0x4C,
    0x0E, 0x33, 0x29, 0xBC, 0x89, 0x05, 0x94, 0x5B,
    0xD9, 0xB0, 0xF1, 0xA7, 0x90, 0xB4, 0xB9, 0x03,
    0x8E, 0x69, 0xC2, 0xA5, 0xAD, 0x49, 0xED, 0x79,
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

constexpr std::uintptr_t LifecycleHookRva = 0x000C14C5;
constexpr std::uintptr_t SecondaryGuardHookRva = 0x001F05C2;
constexpr std::uintptr_t PrimaryGuardHookRva = 0x001F4A82;
constexpr std::uint16_t CharacterPreviewLength = 188;
constexpr std::uint16_t CharacterPreviewOpcode = 0x2712;
constexpr std::uint8_t CharacterPreviewCount = 1;

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
    if (start < regionStart ||
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

} // namespace

namespace godswar::network {

AvatarPreviewGate::AvatarPreviewGate() noexcept
    : AvatarPreviewGate(
        IsSupportedOriginAvatarHost(),
        AreOriginAvatarResourcesReady,
        DestroyLegacyMessage,
        ReadAvatarMonotonicMilliseconds,
        AvatarPreviewWaitTimeoutMilliseconds) {
}

AvatarPreviewGate::AvatarPreviewGate(
    bool enabled,
    AvatarReadinessProbe readinessProbe,
    LegacyMessageDisposer messageDisposer,
    AvatarMonotonicClock monotonicClock,
    std::uint64_t waitTimeoutMilliseconds) noexcept
    : enabled_(
          enabled &&
          readinessProbe != nullptr &&
          messageDisposer != nullptr &&
          monotonicClock != nullptr &&
          waitTimeoutMilliseconds != 0),
      readinessProbe_(readinessProbe),
      messageDisposer_(messageDisposer),
      monotonicClock_(monotonicClock),
      waitTimeoutMilliseconds_(waitTimeoutMilliseconds),
      heldAtMilliseconds_(0),
      heldMessage_(nullptr) {
}

AvatarPreviewGate::~AvatarPreviewGate() noexcept {
    Reset();
}

bool AvatarPreviewGate::IsHolding() const noexcept {
    return heldMessage_ != nullptr;
}

bool AvatarPreviewGate::HasTimedOut() const noexcept {
    return heldMessage_ != nullptr &&
        !readinessProbe_() &&
        monotonicClock_() - heldAtMilliseconds_ >=
            waitTimeoutMilliseconds_;
}

void* AvatarPreviewGate::Filter(void* message) noexcept {
    if (!enabled_ ||
        heldMessage_ != nullptr ||
        !IsCharacterPreviewMessage(message) ||
        readinessProbe_()) {
        return message;
    }

    heldAtMilliseconds_ = monotonicClock_();
    heldMessage_ = message;
    return nullptr;
}

void* AvatarPreviewGate::TryRelease() noexcept {
    if (heldMessage_ == nullptr ||
        (!readinessProbe_() &&
         monotonicClock_() - heldAtMilliseconds_ <
             waitTimeoutMilliseconds_)) {
        return nullptr;
    }

    auto* message = heldMessage_;
    heldMessage_ = nullptr;
    heldAtMilliseconds_ = 0;
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
    heldAtMilliseconds_ = 0;
    if (message != nullptr && messageDisposer_ != nullptr) {
        messageDisposer_(message);
    }
}

std::uint64_t ReadAvatarMonotonicMilliseconds() noexcept {
    return GetTickCount64();
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
            sizeof(PrimaryGuardHook));
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
