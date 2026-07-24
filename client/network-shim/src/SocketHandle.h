#pragma once

#include <WinSock2.h>

namespace godswar::network {

class SocketHandle final {
public:
    SocketHandle() noexcept = default;

    explicit SocketHandle(SOCKET value) noexcept
        : value_(value) {
    }

    ~SocketHandle() noexcept {
        Reset();
    }

    SocketHandle(const SocketHandle&) = delete;
    SocketHandle& operator=(const SocketHandle&) = delete;

    SocketHandle(SocketHandle&& other) noexcept
        : value_(other.Release()) {
    }

    SocketHandle& operator=(SocketHandle&& other) noexcept {
        if (this != &other) {
            Reset(other.Release());
        }

        return *this;
    }

    bool IsValid() const noexcept {
        return value_ != INVALID_SOCKET;
    }

    SOCKET Get() const noexcept {
        return value_;
    }

    SOCKET Release() noexcept {
        const auto released = value_;
        value_ = INVALID_SOCKET;
        return released;
    }

    void Shutdown() noexcept {
        if (IsValid()) {
            static_cast<void>(shutdown(value_, SD_BOTH));
        }
    }

    void Reset(SOCKET replacement = INVALID_SOCKET) noexcept {
        const auto previous = value_;
        if (previous == replacement) {
            return;
        }

        value_ = replacement;
        if (previous != INVALID_SOCKET) {
            static_cast<void>(closesocket(previous));
        }
    }

private:
    SOCKET value_ = INVALID_SOCKET;
};

} // namespace godswar::network
