#pragma once

#define SECURITY_WIN32
#define SCHANNEL_USE_BLACKLISTS

#include "SchannelClientStream.h"
#include "SchannelClientStreamPostHandshake.h"

#include <winternl.h>
#include <security.h>
#include <schannel.h>

#include <cstddef>
#include <cstdint>
#include <limits>

namespace godswar::network {
namespace schannel_detail {

inline constexpr std::size_t EncryptedInputCapacity = 64 * 1024;
inline constexpr std::size_t PlaintextCapacity = 16 * 1024;
inline constexpr std::size_t EncryptedOutputCapacity = 64 * 1024;

inline DWORD RemainingMilliseconds(ULONGLONG deadline) noexcept {
    const ULONGLONG now = GetTickCount64();
    if (deadline <= now) {
        return 0;
    }

    const ULONGLONG remaining = deadline - now;
    return remaining >= static_cast<ULONGLONG>(INFINITE)
        ? INFINITE - 1
        : static_cast<DWORD>(remaining);
}

} // namespace schannel_detail

struct SchannelClientStream::State final {
    explicit State(SocketHandle&& value) noexcept;
    ~State() noexcept;

    State(const State&) = delete;
    State& operator=(const State&) = delete;

    bool IsStopped() const noexcept;
    bool IsEstablished() const noexcept;
    void MarkEstablished(
        DWORD protocol,
        DWORD cipherSuite,
        bool validatedAlpn) noexcept;
    void Fail(SchannelClientFailure reason) noexcept;
    SchannelClientSnapshot Snapshot() const noexcept;
    bool WaitForReady(
        bool write,
        ULONGLONG deadline) noexcept;
    DeadlineStreamResult RawRead(
        void* destination,
        std::size_t capacity,
        ULONGLONG deadline) noexcept;
    bool RawWriteAll(
        const void* source,
        std::size_t bytes,
        ULONGLONG deadline) noexcept;
    bool ContinueTls13PostHandshake(
        const SecBuffer* decryptBuffers,
        std::size_t decryptBufferCount,
        ULONGLONG deadline) noexcept;
    bool ValidateTls13PostHandshakeContext(
        ULONG returnedAttributes) noexcept;

    SocketHandle socket;
    SRWLOCK readLock{};
    SRWLOCK writeLock{};
    SRWLOCK contextLock{};
    mutable SRWLOCK snapshotLock{};
    CredHandle credentials{};
    CtxtHandle context{};
    SecPkgContext_StreamSizes streamSizes{};
    wchar_t targetName[254]{};
    std::uint8_t encryptedInput[
        schannel_detail::EncryptedInputCapacity]{};
    std::size_t encryptedInputBytes = 0;
    std::uint8_t plaintext[
        schannel_detail::PlaintextCapacity]{};
    std::size_t plaintextOffset = 0;
    std::size_t plaintextBytes = 0;
    std::uint8_t encryptedOutput[
        schannel_detail::EncryptedOutputCapacity]{};
    volatile LONG stopped = 0;
    bool configured = false;
    bool established = false;
    SchannelClientFailure failure = SchannelClientFailure::None;
    DWORD negotiatedProtocol = 0;
    DWORD negotiatedCipherSuite = 0;
    bool alpnValidated = false;
};

} // namespace godswar::network
