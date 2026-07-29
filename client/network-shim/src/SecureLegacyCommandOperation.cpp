#include "SecureClientProtocol.h"

#include "SecureClientRuntimeInternal.h"

namespace godswar::network {

bool TryCreateSecureLegacyCommandOperation(
    std::uint16_t packetBytes,
    std::uint16_t opcode,
    SecureLegacyCommandOperation* operation) noexcept {
    if (operation == nullptr ||
        packetBytes < 4 ||
        packetBytes > SecureLegacyMaximumPacketBytes) {
        return false;
    }

    SecureLegacyCommandOperation created{};
    if (!GenerateSystemSecureRandom(
            created.operationId,
            sizeof(created.operationId))) {
        return false;
    }

    // Canonical RFC 4122 version-4 and variant bits make the random
    // identifier portable across the C++ and .NET protocol codecs.
    created.operationId[6] =
        static_cast<std::uint8_t>(
            (created.operationId[6] & 0x0FU) | 0x40U);
    created.operationId[8] =
        static_cast<std::uint8_t>(
            (created.operationId[8] & 0x3FU) | 0x80U);
    created.packetBytes = packetBytes;
    created.opcode = opcode;
    *operation = created;
    return true;
}

} // namespace godswar::network
