#pragma once

#include "SocketHandle.h"
#include "OpaqueDuplexPump.h"

#include <Windows.h>

#include <cstddef>

namespace godswar::network {

// A move-owned blocking WinSock stream for the opaque bridge. Stop performs
// shutdown without closing the descriptor, so concurrent I/O is unblocked
// without allowing the numeric descriptor to be reused before workers join.
class WinSocketByteStream final : public IByteStream {
public:
    static constexpr std::size_t MaximumIoBytes =
        OpaqueDuplexPump::ReadBufferBytes;

    explicit WinSocketByteStream(SocketHandle&& socket) noexcept;
    ~WinSocketByteStream() noexcept;

    WinSocketByteStream(const WinSocketByteStream&) = delete;
    WinSocketByteStream& operator=(const WinSocketByteStream&) = delete;

    bool IsValid() const noexcept;
    bool IsStopped() const noexcept;

    ByteStreamIoResult Read(
        void* destination,
        std::size_t destinationCapacity) noexcept override;
    ByteStreamIoResult Write(
        const void* source,
        std::size_t sourceBytes) noexcept override;
    void Stop() noexcept override;

private:
    bool WaitForReady(bool write) noexcept;

    SocketHandle socket_;
    volatile LONG stopped_ = 0;
    bool configured_ = false;
};

} // namespace godswar::network
