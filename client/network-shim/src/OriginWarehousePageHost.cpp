#include "OriginWarehousePageHost.h"

#include <Windows.h>

#include <cstdint>
#include <cstring>

namespace godswar::network {
namespace {

constexpr std::uint16_t NpcDialogOpenOpcode = 10067;
constexpr std::uint16_t NpcDialogPageOpcode = 10068;
constexpr std::uint16_t NpcFunctionActionResponseOpcode = 10070;
constexpr std::uint16_t WarehouseSnapshotOpcode = 10034;
constexpr std::uint16_t WarehouseTransferOpcode = 10059;
constexpr std::uint32_t AthensWarehouseNpc = 5164;
constexpr std::uint32_t SpartaWarehouseNpc = 47750;
constexpr std::uint32_t AthensWarehouseManagerNpc = 5273;
constexpr std::uint32_t SpartaWarehouseManagerNpc = 5131;
constexpr std::uint32_t PageProjectionMarker = 0x57485000;
constexpr std::uint32_t PageProjectionMask = 0xFFFFFF00;
constexpr int PageProjectionBoxCountShift = 4;
constexpr std::size_t PageRequestBytes = 12;
constexpr std::size_t TransferPacketBytes = 20;
constexpr std::size_t SnapshotHeaderBytes = 24;
constexpr std::uint16_t PhysicalPageCapacity = 40;
constexpr int SnapshotSlotsPerChunk = 12;
constexpr int SnapshotSelectorStride = 2;
constexpr std::uint8_t PhysicalPageTailSelector = 6;
constexpr int WarehousePageCount = 9;
constexpr int NativeWarehousePageCount = 4;

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

bool IsReadable(const void* address, std::size_t bytes) noexcept {
    if (address == nullptr || bytes == 0) {
        return false;
    }
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) == 0 ||
        memory.State != MEM_COMMIT ||
        !HasReadableProtection(memory.Protect)) {
        return false;
    }
    const auto start = reinterpret_cast<std::uintptr_t>(address);
    const auto base = reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
    return start >= base &&
        bytes <= memory.RegionSize - (start - base);
}

bool IsWritable(void* address, std::size_t bytes) noexcept {
    if (address == nullptr || bytes == 0) {
        return false;
    }
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) == 0 ||
        memory.State != MEM_COMMIT ||
        !HasWritableProtection(memory.Protect)) {
        return false;
    }
    const auto start = reinterpret_cast<std::uintptr_t>(address);
    const auto base = reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
    return start >= base &&
        bytes <= memory.RegionSize - (start - base);
}

std::uint16_t Read16(const std::uint8_t* bytes) noexcept {
    return static_cast<std::uint16_t>(
        bytes[0] | (static_cast<std::uint16_t>(bytes[1]) << 8U));
}

std::uint32_t Read32(const std::uint8_t* bytes) noexcept {
    return bytes[0] |
        (static_cast<std::uint32_t>(bytes[1]) << 8U) |
        (static_cast<std::uint32_t>(bytes[2]) << 16U) |
        (static_cast<std::uint32_t>(bytes[3]) << 24U);
}

std::int16_t ReadSigned16(const std::uint8_t* bytes) noexcept {
    return static_cast<std::int16_t>(Read16(bytes));
}

void Write16(std::uint8_t* bytes, std::uint16_t value) noexcept {
    bytes[0] = static_cast<std::uint8_t>(value);
    bytes[1] = static_cast<std::uint8_t>(value >> 8U);
}

void Write32(std::uint8_t* bytes, std::uint32_t value) noexcept {
    bytes[0] = static_cast<std::uint8_t>(value);
    bytes[1] = static_cast<std::uint8_t>(value >> 8U);
    bytes[2] = static_cast<std::uint8_t>(value >> 16U);
    bytes[3] = static_cast<std::uint8_t>(value >> 24U);
}

void WriteSigned16(std::uint8_t* bytes, int value) noexcept {
    Write16(bytes, static_cast<std::uint16_t>(
        static_cast<std::int16_t>(value)));
}

bool IsPhysicalWarehouseSlot(int slot) noexcept {
    return slot >= 0 && slot < PhysicalPageCapacity;
}

bool IsWarehouseNpc(std::uint32_t npcId) noexcept {
    return npcId == AthensWarehouseNpc || npcId == SpartaWarehouseNpc;
}

bool IsRelatedManager(std::uint32_t npcId) noexcept {
    return npcId == AthensWarehouseManagerNpc ||
        npcId == SpartaWarehouseManagerNpc;
}

