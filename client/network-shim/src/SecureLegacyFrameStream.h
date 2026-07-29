#pragma once

#include "OpaqueDuplexPump.h"
#include "SecureClientProtocol.h"

namespace godswar::network {

class ISecureLegacyFrameStream : public IByteStream {
public:
    virtual ByteStreamIoResult WriteDescribedLegacyBytes(
        const SecureLegacyCommandOperation* operation,
        const void* source,
        std::size_t sourceBytes) noexcept = 0;
};

} // namespace godswar::network
