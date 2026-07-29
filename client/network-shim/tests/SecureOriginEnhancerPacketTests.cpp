#include "SecureOriginEnhancerPacketTests.h"

#include "../src/SecureLegacyCommandIdentity.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::LegacyGearMentorAction;
using godswar::network::TryReadLegacyOriginEnhancerCommit;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void Write16(
    std::uint8_t* destination,
    std::uint16_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] =
        static_cast<std::uint8_t>(value >> 8U);
}

void Write32(
    std::uint8_t* destination,
    std::uint32_t value) {
    for (std::size_t index = 0; index < 4; ++index) {
        destination[index] =
            static_cast<std::uint8_t>(
                value >> (index * 8U));
    }
}

void Header(
    std::uint8_t* packet,
    std::uint16_t packetBytes,
    std::uint16_t opcode) {
    Write16(packet, packetBytes);
    Write16(packet + 2, opcode);
}

void OriginActionPacket(
    std::uint32_t npcId,
    std::int32_t action,
    std::uint8_t* packet) {
    std::memset(packet, 0xFF, 92);
    Header(packet, 92, 10069);
    Write32(packet + 4, npcId);
    Write32(packet + 8, 118);
    Write32(
        packet + 16,
        static_cast<std::uint32_t>(action));
    Write32(packet + 20 + 6 * 4, 100);
    Write32(packet + 20 + 7 * 4, 125);
    Write32(packet + 20 + 8 * 4, 195);
}

bool Classifies(
    const std::uint8_t* packet,
    std::size_t packetBytes) {
    LegacyGearMentorAction action =
        LegacyGearMentorAction::InitialMenu;
    std::uint32_t npcId = 0;
    int gear = -1;
    int catalyst = -1;
    int stone = -1;
    return TryReadLegacyOriginEnhancerCommit(
        packet,
        packetBytes,
        &action,
        &npcId,
        &gear,
        &catalyst,
        &stone);
}

void CheckExactInlineClassification() {
    struct Case final {
        std::uint32_t npcId;
        std::int32_t action;
        LegacyGearMentorAction expected;
    };
    const Case cases[]{
        {5140, 2, LegacyGearMentorAction::EnhanceAttribute},
        {5282, 3, LegacyGearMentorAction::AddAttribute},
        {5140, 6, LegacyGearMentorAction::DeleteAttribute},
    };

    for (const auto& test : cases) {
        std::uint8_t packet[92]{};
        OriginActionPacket(test.npcId, test.action, packet);
        LegacyGearMentorAction action =
            LegacyGearMentorAction::InitialMenu;
        std::uint32_t npcId = 0;
        int gear = -1;
        int catalyst = -1;
        int stone = -1;
        Check(
            TryReadLegacyOriginEnhancerCommit(
                packet,
                sizeof(packet),
                &action,
                &npcId,
                &gear,
                &catalyst,
                &stone) &&
                action == test.expected &&
                npcId == test.npcId &&
                gear == 0 &&
                catalyst == 25 &&
                stone == 95,
            "exact Origin Enhancer commit was not classified");
    }

    std::uint8_t packet[92]{};
    OriginActionPacket(5140, 2, packet);
    Check(
        !Classifies(packet, sizeof(packet) - 1),
        "short Origin Enhancer commit was classified");
    Header(packet, 92, 10068);
    Check(
        !Classifies(packet, sizeof(packet)),
        "wrong-opcode Origin Enhancer commit was classified");
    Header(packet, 91, 10069);
    Check(
        !Classifies(packet, sizeof(packet)),
        "wrong-header-length Origin Enhancer commit was classified");
}

} // namespace

int RunSecureOriginEnhancerPacketTests() {
    Failures = 0;
    CheckExactInlineClassification();
    return Failures;
}
