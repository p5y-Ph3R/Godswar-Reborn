#include "WinSocketByteStream.h"

#include <algorithm>

namespace godswar::network {
namespace {

constexpr long ReadinessPollMicroseconds = 100'000;

} // namespace

WinSocketByteStream::WinSocketByteStream(
    SocketHandle&& socket) noexcept
    : socket_(static_cast<SocketHandle&&>(socket)) {
    u_long nonBlocking = 1;
    configured_ =
        socket_.IsValid() &&
        ioctlsocket(
            socket_.Get(),
            FIONBIO,
            &nonBlocking) != SOCKET_ERROR;
}

WinSocketByteStream::~WinSocketByteStream() noexcept {
    Stop();
}

bool WinSocketByteStream::IsValid() const noexcept {
    return socket_.IsValid() && configured_;
}

bool WinSocketByteStream::IsStopped() const noexcept {
    return InterlockedCompareExchange(
        const_cast<volatile LONG*>(&stopped_),
        0,
        0) != 0;
}

ByteStreamIoResult WinSocketByteStream::Read(
    void* destination,
    std::size_t destinationCapacity) noexcept {
    if (destination == nullptr ||
        destinationCapacity == 0 ||
        !IsValid() ||
        IsStopped()) {
        return {ByteStreamIoStatus::Failed, 0};
    }

    const std::size_t boundedBytes = (std::min)(
        destinationCapacity,
        MaximumIoBytes);
    for (;;) {
        const int received = recv(
            socket_.Get(),
            static_cast<char*>(destination),
            static_cast<int>(boundedBytes),
            0);
        if (received > 0) {
            return {
                ByteStreamIoStatus::Success,
                static_cast<std::size_t>(received)};
        }
        if (received == 0 && !IsStopped()) {
            return {ByteStreamIoStatus::EndOfStream, 0};
        }
        if (received == SOCKET_ERROR &&
            WSAGetLastError() == WSAEWOULDBLOCK &&
            WaitForReady(false)) {
            continue;
        }
        return {ByteStreamIoStatus::Failed, 0};
    }
}

ByteStreamIoResult WinSocketByteStream::Write(
    const void* source,
    std::size_t sourceBytes) noexcept {
    if (source == nullptr ||
        sourceBytes == 0 ||
        !IsValid() ||
        IsStopped()) {
        return {ByteStreamIoStatus::Failed, 0};
    }

    const std::size_t boundedBytes = (std::min)(
        sourceBytes,
        MaximumIoBytes);
    for (;;) {
        const int sent = send(
            socket_.Get(),
            static_cast<const char*>(source),
            static_cast<int>(boundedBytes),
            0);
        if (sent > 0) {
            return {
                ByteStreamIoStatus::Success,
                static_cast<std::size_t>(sent)};
        }
        if (sent == SOCKET_ERROR &&
            WSAGetLastError() == WSAEWOULDBLOCK &&
            WaitForReady(true)) {
            continue;
        }
        return {ByteStreamIoStatus::Failed, 0};
    }
}

void WinSocketByteStream::Stop() noexcept {
    if (InterlockedCompareExchange(&stopped_, 1, 0) == 0) {
        socket_.Shutdown();
    }
}

bool WinSocketByteStream::WaitForReady(bool write) noexcept {
    while (!IsStopped()) {
        fd_set descriptors;
        FD_ZERO(&descriptors);
        FD_SET(socket_.Get(), &descriptors);
        timeval timeout{};
        timeout.tv_usec = ReadinessPollMicroseconds;
        const int selected = select(
            0,
            write ? nullptr : &descriptors,
            write ? &descriptors : nullptr,
            nullptr,
            &timeout);
        if (selected > 0) {
            return !IsStopped();
        }
        if (selected == SOCKET_ERROR) {
            return false;
        }
    }

    return false;
}

} // namespace godswar::network
