#include "OriginWarehousePageHost.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <new>

namespace {

constexpr std::uintptr_t OriginImageBase = 0x00400000;
constexpr DWORD OriginTimestamp = 0x52AA79CA;
constexpr DWORD OriginImageSize = 0x011DE000;
constexpr DWORD OriginEntryPointRva = 0x003BF68D;
constexpr std::uintptr_t StorageUiSingletonRva = 0x011763B8;
constexpr std::uintptr_t DragManagerSingletonRva = 0x01176304;
constexpr std::uintptr_t LocalPlayerSingletonRva = 0x01175EAC;
constexpr std::uintptr_t StorageUpdatePageMethodRva = 0x00220A40;
constexpr std::uintptr_t StorageUpdatePageStartRva = 0x00220AEA;
constexpr std::uintptr_t StorageUpdatePageEndRva = 0x00220D79;
constexpr std::uintptr_t NativeItemClearRva = 0x00034F30;
constexpr std::size_t StorageTabBarOffset = 0x348;
constexpr std::size_t StorageSelectedPageOffset = 0x358;
constexpr std::size_t TabBarSelectPageMethodOffset = 0xEC;
constexpr std::size_t PlayerStorageItemsOffset = 0x8108;
constexpr std::size_t NativeItemBytes = 0xF8;
constexpr std::size_t NativeItemClearMarkerOffset = 0xF4;
constexpr std::size_t DragSourceTypeOffset = 0x14;
constexpr std::size_t DragPayloadOffset = 0x20;
constexpr int WarehouseDragSourceType = 3;
constexpr int WarehousePageCount = 9;
constexpr char WarehouseAssetMarker[] =
    "Reborn logical warehouse pages v2";
constexpr wchar_t WarehouseXmlRelativePath[] =
    L"Localization\\en_us\\UI\\XML\\StorageUI.xml";

INIT_ONCE RuntimePatchOnce = INIT_ONCE_STATIC_INIT;
bool RuntimePatched = false;

#if defined(_M_IX86)
__declspec(naked) void InvokeNativeItemClear(void*, void*) noexcept {
    __asm {
        push esi
        mov esi, dword ptr [esp + 8]
        mov eax, dword ptr [esp + 12]
        call eax
        pop esi
        ret
    }
}
#endif

bool HasReadableProtection(DWORD protection) noexcept {
    if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
        return false;
    }
    switch (protection & 0xFF) {
        case PAGE_READONLY:
        case PAGE_READWRITE:
        case PAGE_WRITECOPY:
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
    std::size_t bytes,
    bool executable) noexcept {
    if (address == nullptr || bytes == 0) {
        return false;
    }
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) == 0 ||
        memory.State != MEM_COMMIT ||
        !HasReadableProtection(memory.Protect)) {
        return false;
    }
    if (executable &&
        (memory.Protect & 0xF0) != PAGE_EXECUTE &&
        (memory.Protect & 0xF0) != PAGE_EXECUTE_READ &&
        (memory.Protect & 0xF0) != PAGE_EXECUTE_READWRITE &&
        (memory.Protect & 0xF0) != PAGE_EXECUTE_WRITECOPY) {
        return false;
    }
    const auto start = reinterpret_cast<std::uintptr_t>(address);
    const auto base = reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
    return start >= base &&
        bytes <= memory.RegionSize - (start - base);
}

bool HasBytes(
    const std::uint8_t* image,
    std::uintptr_t rva,
    const void* expected,
    std::size_t bytes) noexcept {
    const auto* address = image + rva;
    return IsRangeAccessible(address, bytes, false) &&
        std::memcmp(address, expected, bytes) == 0;
}

