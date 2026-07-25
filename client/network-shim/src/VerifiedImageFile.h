#pragma once

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

// Owns a verified normal-file handle that denies write and delete sharing.
// Keep the object alive until LoadLibraryExW has mapped the verified image.
class VerifiedImageFile final {
public:
    VerifiedImageFile() noexcept = default;
    ~VerifiedImageFile() noexcept;

    VerifiedImageFile(const VerifiedImageFile&) = delete;
    VerifiedImageFile& operator=(const VerifiedImageFile&) = delete;

    bool OpenAndVerify(
        const wchar_t* path,
        const std::uint8_t* expectedHash,
        std::size_t expectedHashSize) noexcept;

    bool IsOpen() const noexcept;
    void Close() noexcept;

private:
    HANDLE handle_ = INVALID_HANDLE_VALUE;
};

} // namespace godswar::network
