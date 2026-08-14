#pragma once

#include "SecureRealtimeMovementProtocol.h"
#include "SecureUdpBindingGrant.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

enum class SecureEndpointRole : std::uint8_t {
    Login = 1,
    Game = 2,
};

enum class SecureServerPrefaceStatus : std::uint8_t {
    Ok = 0,
    UnsupportedVersion = 1,
    WrongEndpoint = 2,
    UnsupportedBuild = 3,
    ServerBusy = 4,
    PolicyRejected = 5,
};

enum class SecureFrameDirection : std::uint8_t {
    ClientToServer = 0,
    ServerToClient = 1,
};

enum class SecureFrameType : std::uint16_t {
    Ping = 0x0001,
    Pong = 0x0002,
    Close = 0x0003,
    LegacyBytes = 0x0100,
    LegacyCommandOperation = 0x0101,
    LegacyCommandResult = 0x0102,
    GameGrant = 0x0200,
    GameBind = 0x0201,
    BindResult = 0x0202,
    UdpBindingGrant = 0x0203,
    RealtimeMovementInput = 0x0300,
};

struct SecureServerPrefaceView final {
    SecureServerPrefaceStatus status =
        SecureServerPrefaceStatus::PolicyRejected;
    SecureEndpointRole role = SecureEndpointRole::Login;
    std::uint8_t connectionId[16]{};
};

struct SecureFrameHeader final {
    std::uint32_t payloadBytes = 0;
    SecureFrameType type = SecureFrameType::Close;
    std::uint64_t sequence = 0;
};

struct SecureLegacyCommandOperation final {
    std::uint8_t operationId[16]{};
    std::uint16_t packetBytes = 0;
    std::uint16_t opcode = 0;
};

enum class SecureLegacyCommandDisposition : std::uint8_t {
    Applied = 1,
    Replayed = 2,
    Rejected = 3,
    Conflict = 4,
};

enum class SecureLegacyCommandFamily : std::uint16_t {
    PetLevelUpgrade = 2,
    EquipmentForge = 3,
    MakeAttributeStone = 6,
    TransformCrystal = 7,
    CombineGemPieces = 8,
    DecomposeGear = 9,
    GearMentorEnhanceAttribute = 10,
    GearMentorAddAttribute = 11,
    GearMentorDeleteAttribute = 12,
    KitBagItemDelete = 13,
    KitBagItemMove = 14,
    EquipmentBagTransfer = 15,
    HolyStoneMount = 16,
    HolyStoneRemove = 17,
    HolyStoneDrill = 18,
    ZodiacSkillGridUpgrade = 20,
    ZodiacSkillGridSelection = 21,
    CharacterCreate = 22,
    CharacterDelete = 23,
    BagItemActivation = 26,
    PetPresenceTransition = 27,
    HolySuitStoreExperience = 30,
    HolySuitTransferExperience = 31,
    HolySuitConsumeWare = 32,
    HolySuitTransformExperience = 33,
    ClassSuitExchangeTierI = 34,
    ClassSuitConvertToCommon = 35,
    ClassSuitUpgradeTierII = 36,
    ClassSuitUpgradeTierIII = 37,
    ClassSuitUpgradeTierIV = 38,
    ClassSuitAddAttribute = 39,
    ClassSuitDeleteAttribute = 40,
    HolyStoneAdvancedDrill = 41,
    HolyStoneUpgrade = 42,
    HolyStoneCombine = 43,
    HolyStoneImplementSpirit = 44,
    MountGearDrill = 45,
    PetSkillUnlearn = 46,
    PetGrowthReset = 47,
    PetOwnerMergeToggle = 48,
    PetToPetMerge = 49,
    PetRebirth = 50,
    PetBasicSavvyReset = 51,
    PetAppearanceChange = 52,
    PetBind = 53,
    PetSoulContract = 54,
    PetManagerUtility = 55,
};