bool HasSupportedPeIdentity(const std::uint8_t* image) noexcept {
    if (reinterpret_cast<std::uintptr_t>(image) != OriginImageBase ||
        !IsRangeAccessible(image, sizeof(IMAGE_DOS_HEADER), false)) {
        return false;
    }
    __try {
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE ||
            dos->e_lfanew < static_cast<LONG>(sizeof(*dos))) {
            return false;
        }
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(
            image + dos->e_lfanew);
        return IsRangeAccessible(nt, sizeof(*nt), false) &&
            nt->Signature == IMAGE_NT_SIGNATURE &&
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

bool HasWarehousePageAssets(HMODULE module) noexcept {
    wchar_t path[4096]{};
    const auto length = GetModuleFileNameW(
        module, path, static_cast<DWORD>(sizeof(path) / sizeof(path[0])));
    if (length == 0 || length >= sizeof(path) / sizeof(path[0])) {
        return false;
    }
    std::size_t separator = length;
    while (separator > 0 && path[separator - 1] != L'\\' &&
           path[separator - 1] != L'/') {
        --separator;
    }
    const auto suffixCharacters =
        sizeof(WarehouseXmlRelativePath) / sizeof(wchar_t);
    if (separator + suffixCharacters > sizeof(path) / sizeof(path[0])) {
        return false;
    }
    std::memcpy(
        path + separator,
        WarehouseXmlRelativePath,
        sizeof(WarehouseXmlRelativePath));

    const HANDLE file = CreateFileW(
        path,
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    LARGE_INTEGER lengthBytes{};
    bool found = false;
    if (GetFileSizeEx(file, &lengthBytes) &&
        lengthBytes.QuadPart > 0 &&
        lengthBytes.QuadPart <= 1024 * 1024) {
        const auto bytes = static_cast<DWORD>(lengthBytes.QuadPart);
        auto* content = new (std::nothrow) char[bytes];
        if (content != nullptr) {
            DWORD read = 0;
            if (ReadFile(file, content, bytes, &read, nullptr) &&
                read == bytes) {
                constexpr std::size_t markerBytes =
                    sizeof(WarehouseAssetMarker) - 1;
                for (std::size_t offset = 0;
                     offset + markerBytes <= bytes;
                     ++offset) {
                    if (std::memcmp(
                            content + offset,
                            WarehouseAssetMarker,
                            markerBytes) == 0) {
                        found = true;
                        break;
                    }
                }
            }
            delete[] content;
        }
    }
    CloseHandle(file);
    return found;
}

bool WriteCode(
    std::uint8_t* address,
    const std::uint8_t* bytes,
    std::size_t byteCount) noexcept {
    DWORD previous = 0;
    if (!VirtualProtect(
            address, byteCount, PAGE_EXECUTE_READWRITE, &previous)) {
        return false;
    }
    std::memcpy(address, bytes, byteCount);
    const bool flushed = FlushInstructionCache(
        GetCurrentProcess(), address, byteCount) != FALSE;
    DWORD ignored = 0;
    const bool restored =
        VirtualProtect(address, byteCount, previous, &ignored) != FALSE;
    return flushed && restored;
}

bool InstallRuntimePatches(std::uint8_t* image) noexcept {
    const std::uint8_t updateStartExpected[]{
        0x8B, 0x87, 0x58, 0x03, 0x00, 0x00};
    const std::uint8_t updateEndExpected[]{
        0x8B, 0x82, 0x58, 0x03, 0x00, 0x00};
    const std::uint8_t updateReplacement[]{0x31, 0xC0, 0x90, 0x90, 0x90, 0x90};
    const bool startPatched = HasBytes(
        image,
        StorageUpdatePageStartRva,
        updateReplacement,
        sizeof(updateReplacement));
    const bool endPatched = HasBytes(
        image,
        StorageUpdatePageEndRva,
        updateReplacement,
        sizeof(updateReplacement));
    if ((!startPatched && !HasBytes(
            image,
            StorageUpdatePageStartRva,
            updateStartExpected,
            sizeof(updateStartExpected))) ||
        (!endPatched && !HasBytes(
            image,
            StorageUpdatePageEndRva,
            updateEndExpected,
            sizeof(updateEndExpected)))) {
        return false;
    }
    if (!startPatched && !WriteCode(
            image + StorageUpdatePageStartRva,
            updateReplacement,
            sizeof(updateReplacement))) {
        return false;
    }
    return endPatched || WriteCode(
        image + StorageUpdatePageEndRva,
        updateReplacement,
        sizeof(updateReplacement));
}

BOOL CALLBACK InitializeRuntimePatch(
    PINIT_ONCE,
    PVOID,
    PVOID*) noexcept {
    const auto module = GetModuleHandleW(nullptr);
    auto* image = reinterpret_cast<std::uint8_t*>(module);
    RuntimePatched = module != nullptr &&
        HasSupportedPeIdentity(image) &&
        HasWarehousePageAssets(module) &&
        InstallRuntimePatches(image);
    return TRUE;
}

void* TryGetStorageUi() noexcept {
    const auto module = GetModuleHandleW(nullptr);
    if (reinterpret_cast<std::uintptr_t>(module) != OriginImageBase) {
        return nullptr;
    }
    auto* slot = reinterpret_cast<void**>(
        reinterpret_cast<std::uint8_t*>(module) + StorageUiSingletonRva);
    if (!IsRangeAccessible(slot, sizeof(*slot), false)) {
        return nullptr;
    }
    __try {
        return *slot;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

void* TryGetDragManager() noexcept {
    const auto module = GetModuleHandleW(nullptr);
    if (reinterpret_cast<std::uintptr_t>(module) != OriginImageBase) {
        return nullptr;
    }
    auto* slot = reinterpret_cast<void**>(
        reinterpret_cast<std::uint8_t*>(module) +
        DragManagerSingletonRva);
    if (!IsRangeAccessible(slot, sizeof(*slot), false)) {
        return nullptr;
    }
    __try {
        return *slot;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

void* TryGetLocalPlayer() noexcept {
    const auto module = GetModuleHandleW(nullptr);
    if (reinterpret_cast<std::uintptr_t>(module) != OriginImageBase) {
        return nullptr;
    }
    auto* slot = reinterpret_cast<void**>(
        reinterpret_cast<std::uint8_t*>(module) +
        LocalPlayerSingletonRva);
    if (!IsRangeAccessible(slot, sizeof(*slot), false)) {
        return nullptr;
    }
    __try {
        return *slot;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

} // namespace

namespace godswar::network::warehouse_page_host_detail {

bool PrepareRuntimePatchOnLoad() noexcept {
    const auto module = GetModuleHandleW(nullptr);
    auto* image = reinterpret_cast<std::uint8_t*>(module);
    return reinterpret_cast<std::uintptr_t>(image) == OriginImageBase &&
        InstallRuntimePatches(image);
}

bool EnsureRuntimePatched() noexcept {
    if (!InitOnceExecuteOnce(
            &RuntimePatchOnce,
            InitializeRuntimePatch,
            nullptr,
            nullptr)) {
        return false;
    }
    if (!PrepareRuntimePatchOnLoad()) {
        return false;
    }
    if (RuntimePatched) {
        return true;
    }
    const auto module = GetModuleHandleW(nullptr);
    auto* image = reinterpret_cast<std::uint8_t*>(module);
    return module != nullptr &&
        HasSupportedPeIdentity(image) &&
        HasWarehousePageAssets(module);
}

bool TryReadSelectedPage(int* page) noexcept {
    if (page == nullptr || !RuntimePatched) {
        return false;
    }
    *page = -1;
    auto* ui = static_cast<std::uint8_t*>(TryGetStorageUi());
    if (!IsRangeAccessible(
            ui, StorageSelectedPageOffset + sizeof(int), false)) {
        return false;
    }
    __try {
        const auto selected = *reinterpret_cast<int*>(
            ui + StorageSelectedPageOffset);
        if (selected < 0 || selected >= WarehousePageCount) {
            return false;
        }
        *page = selected;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool TrySelectPage(int page) noexcept {
    if (!RuntimePatched || page < 0 || page >= WarehousePageCount) {
        return false;
    }
    auto* ui = static_cast<std::uint8_t*>(TryGetStorageUi());
    if (!IsRangeAccessible(
            ui, StorageSelectedPageOffset + sizeof(int), false)) {
        return false;
    }
    __try {
        auto* tabBar = *reinterpret_cast<void**>(ui + StorageTabBarOffset);
        if (!IsRangeAccessible(tabBar, sizeof(void*), false)) {
            return false;
        }
        auto* vtable = *reinterpret_cast<std::uint8_t**>(tabBar);
        if (!IsRangeAccessible(
                vtable,
                TabBarSelectPageMethodOffset + sizeof(void*),
                false)) {
            return false;
        }
        auto* methodAddress = *reinterpret_cast<void**>(
            vtable + TabBarSelectPageMethodOffset);
        if (!IsRangeAccessible(methodAddress, 1, true)) {
            return false;
        }
        using SelectPageMethod = void(__thiscall*)(void*, int);
        auto selectPage = reinterpret_cast<SelectPageMethod>(methodAddress);
        *reinterpret_cast<int*>(ui + StorageSelectedPageOffset) = page;
        selectPage(tabBar, page);
        *reinterpret_cast<int*>(ui + StorageSelectedPageOffset) = page;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool TryClearProjectedStorageChunk(
    int firstSlot,
    int slotCount) noexcept {
#if !defined(_M_IX86)
    static_cast<void>(firstSlot);
    static_cast<void>(slotCount);
    return false;
#else
    if (!RuntimePatched || firstSlot < 0 || firstSlot >= 40 ||
        slotCount <= 0 || slotCount > 40 - firstSlot) {
        return false;
    }
    const auto module = GetModuleHandleW(nullptr);
    auto* image = reinterpret_cast<std::uint8_t*>(module);
    auto* player = static_cast<std::uint8_t*>(TryGetLocalPlayer());
    const std::uint8_t itemClearExpected[]{
        0x53, 0x6A, 0x48, 0x33, 0xDB, 0x8D, 0x46, 0x30};
    if (module == nullptr || player == nullptr ||
        !HasBytes(
            image,
            NativeItemClearRva,
            itemClearExpected,
            sizeof(itemClearExpected))) {
        return false;
    }
    auto* clearMethod = image + NativeItemClearRva;
    auto* firstItem = player + PlayerStorageItemsOffset +
        (firstSlot * NativeItemBytes);
    const auto regionBytes = static_cast<std::size_t>(slotCount) *
        NativeItemBytes;
    if (!IsRangeAccessible(
            clearMethod, sizeof(itemClearExpected), true) ||
        !IsRangeAccessible(firstItem, regionBytes, false)) {
        return false;
    }
    __try {
        for (int offset = 0; offset < slotCount; ++offset) {
            auto* item = firstItem + (offset * NativeItemBytes);
            InvokeNativeItemClear(item, clearMethod);
            if (*reinterpret_cast<std::uint32_t*>(
                    item + NativeItemClearMarkerOffset) != 0) {
                return false;
            }
        }
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
#endif
}

bool TryShowEmptyProjectedStoragePage() noexcept {
#if !defined(_M_IX86)
    return false;
#else
    if (!TryClearProjectedStorageChunk(0, 40)) {
        return false;
    }
    const auto module = GetModuleHandleW(nullptr);
    auto* image = reinterpret_cast<std::uint8_t*>(module);
    auto* ui = static_cast<std::uint8_t*>(TryGetStorageUi());
    const std::uint8_t updateExpected[]{
        0x6A, 0xFF, 0x68, 0xA6, 0xF6, 0x7F, 0x00,
        0x64, 0xA1, 0x00, 0x00, 0x00, 0x00};
    if (module == nullptr ||
        !HasBytes(
            image,
            StorageUpdatePageMethodRva,
            updateExpected,
            sizeof(updateExpected)) ||
        !IsRangeAccessible(
            ui, StorageSelectedPageOffset + sizeof(int), false)) {
        return false;
    }
    auto* updateAddress = image + StorageUpdatePageMethodRva;
    if (!IsRangeAccessible(
            updateAddress, sizeof(updateExpected), true)) {
        return false;
    }
    __try {
        using UpdatePageMethod = void(__thiscall*)(void*);
        auto updatePage = reinterpret_cast<UpdatePageMethod>(updateAddress);
        updatePage(ui);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
#endif
}

bool IsWarehouseDragActive() noexcept {
    if (!RuntimePatched) {
        return false;
    }
    auto* drag = static_cast<std::uint8_t*>(TryGetDragManager());
    if (!IsRangeAccessible(
            drag, DragPayloadOffset + sizeof(void*), false)) {
        return false;
    }
    __try {
        return *reinterpret_cast<int*>(drag + DragSourceTypeOffset) ==
                WarehouseDragSourceType &&
            *reinterpret_cast<void**>(drag + DragPayloadOffset) != nullptr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

} // namespace godswar::network::warehouse_page_host_detail
