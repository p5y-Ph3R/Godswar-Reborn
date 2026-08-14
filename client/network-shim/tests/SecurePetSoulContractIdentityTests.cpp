#include "SecurePetSoulContractIdentityTests.h"

#include "../src/SecurePetCommandIdentity.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using namespace godswar::network;

struct Checks final {
    int failures = 0;
    void Require(bool condition, const char* message) {
        if (!condition) {
            std::fprintf(stderr, "FAIL: %s\n", message);
            ++failures;
        }
    }
};

void Write16(std::uint8_t* target, std::uint16_t value) {
    target[0] = static_cast<std::uint8_t>(value);
    target[1] = static_cast<std::uint8_t>(value >> 8U);
}

void Write32(std::uint8_t* target, std::uint32_t value) {
    target[0] = static_cast<std::uint8_t>(value);
    target[1] = static_cast<std::uint8_t>(value >> 8U);
    target[2] = static_cast<std::uint8_t>(value >> 16U);
    target[3] = static_cast<std::uint8_t>(value >> 24U);
}

void Build(
    std::uint8_t* packet,
    std::uint32_t material,
    std::uint8_t quantity) {
    std::memset(packet, 0, LegacyPetSoulContractPacketBytes);
    Write16(packet, LegacyPetSoulContractPacketBytes);
    Write16(packet + 2, LegacyPetSoulContractOpcode);
    Write32(packet + 4, material);
    packet[8] = quantity;
}

void CheckCanonicalFrames(Checks* checks) {
    std::uint8_t packet[LegacyPetSoulContractPacketBytes]{};
    LegacyPetCommandIntent first{};
    Build(packet, LegacyContractSpiritItemId, 0);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &first) ==
            LegacyPetCommandPacketKind::Command &&
        first.family == SecureLegacyCommandFamily::PetSoulContract &&
        first.bytes[0] == 1 && first.bytes[1] == 1 &&
        first.bytes[2] ==
            static_cast<std::uint8_t>(LegacyContractSpiritItemId) &&
        first.bytes[6] == 0,
        "zero-spirit Soul Contract did not receive exact identity");

    for (std::uint8_t quantity = 1;
         quantity <= LegacyMaximumPetAlterMaterialQuantity;
         ++quantity) {
        LegacyPetCommandIntent current{};
        Build(packet, LegacyContractSpiritItemId, quantity);
        checks->Require(
            ClassifyLegacyPetCommandPacket(
                packet,
                sizeof(packet),
                &current) == LegacyPetCommandPacketKind::Command &&
            current.family ==
                SecureLegacyCommandFamily::PetSoulContract &&
            current.bytes[6] == quantity &&
            !EqualPetCommandIntent(first, current),
            "Soul Contract quantity was rejected or aliased q0");
    }
}

void CheckMalformedFrames(Checks* checks) {
    std::uint8_t packet[LegacyPetSoulContractPacketBytes]{};
    LegacyPetCommandIntent intent{};
    Build(packet, LegacyRebirthSpiritItemId, 1);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Soul Contract accepted the wrong material template");
    Build(packet, LegacyContractSpiritItemId, 6);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Soul Contract accepted more than five spirits");
    Build(packet, LegacyContractSpiritItemId, 1);
    packet[11] = 1;
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Soul Contract accepted a nonzero reserved byte");
    Build(packet, LegacyContractSpiritItemId, 1);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            packet,
            sizeof(packet) - 1,
            &intent) == LegacyPetCommandPacketKind::InvalidMutation,
        "Soul Contract accepted a truncated frame");
}

} // namespace

int RunSecurePetSoulContractIdentityTests() {
    Checks checks{};
    CheckCanonicalFrames(&checks);
    CheckMalformedFrames(&checks);
    return checks.failures;
}
