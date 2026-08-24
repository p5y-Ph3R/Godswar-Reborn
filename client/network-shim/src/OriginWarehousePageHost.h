#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

namespace warehouse_page_host_detail {

bool PrepareRuntimePatchOnLoad() noexcept;
bool EnsureRuntimePatched() noexcept;
bool TryReadSelectedPage(int* page) noexcept;
bool TrySelectPage(int page) noexcept;
bool TryClearProjectedStorageChunk(
    int firstSlot,
    int slotCount) noexcept;
bool TryShowEmptyProjectedStoragePage() noexcept;
bool IsWarehouseDragActive() noexcept;
bool NormalizeProjectedSnapshotHeader(
    void* packet,
    std::size_t packetBytes,
    int* page,
    int* unlockedPageCount,
    bool* isTail) noexcept;
bool RewriteTransferPacketForPages(
    const void* packet,
    int packetBytes,
    int sourcePage,
    int destinationPage,
    void* destination,
    std::size_t destinationBytes) noexcept;

} // namespace warehouse_page_host_detail

class OriginWarehousePageHost final {
public:
    OriginWarehousePageHost() noexcept;

    void Reset() noexcept;
    bool TryRewriteClientPacket(
        const void* packet,
        int packetBytes,
        void* destination,
        std::size_t destinationBytes) noexcept;
    void ObserveClientPacket(const void* packet, int packetBytes) noexcept;
    void ObserveServerMessage(void* message) noexcept;
    bool TryBuildPageRequest(
        void* destination,
        std::size_t destinationBytes,
        int* requestBytes) noexcept;
    void CompletePageRequestSend(bool sent) noexcept;

private:
    bool BuildPendingRequest(
        void* destination,
        std::size_t destinationBytes,
        int* requestBytes) noexcept;

    bool enabled_ = false;
    std::uint32_t warehouseNpcId_ = 0;
    int visiblePage_ = 0;
    int unlockedPageCount_ = 1;
    int pendingPage_ = -1;
    int readySnapshotPage_ = -1;
    int readyUnlockedPageCount_ = -1;
    int dragSourcePage_ = -1;
    bool requestSent_ = false;
    bool snapshotInProgress_ = false;
};

} // namespace godswar::network
