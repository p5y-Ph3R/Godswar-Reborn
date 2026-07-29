#include "SecureLegacyCommandIdentity.h"

#include <Windows.h>
#include <bcrypt.h>

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

std::uint16_t ReadUInt16Little(
    const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        source[0] |
        (static_cast<std::uint16_t>(source[1]) << 8U));
}

std::uint32_t ReadUInt32Little(
    const std::uint8_t* source) noexcept {
    return
        source[0] |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
}

bool HashSha256(
    const std::uint8_t* source,
    std::size_t sourceBytes,
    std::uint8_t* destination) noexcept {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    if (BCryptOpenAlgorithmProvider(
            &algorithm,
            BCRYPT_SHA256_ALGORITHM,
            nullptr,
            0) < 0) {
        return false;
    }

    const NTSTATUS status = BCryptHash(
        algorithm,
        nullptr,
        0,
        const_cast<PUCHAR>(source),
        static_cast<ULONG>(sourceBytes),
        destination,
        SecurePrincipalFingerprintBytes);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return status >= 0;
}

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

bool IsRangeReadable(
    const void* address,
    std::size_t length) noexcept {
    if (address == nullptr || length == 0) {
        return false;
    }

    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) == 0 ||
        memory.State != MEM_COMMIT ||
        !HasReadableProtection(memory.Protect)) {
        return false;
    }

    const auto start = reinterpret_cast<std::uintptr_t>(address);
    const auto regionStart =
        reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
    const auto regionEnd = regionStart + memory.RegionSize;
    return regionEnd >= regionStart &&
        start >= regionStart &&
        start <= regionEnd &&
        length <= regionEnd - start;
}

} // namespace

bool TryReadLegacyPacketHeader(
    const void* packet,
    std::size_t packetBytes,
    std::uint16_t* opcode) noexcept {
    if (packet == nullptr ||
        packetBytes < 4 ||
        packetBytes > SecureLegacyMaximumPacketBytes ||
        opcode == nullptr) {
        return false;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    if (ReadUInt16Little(bytes) != packetBytes) {
        return false;
    }
    *opcode = ReadUInt16Little(bytes + 2);
    return true;
}

bool TryHashLegacyLoginPrincipal(
    const void* packet,
    std::size_t packetBytes,
    std::uint8_t* fingerprint,
    std::size_t fingerprintBytes) noexcept {
    std::uint16_t opcode = 0;
    if (fingerprint == nullptr ||
        fingerprintBytes != SecurePrincipalFingerprintBytes ||
        packetBytes < 4 + SecurePrincipalFingerprintBytes ||
        !TryReadLegacyPacketHeader(
            packet,
            packetBytes,
            &opcode) ||
        opcode != LegacyLoginGameServerOpcode) {
        return false;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    return HashSha256(
        bytes + 4,
        SecurePrincipalFingerprintBytes,
        fingerprint);
}

bool TryReadLegacyGearSelection(
    const void* packet,
    std::size_t packetBytes,
    int* bagSlot,
    bool* selected) noexcept {
    std::uint16_t opcode = 0;
    if (bagSlot == nullptr ||
        selected == nullptr ||
        packetBytes != 16 ||
        !TryReadLegacyPacketHeader(
            packet,
            packetBytes,
            &opcode) ||
        opcode != LegacyGearSelectionOpcode) {
        return false;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    const std::uint32_t page = ReadUInt32Little(bytes + 4);
    const std::uint32_t pageSlot =
        ReadUInt32Little(bytes + 8);
    const std::uint8_t isSelected = bytes[12];
    if (page >= 4 || pageSlot >= 24 || isSelected > 1) {
        return false;
    }

    *bagSlot = static_cast<int>(page * 24 + pageSlot);
    *selected = isSelected != 0;
    return true;
}

bool TryReadLegacyGearMentorAction(
    const void* packet,
    std::size_t packetBytes,
    LegacyGearMentorAction* action,
    std::uint32_t* npcId) noexcept {
    std::uint16_t opcode = 0;
    if (action == nullptr ||
        npcId == nullptr ||
        packetBytes != LegacyGearMentorActionPacketBytes ||
        !TryReadLegacyPacketHeader(
            packet,
            packetBytes,
            &opcode) ||
        opcode != LegacyNpcFunctionActionOpcode) {
        return false;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    const std::uint32_t npc = ReadUInt32Little(bytes + 4);
    const auto subId = static_cast<std::int32_t>(
        ReadUInt32Little(bytes + 16));
    if ((npc != LegacySpartaGearMentorNpc &&
            npc != LegacyAthensGearMentorNpc) ||
        static_cast<std::int32_t>(
            ReadUInt32Little(bytes + 8)) !=
            LegacyGearMentorDialog ||
        (subId !=
                static_cast<std::int32_t>(
                    LegacyGearMentorAction::InitialMenu) &&
            (subId <
                    static_cast<std::int32_t>(
                        LegacyGearMentorAction::DecomposeGear) ||
                subId >
                    static_cast<std::int32_t>(
                        LegacyGearMentorAction::
                            CombineGemPieces)))) {
        return false;
    }

    *action =
        static_cast<LegacyGearMentorAction>(subId);
    *npcId = npc;
    return true;
}

bool TryReadLegacyEnterMainCharacterId(
    const void* message,
    int* characterId) noexcept {
    constexpr std::size_t PacketOffset = sizeof(void*);
    constexpr std::size_t RequiredBytes =
        PacketOffset + sizeof(std::uint16_t) +
        sizeof(std::uint16_t) + sizeof(std::uint32_t);
    if (characterId == nullptr ||
        !IsRangeReadable(message, RequiredBytes)) {
        return false;
    }

    __try {
        const auto* packet =
            static_cast<const std::uint8_t*>(message) +
            PacketOffset;
        const std::uint32_t persistentCharacterId =
            ReadUInt32Little(packet + 4);
        if (ReadUInt16Little(packet) !=
                LegacyEnterMainPacketBytes ||
            ReadUInt16Little(packet + 2) !=
                LegacyEnterMainOpcode ||
            persistentCharacterId == 0 ||
            persistentCharacterId >
                static_cast<std::uint32_t>(
                    (std::numeric_limits<int>::max)())) {
            return false;
        }

        *characterId =
            static_cast<int>(persistentCharacterId);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

} // namespace godswar::network
