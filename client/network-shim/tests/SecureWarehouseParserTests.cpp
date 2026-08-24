#include "SecureWarehouseTestSupport.h"
#include "../src/OriginWarehousePageHost.h"

namespace {

using namespace warehouse_test;

void CheckTransferShapes(Checks* checks) {
    std::uint8_t packet[LegacyWarehouseTransferPacketBytes]{};
    LegacyWarehouseTransferCommand command{};

    BuildTransferPacket(packet, 7, 2, 5, 1, 0, 0xBEEF);
    checks->Require(
        ClassifyLegacyWarehouseTransferPacket(
            packet, sizeof(packet), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.operation ==
            LegacyWarehouseTransferOperation::Deposit &&
        command.warehouseSlot == 7 && command.kitBagSlot == 53 &&
        command.destinationWarehouseSlot == -1,
        "explicit warehouse deposit did not parse");

    BuildTransferPacket(packet, -1, 0, 23, 1, 0, 0xCAFE);
    checks->Require(
        ClassifyLegacyWarehouseTransferPacket(
            packet, sizeof(packet), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.operation ==
            LegacyWarehouseTransferOperation::Deposit &&
        command.warehouseSlot == -1 && command.kitBagSlot == 23,
        "automatic warehouse deposit did not normalize native tail scratch");

    BuildTransferPacket(packet, 10, 1, 23, 0);
    checks->Require(
        ClassifyLegacyWarehouseTransferPacket(
            packet, sizeof(packet), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.operation ==
            LegacyWarehouseTransferOperation::Withdraw &&
        command.warehouseSlot == 10 && command.kitBagSlot == 47,
        "explicit warehouse withdrawal did not parse");

    BuildTransferPacket(packet, 10, -1, -1, 0);
    checks->Require(
        ClassifyLegacyWarehouseTransferPacket(
            packet, sizeof(packet), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.operation ==
            LegacyWarehouseTransferOperation::Withdraw &&
        command.kitBagSlot == -1,
        "automatic warehouse withdrawal did not parse");

    BuildTransferPacket(packet, 10, 11, -1, 0, 0, 1);
    checks->Require(
        ClassifyLegacyWarehouseTransferPacket(
            packet, sizeof(packet), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.operation ==
            LegacyWarehouseTransferOperation::InternalMove &&
        command.warehouseSlot == 10 &&
        command.destinationWarehouseSlot == 11 &&
        command.kitBagSlot == -1,
        "stock warehouse internal swap tail did not normalize");

    BuildTransferPacket(packet, 359, 0, 0, 1);
    checks->Require(
        ClassifyLegacyWarehouseTransferPacket(
            packet, sizeof(packet), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.warehouseSlot == 359,
        "highest logical warehouse slot did not parse");
}

void CheckTransferFailures(Checks* checks) {
    std::uint8_t packet[LegacyWarehouseTransferPacketBytes + 1]{};
    BuildTransferPacket(packet, 7, 0, 0, 1);
    checks->Require(
        ClassifyLegacyWarehouseTransferPacket(
            packet, LegacyWarehouseTransferPacketBytes - 1, nullptr) ==
                LegacyWarehousePacketKind::InvalidMutation &&
        ClassifyLegacyWarehouseTransferPacket(
            packet, LegacyWarehouseTransferPacketBytes + 1, nullptr) ==
                LegacyWarehousePacketKind::InvalidMutation,
        "recognized warehouse transfer accepted a non-20-byte frame");

    struct Invalid final {
        std::int16_t first;
        std::int16_t second;
        std::int16_t third;
        std::uint8_t direction;
        std::int32_t money;
        std::uint16_t storageType;
    };
    const Invalid invalid[]{
        {7, 0, 0, 2, 0, 0},
        {7, 0, 0, 1, 1, 0},
        {360, 0, 0, 1, 0, 0},
        {7, 4, 0, 1, 0, 0},
        {7, 0, 24, 1, 0, 0},
        {360, -1, -1, 0, 0, 0},
        {7, -1, 0, 0, 0, 0},
        {7, 0, 0, 0, 0, 1},
        {7, 7, -1, 0, 0, 0},
        {7, 360, -1, 0, 0, 0},
        {7, 8, -1, 0, 0, 2},
    };
    for (const auto& shape : invalid) {
        BuildTransferPacket(
            packet, shape.first, shape.second, shape.third,
            shape.direction, shape.money, shape.storageType);
        checks->Require(
            ClassifyLegacyWarehouseTransferPacket(
                packet, LegacyWarehouseTransferPacketBytes, nullptr) ==
                    LegacyWarehousePacketKind::InvalidMutation,
            "warehouse transfer accepted an invalid mutation shape");
    }

    BuildTransferPacket(packet, 7, 0, 0, 1);
    Write16(packet + 2, LegacyWarehouseTransferOpcode - 1);
    checks->Require(
        ClassifyLegacyWarehouseTransferPacket(
            packet, LegacyWarehouseTransferPacketBytes, nullptr) ==
                LegacyWarehousePacketKind::Unrelated &&
        ClassifyLegacyWarehouseTransferPacket(nullptr, 0, nullptr) ==
                LegacyWarehousePacketKind::Unrelated,
        "unrelated/null packet was mistaken for warehouse transfer");
}

void CheckProjectedPageRewrites(Checks* checks) {
    std::uint8_t packet[LegacyWarehouseTransferPacketBytes]{};
    std::uint8_t rewritten[LegacyWarehouseTransferPacketBytes]{};
    LegacyWarehouseTransferCommand command{};

    BuildTransferPacket(packet, 39, 2, 5, 1);
    checks->Require(
        warehouse_page_host_detail::RewriteTransferPacketForPages(
            packet, sizeof(packet), 8, 8, rewritten, sizeof(rewritten)) &&
        ClassifyLegacyWarehouseTransferPacket(
            rewritten, sizeof(rewritten), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.operation ==
            LegacyWarehouseTransferOperation::Deposit &&
        command.warehouseSlot == 359,
        "projected deposit did not target the selected logical box");

    BuildTransferPacket(packet, 7, 1, 23, 0);
    checks->Require(
        warehouse_page_host_detail::RewriteTransferPacketForPages(
            packet, sizeof(packet), 4, 6, rewritten, sizeof(rewritten)) &&
        ClassifyLegacyWarehouseTransferPacket(
            rewritten, sizeof(rewritten), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.operation ==
            LegacyWarehouseTransferOperation::Withdraw &&
        command.warehouseSlot == 167 && command.kitBagSlot == 47,
        "projected withdrawal changed the bag target or source page");

    BuildTransferPacket(packet, 5, 9, -1, 0, 0, 1);
    checks->Require(
        warehouse_page_host_detail::RewriteTransferPacketForPages(
            packet, sizeof(packet), 2, 6, rewritten, sizeof(rewritten)) &&
        ClassifyLegacyWarehouseTransferPacket(
            rewritten, sizeof(rewritten), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.operation ==
            LegacyWarehouseTransferOperation::InternalMove &&
        command.warehouseSlot == 85 &&
        command.destinationWarehouseSlot == 249,
        "cross-box move did not preserve distinct source/destination pages");

    BuildTransferPacket(packet, -1, 0, 0, 1);
    checks->Require(
        warehouse_page_host_detail::RewriteTransferPacketForPages(
            packet, sizeof(packet), 7, 7, rewritten, sizeof(rewritten)) &&
        ClassifyLegacyWarehouseTransferPacket(
            rewritten, sizeof(rewritten), &command) ==
                LegacyWarehousePacketKind::Transfer &&
        command.warehouseSlot == -1,
        "automatic deposit was incorrectly assigned a physical cell");

    Write16(packet + 2, LegacyWarehouseTransferOpcode - 1);
    checks->Require(
        !warehouse_page_host_detail::RewriteTransferPacketForPages(
            packet, sizeof(packet), 0, 0, rewritten, sizeof(rewritten)),
        "unrelated packet was rewritten as a warehouse transfer");
}

void CheckProjectedSnapshotCapacity(Checks* checks) {
    constexpr std::size_t HeaderBytes = 24;
    constexpr std::uint16_t SnapshotOpcode = 10034;
    constexpr std::uint32_t ProjectionMarker = 0x57485000;
    std::uint8_t packet[HeaderBytes]{};
    Write16(packet, static_cast<std::uint16_t>(HeaderBytes));
    Write16(packet + 2, SnapshotOpcode);
    Write32(packet + 8, ProjectionMarker + 0x41);
    Write16(packet + 12, 40);
    packet[14] = 6;

    int page = -1;
    int unlocked = -1;
    bool tail = false;
    checks->Require(
        warehouse_page_host_detail::NormalizeProjectedSnapshotHeader(
            packet, sizeof(packet), &page, &unlocked, &tail) &&
        page == 1 && unlocked == 4 && tail &&
        packet[12] == 160 && packet[13] == 0,
        "legacy 40-cell projection did not enable four native tabs");
    checks->Require(
        warehouse_page_host_detail::NormalizeProjectedSnapshotHeader(
            packet, sizeof(packet), &page, &unlocked, &tail) &&
        packet[12] == 160 && packet[13] == 0,
        "normalized projected capacity was not idempotent");

    packet[14] = 5;
    checks->Require(
        !warehouse_page_host_detail::NormalizeProjectedSnapshotHeader(
            packet, sizeof(packet), &page, &unlocked, &tail),
        "misaligned projected snapshot selector was accepted");
    packet[14] = 6;
    Write16(packet + 16, 0x10);
    checks->Require(
        !warehouse_page_host_detail::NormalizeProjectedSnapshotHeader(
            packet, sizeof(packet), &page, &unlocked, &tail),
        "projected tail snapshot escaped its four-cell clear region");
    Write16(packet + 16, 0);

    Write32(packet + 8, ProjectionMarker + 0x98);
    Write16(packet + 12, 40);
    checks->Require(
        warehouse_page_host_detail::NormalizeProjectedSnapshotHeader(
            packet, sizeof(packet), &page, &unlocked, &tail) &&
        page == 8 && unlocked == 9 &&
        packet[12] == 160 && packet[13] == 0,
        "nine-box projection exceeded the stock four-tab capacity");

    Write16(packet + 12, 80);
    checks->Require(
        !warehouse_page_host_detail::NormalizeProjectedSnapshotHeader(
            packet, sizeof(packet), &page, &unlocked, &tail),
        "contradictory projected capacity was accepted");
}

void CheckManagerShapes(Checks* checks) {
    std::uint8_t packet[LegacyWarehouseManagerPacketBytes + 1]{};
    LegacyWarehouseExpansionCommand command{};

    BuildManagerPacket(packet, LegacyWarehouseManagerInitialSubId);
    checks->Require(
        ClassifyLegacyWarehouseExpansionPacket(
            packet, LegacyWarehouseManagerPacketBytes, nullptr) ==
                LegacyWarehousePacketKind::Navigation,
        "warehouse manager initial page was marked valuable");

    BuildManagerPacket(
        packet, LegacyWarehouseManagerExpandSubId,
        LegacySpartaWarehouseManagerNpc);
    Write32(packet + 20, 0xA5010101U);
    Write32(packet + 88, 0xB6020202U);
    checks->Require(
        ClassifyLegacyWarehouseExpansionPacket(
            packet, LegacyWarehouseManagerPacketBytes, &command) ==
                LegacyWarehousePacketKind::Expansion &&
        command.npcId == LegacySpartaWarehouseManagerNpc,
        "warehouse manager expansion rejected native argument scratch");

    checks->Require(
        ClassifyLegacyWarehouseExpansionPacket(
            packet, LegacyWarehouseManagerPacketBytes - 1, nullptr) ==
                LegacyWarehousePacketKind::InvalidMutation &&
        ClassifyLegacyWarehouseExpansionPacket(
            packet, LegacyWarehouseManagerPacketBytes + 1, nullptr) ==
                LegacyWarehousePacketKind::InvalidMutation,
        "recognized warehouse manager accepted a non-92-byte frame");

    BuildManagerPacket(packet, LegacyWarehouseManagerExpandSubId);
    Write32(packet + 12, LegacyWarehouseManagerDialog + 1);
    checks->Require(
        ClassifyLegacyWarehouseExpansionPacket(
            packet, LegacyWarehouseManagerPacketBytes, nullptr) ==
                LegacyWarehousePacketKind::InvalidMutation,
        "warehouse manager accepted a changed duplicate dialog");
    BuildManagerPacket(packet, 101);
    checks->Require(
        ClassifyLegacyWarehouseExpansionPacket(
            packet, LegacyWarehouseManagerPacketBytes, nullptr) ==
                LegacyWarehousePacketKind::InvalidMutation,
        "warehouse manager accepted an unknown action");
    BuildManagerPacket(packet, LegacyWarehouseManagerExpandSubId, 5000);
    checks->Require(
        ClassifyLegacyWarehouseExpansionPacket(
            packet, LegacyWarehouseManagerPacketBytes, nullptr) ==
                LegacyWarehousePacketKind::Unrelated,
        "another NPC was mistaken for warehouse manager");
    Write16(packet + 2, LegacyNpcFunctionActionOpcode - 1);
    checks->Require(
        ClassifyLegacyWarehouseExpansionPacket(
            packet, LegacyWarehouseManagerPacketBytes, nullptr) ==
                LegacyWarehousePacketKind::Unrelated,
        "another opcode was mistaken for warehouse manager");
}

} // namespace

int RunSecureWarehouseParserTests() {
    Checks checks{};
    CheckTransferShapes(&checks);
    CheckTransferFailures(&checks);
    CheckProjectedPageRewrites(&checks);
    CheckProjectedSnapshotCapacity(&checks);
    CheckManagerShapes(&checks);
    return checks.failures;
}