bool TryReadPacketHeader(
    const void* packet,
    int packetBytes,
    std::uint16_t* opcode,
    std::uint32_t* npcId) noexcept {
    if (packet == nullptr || packetBytes < 8 ||
        !IsReadable(packet, static_cast<std::size_t>(packetBytes))) {
        return false;
    }
    __try {
        const auto* bytes = static_cast<const std::uint8_t*>(packet);
        if (Read16(bytes) != packetBytes) {
            return false;
        }
        *opcode = Read16(bytes + 2);
        *npcId = Read32(bytes + 4);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool TryNormalizeSnapshotHeader(
    void* packetAddress,
    std::size_t packetBytes,
    int* page,
    int* unlockedPageCount,
    bool* isTail,
    int* firstSlot,
    int* slotCount) noexcept {
    if (page == nullptr || unlockedPageCount == nullptr ||
        isTail == nullptr || firstSlot == nullptr || slotCount == nullptr ||
        packetBytes < SnapshotHeaderBytes ||
        !IsReadable(packetAddress, SnapshotHeaderBytes)) {
        return false;
    }
    __try {
        auto* packet = static_cast<std::uint8_t*>(packetAddress);
        const auto length = Read16(packet);
        const auto marker = Read32(packet + 8);
        const auto decodedPage = static_cast<int>(marker & 0x0F);
        const auto decodedBoxCount = static_cast<int>(
            (marker >> PageProjectionBoxCountShift) & 0x0F);
        const auto nativeCapacity = static_cast<std::uint16_t>(
            (decodedBoxCount < NativeWarehousePageCount
                ? decodedBoxCount
                : NativeWarehousePageCount) * PhysicalPageCapacity);
        const auto advertisedCapacity = Read16(packet + 12);
        const auto selector = static_cast<int>(packet[14]);
        const auto decodedFirstSlot =
            (selector / SnapshotSelectorStride) * SnapshotSlotsPerChunk;
        const auto decodedSlotCount = decodedFirstSlot < PhysicalPageCapacity
            ? (PhysicalPageCapacity - decodedFirstSlot < SnapshotSlotsPerChunk
                ? PhysicalPageCapacity - decodedFirstSlot
                : SnapshotSlotsPerChunk)
            : 0;
        const auto allowedMask = static_cast<std::uint16_t>(
            (1U << decodedSlotCount) - 1U);
        if (length < SnapshotHeaderBytes ||
            Read16(packet + 2) != WarehouseSnapshotOpcode ||
            (marker & PageProjectionMask) != PageProjectionMarker ||
            decodedBoxCount < 1 || decodedBoxCount > 9 ||
            decodedPage < 0 || decodedPage >= decodedBoxCount ||
            selector > PhysicalPageTailSelector ||
            selector % SnapshotSelectorStride != 0 || packet[15] != 0 ||
            decodedSlotCount == 0 ||
            (Read16(packet + 16) & ~allowedMask) != 0 ||
            (advertisedCapacity != PhysicalPageCapacity &&
                advertisedCapacity != nativeCapacity) ||
            Read16(packet + 18) != 0) {
            return false;
        }
        if (advertisedCapacity != nativeCapacity) {
            if (!IsWritable(packet + 12, sizeof(std::uint16_t))) {
                return false;
            }
            Write16(packet + 12, nativeCapacity);
        }
        *page = decodedPage;
        *unlockedPageCount = decodedBoxCount;
        *isTail = selector == PhysicalPageTailSelector;
        *firstSlot = decodedFirstSlot;
        *slotCount = decodedSlotCount;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool TryReadSnapshotPage(
    void* message,
    int* page,
    int* unlockedPageCount,
    bool* isTail,
    int* firstSlot,
    int* slotCount) noexcept {
    if (!IsReadable(message, sizeof(void*) + SnapshotHeaderBytes)) {
        return false;
    }
    auto* packet = static_cast<std::uint8_t*>(message) + sizeof(void*);
    return TryNormalizeSnapshotHeader(
        packet,
        SnapshotHeaderBytes,
        page,
        unlockedPageCount,
        isTail,
        firstSlot,
        slotCount);
}

bool TryReadExpansionSuccess(
    const void* message,
    int* unlockedPageCount) noexcept {
    constexpr std::size_t ResultPacketBytes = 16;
    constexpr int ManagerDialogIndex = 2;
    constexpr int FirstSuccessSubId = 201;
    constexpr int LastSuccessSubId = 208;
    if (unlockedPageCount == nullptr ||
        !IsReadable(message, sizeof(void*) + ResultPacketBytes)) {
        return false;
    }
    __try {
        const auto* packet = static_cast<const std::uint8_t*>(message) +
            sizeof(void*);
        const auto result = static_cast<int>(Read32(packet + 12));
        if (Read16(packet) != ResultPacketBytes ||
            Read16(packet + 2) != NpcFunctionActionResponseOpcode ||
            !IsRelatedManager(Read32(packet + 4)) ||
            static_cast<int>(Read32(packet + 8)) != ManagerDialogIndex ||
            result < FirstSuccessSubId || result > LastSuccessSubId) {
            return false;
        }
        *unlockedPageCount = result - 199;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

} // namespace

OriginWarehousePageHost::OriginWarehousePageHost() noexcept
    : enabled_(warehouse_page_host_detail::EnsureRuntimePatched()) {
}

void OriginWarehousePageHost::Reset() noexcept {
    warehouseNpcId_ = 0;
    visiblePage_ = 0;
    unlockedPageCount_ = 1;
    pendingPage_ = -1;
    readySnapshotPage_ = -1;
    readyUnlockedPageCount_ = -1;
    dragSourcePage_ = -1;
    requestSent_ = false;
    snapshotInProgress_ = false;
}

bool OriginWarehousePageHost::TryRewriteClientPacket(
    const void* packet,
    int packetBytes,
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (!enabled_ || warehouseNpcId_ == 0) {
        return false;
    }
    const int sourcePage = dragSourcePage_ >= 0
        ? dragSourcePage_
        : visiblePage_;
    return warehouse_page_host_detail::RewriteTransferPacketForPages(
        packet,
        packetBytes,
        sourcePage,
        visiblePage_,
        destination,
        destinationBytes);
}

void OriginWarehousePageHost::ObserveClientPacket(
    const void* packet,
    int packetBytes) noexcept {
    std::uint16_t opcode = 0;
    std::uint32_t npcId = 0;
    if (!enabled_ ||
        !TryReadPacketHeader(packet, packetBytes, &opcode, &npcId)) {
        return;
    }
    if (opcode == WarehouseTransferOpcode &&
        packetBytes == TransferPacketBytes) {
        dragSourcePage_ = -1;
        return;
    }
    if (opcode == NpcDialogPageOpcode && packetBytes == 8) {
        if (IsWarehouseNpc(npcId)) {
            Reset();
            warehouseNpcId_ = npcId;
        } else if (!IsRelatedManager(npcId)) {
            Reset();
        }
    } else if (opcode == NpcDialogOpenOpcode &&
               !IsWarehouseNpc(npcId) &&
               !IsRelatedManager(npcId)) {
        Reset();
    }
}

void OriginWarehousePageHost::ObserveServerMessage(
    void* message) noexcept {
    if (!enabled_ || warehouseNpcId_ == 0) {
        return;
    }
    int expandedPageCount = -1;
    if (TryReadExpansionSuccess(message, &expandedPageCount)) {
        const int previousUnlockedPageCount = unlockedPageCount_;
        if (expandedPageCount > unlockedPageCount_) {
            unlockedPageCount_ = expandedPageCount;
        }
        if (visiblePage_ >= previousUnlockedPageCount &&
            visiblePage_ < unlockedPageCount_) {
            pendingPage_ = visiblePage_;
            requestSent_ = false;
        }
        if (pendingPage_ >= 0 && pendingPage_ < unlockedPageCount_) {
            requestSent_ = false;
        }
        return;
    }
    int page = -1;
    int unlockedPageCount = -1;
    int firstSlot = -1;
    int slotCount = 0;
    bool isTail = false;
    if (!TryReadSnapshotPage(
            message,
            &page,
            &unlockedPageCount,
            &isTail,
            &firstSlot,
            &slotCount)) {
        return;
    }
    static_cast<void>(warehouse_page_host_detail::
        TryClearProjectedStorageChunk(firstSlot, slotCount));
    snapshotInProgress_ = true;
    if (isTail) {
        readySnapshotPage_ = page;
        readyUnlockedPageCount_ = unlockedPageCount;
    }
}

bool OriginWarehousePageHost::TryBuildPageRequest(
    void* destination,
    std::size_t destinationBytes,
    int* requestBytes) noexcept {
    if (requestBytes != nullptr) {
        *requestBytes = 0;
    }
    enabled_ = warehouse_page_host_detail::EnsureRuntimePatched();
    if (!enabled_ || warehouseNpcId_ == 0 || requestBytes == nullptr) {
        return false;
    }

    if (readySnapshotPage_ >= 0) {
        if (readyUnlockedPageCount_ > unlockedPageCount_) {
            unlockedPageCount_ = readyUnlockedPageCount_;
        }
        // Stock Origin selects its first tab whenever a storage snapshot is
        // applied. Restore the logical tab only after the tail was delivered,
        // so that reset is not mistaken for a fresh SB-1 click.
        if (visiblePage_ != 0) {
            if (!warehouse_page_host_detail::TrySelectPage(visiblePage_)) {
                return false;
            }
            if (visiblePage_ >= unlockedPageCount_ &&
                !warehouse_page_host_detail::
                    TryShowEmptyProjectedStoragePage()) {
                return false;
            }
        }
        if (pendingPage_ == readySnapshotPage_) {
            pendingPage_ = -1;
            requestSent_ = false;
        }
        readySnapshotPage_ = -1;
        readyUnlockedPageCount_ = -1;
        snapshotInProgress_ = false;
    }

    if (dragSourcePage_ >= 0 &&
        !warehouse_page_host_detail::IsWarehouseDragActive()) {
        dragSourcePage_ = -1;
    }

    int nativePage = -1;
    if (!warehouse_page_host_detail::TryReadSelectedPage(&nativePage)) {
        return false;
    }
    if (nativePage != visiblePage_) {
        if (warehouse_page_host_detail::IsWarehouseDragActive()) {
            dragSourcePage_ = visiblePage_;
        }
        if (nativePage >= unlockedPageCount_) {
            if (!warehouse_page_host_detail::
                    TryShowEmptyProjectedStoragePage()) {
                return false;
            }
            visiblePage_ = nativePage;
            pendingPage_ = -1;
            requestSent_ = false;
            return false;
        }
        visiblePage_ = nativePage;
        pendingPage_ = nativePage;
        requestSent_ = false;
    }
    if (snapshotInProgress_) {
        return false;
    }
    if (pendingPage_ >= 0) {
        if (!requestSent_) {
            return BuildPendingRequest(
                destination, destinationBytes, requestBytes);
        }
    }
    return false;
}

bool OriginWarehousePageHost::BuildPendingRequest(
    void* destination,
    std::size_t destinationBytes,
    int* requestBytes) noexcept {
    if (destination == nullptr || destinationBytes < PageRequestBytes ||
        requestBytes == nullptr || pendingPage_ < 0 ||
        pendingPage_ >= unlockedPageCount_) {
        return false;
    }
    auto* bytes = static_cast<std::uint8_t*>(destination);
    std::memset(bytes, 0, PageRequestBytes);
    Write16(bytes, static_cast<std::uint16_t>(PageRequestBytes));
    Write16(bytes + 2, NpcDialogPageOpcode);
    Write32(bytes + 4, warehouseNpcId_);
    Write32(bytes + 8, static_cast<std::uint32_t>(pendingPage_));
    *requestBytes = static_cast<int>(PageRequestBytes);
    requestSent_ = true;
    return true;
}

void OriginWarehousePageHost::CompletePageRequestSend(bool sent) noexcept {
    if (!sent && pendingPage_ >= 0) {
        requestSent_ = false;
    }
}

namespace warehouse_page_host_detail {

bool NormalizeProjectedSnapshotHeader(
    void* packet,
    std::size_t packetBytes,
    int* page,
    int* unlockedPageCount,
    bool* isTail) noexcept {
    int firstSlot = -1;
    int slotCount = 0;
    return TryNormalizeSnapshotHeader(
        packet,
        packetBytes,
        page,
        unlockedPageCount,
        isTail,
        &firstSlot,
        &slotCount);
}

bool RewriteTransferPacketForPages(
    const void* packet,
    int packetBytes,
    int sourcePage,
    int destinationPage,
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (packet == nullptr || destination == nullptr ||
        packetBytes != TransferPacketBytes ||
        destinationBytes < TransferPacketBytes ||
        sourcePage < 0 || sourcePage >= WarehousePageCount ||
        destinationPage < 0 || destinationPage >= WarehousePageCount ||
        !IsReadable(packet, TransferPacketBytes)) {
        return false;
    }

    __try {
        const auto* source = static_cast<const std::uint8_t*>(packet);
        if (Read16(source) != TransferPacketBytes ||
            Read16(source + 2) != WarehouseTransferOpcode) {
            return false;
        }
        auto* rewritten = static_cast<std::uint8_t*>(destination);
        std::memcpy(rewritten, source, TransferPacketBytes);

        const int warehouseSlot = ReadSigned16(rewritten + 4);
        if (rewritten[16] == 1) {
            if (IsPhysicalWarehouseSlot(warehouseSlot)) {
                WriteSigned16(
                    rewritten + 4,
                    destinationPage * PhysicalPageCapacity + warehouseSlot);
            }
            return true;
        }
        if (rewritten[16] != 0) {
            return true;
        }

        if (IsPhysicalWarehouseSlot(warehouseSlot)) {
            WriteSigned16(
                rewritten + 4,
                sourcePage * PhysicalPageCapacity + warehouseSlot);
        }
        const int firstTarget = ReadSigned16(rewritten + 6);
        const int secondTarget = ReadSigned16(rewritten + 8);
        if (secondTarget == -1 &&
            IsPhysicalWarehouseSlot(firstTarget)) {
            WriteSigned16(
                rewritten + 6,
                destinationPage * PhysicalPageCapacity + firstTarget);
        }
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

} // namespace warehouse_page_host_detail

} // namespace godswar::network
