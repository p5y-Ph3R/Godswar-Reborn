#include "SecureForgeCommandIdentity.h"

#include "SecureLegacyCommandIdentity.h"

#include <limits>

namespace godswar::network {
namespace {

std::uint32_t ReadUInt32Little(
    const std::uint8_t* source) noexcept {
    return
        source[0] |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
}

bool HasOpcode(
    const void* packet,
    std::size_t packetBytes,
    std::uint16_t expectedOpcode) noexcept {
    std::uint16_t opcode = 0;
    return TryReadLegacyPacketHeader(
               packet,
               packetBytes,
               &opcode) &&
        opcode == expectedOpcode;
}

bool HasValidForgeDescriptor(
    const std::uint8_t* bytes) noexcept {
    const std::uint32_t itemId = ReadUInt32Little(bytes + 20);
    const std::uint32_t quality = ReadUInt32Little(bytes + 24);
    const std::uint32_t grade = ReadUInt32Little(bytes + 28);
    const std::uint32_t stack = ReadUInt32Little(bytes + 32);
    const std::uint32_t bound = ReadUInt32Little(bytes + 36);
    const auto maximum =
        static_cast<std::uint32_t>(
            (std::numeric_limits<std::int16_t>::max)());
    return itemId != 0 &&
        quality <= maximum &&
        grade <= maximum &&
        stack != 0 &&
        stack <= maximum &&
        bound <= maximum;
}

} // namespace

bool IsLegacyForgeOpcode(std::uint16_t opcode) noexcept {
    return opcode == LegacyForgeStartOpcode ||
        opcode == LegacyForgeSelectionOpcode ||
        opcode == LegacyForgeReplacementSelectionOpcode ||
        opcode == LegacyForgeReplacementActionOpcode ||
        opcode == LegacyForgeCancelOpcode;
}

bool TryReadLegacyForgeSelection(
    const void* packet,
    std::size_t packetBytes,
    LegacyForgeSelection* selection) noexcept {
    if (selection == nullptr ||
        packetBytes != LegacyForgeSelectionPacketBytes ||
        !HasOpcode(
            packet,
            packetBytes,
            LegacyForgeSelectionOpcode)) {
        return false;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    const std::uint32_t page = ReadUInt32Little(bytes + 4);
    const std::uint32_t pageSlot = ReadUInt32Little(bytes + 8);
    const std::uint32_t destination =
        ReadUInt32Little(bytes + 12);
    const std::uint32_t mode = ReadUInt32Little(bytes + 16);
    if (page >= LegacyForgePageCount ||
        pageSlot >= LegacyForgeSlotsPerPage ||
        destination >
            static_cast<std::uint32_t>(
                (std::numeric_limits<std::int32_t>::max)()) ||
        mode >
            static_cast<std::uint32_t>(
                (std::numeric_limits<std::int32_t>::max)())) {
        return false;
    }

    // Other Forge modes use the same envelope but are not assigned a durable
    // identity here. Their descriptor tail is intentionally not interpreted.
    if (mode == LegacyOrdinaryForgeMode &&
        destination != LegacyForgeOddsIncrementAction &&
        !HasValidForgeDescriptor(bytes)) {
        return false;
    }

    LegacyForgeSelection parsed{};
    parsed.bagSlot = static_cast<int>(
        page * LegacyForgeSlotsPerPage + pageSlot);
    parsed.destination = destination;
    parsed.mode = mode;
    *selection = parsed;
    return true;
}

bool TryReadLegacyForgeStart(
    const void* packet,
    std::size_t packetBytes,
    std::uint32_t* mode) noexcept {
    if (mode == nullptr ||
        packetBytes != LegacyForgeStartPacketBytes ||
        !HasOpcode(packet, packetBytes, LegacyForgeStartOpcode)) {
        return false;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    const std::uint32_t parsed = ReadUInt32Little(bytes + 8);
    if (parsed >
        static_cast<std::uint32_t>(
            (std::numeric_limits<std::int32_t>::max)())) {
        return false;
    }
    *mode = parsed;
    return true;
}

bool TryReadLegacyForgeCancel(
    const void* packet,
    std::size_t packetBytes) noexcept {
    return packetBytes == LegacyForgeCancelPacketBytes &&
        HasOpcode(packet, packetBytes, LegacyForgeCancelOpcode);
}

bool TryReadLegacyForgeReplacement(
    const void* packet,
    std::size_t packetBytes,
    std::uint16_t expectedOpcode) noexcept {
    return (expectedOpcode ==
                LegacyForgeReplacementSelectionOpcode ||
            expectedOpcode ==
                LegacyForgeReplacementActionOpcode) &&
        HasOpcode(packet, packetBytes, expectedOpcode);
}

} // namespace godswar::network