struct SecureLegacyCommandResult final {
    SecureLegacyCommandDisposition disposition =
        SecureLegacyCommandDisposition::Rejected;
    SecureLegacyCommandFamily commandFamily =
        SecureLegacyCommandFamily::MakeAttributeStone;
    std::uint32_t resultCode = 0;
    std::uint64_t inventoryRevision = 0;
    std::uint8_t operationId[16]{};
};

inline constexpr std::size_t SecureClientPrefaceBytes = 72;
inline constexpr std::size_t SecureServerPrefaceBytes = 40;
inline constexpr std::size_t SecureFrameHeaderBytes = 16;
inline constexpr std::size_t SecureMaximumPayloadBytes = 16 * 1024;
inline constexpr std::size_t SecureGameGrantIdBytes = 16;
inline constexpr std::size_t SecureGameTicketBytes = 32;
inline constexpr std::size_t SecureGameGrantFixedBytes = 68;
inline constexpr std::size_t SecureGameGrantMinimumBytes = 71;
inline constexpr std::size_t SecureGameGrantMaximumBytes = 408;
inline constexpr std::size_t SecureGameBindBytes = 52;
inline constexpr std::size_t SecureBindResultBytes = 4;
inline constexpr std::size_t SecureUdpBindingGrantPayloadBytes =
    SecureUdpBindingGrantBytes;
inline constexpr std::size_t SecureRealtimeMovementInputPayloadBytes =
    SecureRealtimeMovementInputBytes;
inline constexpr std::size_t SecureLegacyCommandOperationPayloadBytes = 24;
inline constexpr std::uint8_t SecureLegacyCommandOperationVersion = 1;
inline constexpr std::size_t SecureLegacyCommandResultPayloadBytes = 32;
inline constexpr std::uint8_t SecureLegacyCommandResultVersion = 1;
inline constexpr std::uint16_t SecureLegacyMaximumPacketBytes = 8'196;
inline constexpr std::uint16_t SecureProtocolMajor = 1;
inline constexpr std::uint16_t SecureProtocolMinor = 0;

bool TryEncodeSecureClientPreface(
    SecureEndpointRole role,
    const std::uint8_t* clientInstanceId,
    std::size_t clientInstanceIdBytes,
    const std::uint8_t* originSha256,
    std::size_t originSha256Bytes,
    void* destination,
    std::size_t destinationBytes) noexcept;

bool TryDecodeSecureServerPreface(
    const void* source,
    std::size_t sourceBytes,
    SecureEndpointRole expectedRole,
    SecureServerPrefaceView* preface) noexcept;

bool TryEncodeSecureFrameHeader(
    const SecureFrameHeader& header,
    SecureEndpointRole role,
    SecureFrameDirection direction,
    void* destination,
    std::size_t destinationBytes) noexcept;

bool TryDecodeSecureFrameHeader(
    const void* source,
    std::size_t sourceBytes,
    SecureEndpointRole role,
    SecureFrameDirection direction,
    std::uint64_t expectedSequence,
    SecureFrameHeader* header) noexcept;

bool TryGetNextSecureSequence(
    std::uint64_t current,
    std::uint64_t* next) noexcept;

bool TryEncodeSecureLegacyCommandOperation(
    const SecureLegacyCommandOperation& operation,
    void* destination,
    std::size_t destinationBytes) noexcept;

bool TryDecodeSecureLegacyCommandOperation(
    const void* source,
    std::size_t sourceBytes,
    SecureLegacyCommandOperation* operation) noexcept;

bool TryEncodeSecureLegacyCommandResult(
    const SecureLegacyCommandResult& result,
    void* destination,
    std::size_t destinationBytes) noexcept;

bool TryDecodeSecureLegacyCommandResult(
    const void* source,
    std::size_t sourceBytes,
    SecureLegacyCommandResult* result) noexcept;

bool TryCreateSecureLegacyCommandOperation(
    std::uint16_t packetBytes,
    std::uint16_t opcode,
    SecureLegacyCommandOperation* operation) noexcept;

} // namespace godswar::network
