#define SECURITY_WIN32
#define SCHANNEL_USE_BLACKLISTS

#include "SchannelClientStreamPostHandshake.h"

#include <cstring>
#include <limits>

namespace godswar::network::schannel_detail {
namespace {

constexpr std::size_t MaximumSchannelBuffers = 8;

struct BufferSlice final {
    std::size_t offset = 0;
    std::size_t bytes = 0;
};

bool IsContinuationSliceType(unsigned long type) noexcept {
    return type == SECBUFFER_DATA ||
        type == SECBUFFER_TOKEN ||
        type == SECBUFFER_STREAM_HEADER ||
        type == SECBUFFER_STREAM_TRAILER;
}

bool TryResolveInputSlice(
    const SecBuffer& buffer,
    void* encryptedInput,
    std::size_t encryptedInputBytes,
    bool allowSizeOnly,
    BufferSlice* slice) noexcept {
    if (encryptedInput == nullptr ||
        encryptedInputBytes == 0 ||
        slice == nullptr ||
        buffer.cbBuffer == 0 ||
        buffer.cbBuffer > encryptedInputBytes) {
        return false;
    }

    const std::size_t bytes = buffer.cbBuffer;
    if (buffer.pvBuffer == nullptr) {
        if (!allowSizeOnly) {
            return false;
        }
        slice->offset = encryptedInputBytes - bytes;
        slice->bytes = bytes;
        return true;
    }

    const auto inputStart =
        reinterpret_cast<std::uintptr_t>(encryptedInput);
    if (encryptedInputBytes >
        (std::numeric_limits<std::uintptr_t>::max)() -
            inputStart) {
        return false;
    }
    const auto inputEnd = inputStart + encryptedInputBytes;
    const auto bufferStart =
        reinterpret_cast<std::uintptr_t>(buffer.pvBuffer);
    if (bufferStart < inputStart ||
        bufferStart >= inputEnd ||
        bytes > inputEnd - bufferStart) {
        return false;
    }

    slice->offset =
        static_cast<std::size_t>(bufferStart - inputStart);
    slice->bytes = bytes;
    return true;
}

bool TryFindExtraSlice(
    const SecBuffer* buffers,
    std::size_t bufferCount,
    void* encryptedInput,
    std::size_t encryptedInputBytes,
    bool* found,
    BufferSlice* slice) noexcept {
    if (buffers == nullptr ||
        bufferCount == 0 ||
        bufferCount > MaximumSchannelBuffers ||
        encryptedInput == nullptr ||
        encryptedInputBytes == 0 ||
        found == nullptr ||
        slice == nullptr) {
        return false;
    }

    *found = false;
    *slice = BufferSlice{};
    for (std::size_t index = 0; index < bufferCount; ++index) {
        if (buffers[index].BufferType != SECBUFFER_EXTRA) {
            continue;
        }
        if (*found ||
            !TryResolveInputSlice(
                buffers[index],
                encryptedInput,
                encryptedInputBytes,
                true,
                slice) ||
            slice->offset + slice->bytes !=
                encryptedInputBytes) {
            return false;
        }
        *found = true;
    }
    return true;
}

void RetainInputSlice(
    void* encryptedInput,
    std::size_t encryptedInputBytes,
    const BufferSlice& slice) noexcept {
    auto* input = static_cast<std::uint8_t*>(encryptedInput);
    std::memmove(
        input,
        input + slice.offset,
        slice.bytes);
    if (encryptedInputBytes > slice.bytes) {
        SecureZeroMemory(
            input + slice.bytes,
            encryptedInputBytes - slice.bytes);
    }
}

} // namespace

bool TryPrepareSchannelPostHandshakeToken(
    const SecBuffer* buffers,
    std::size_t bufferCount,
    void* encryptedInput,
    std::size_t encryptedInputBytes,
    std::size_t encryptedInputCapacity,
    std::size_t* tokenBytes) noexcept {
    if (buffers == nullptr ||
        bufferCount == 0 ||
        bufferCount > MaximumSchannelBuffers ||
        encryptedInput == nullptr ||
        encryptedInputBytes == 0 ||
        encryptedInputBytes > encryptedInputCapacity ||
        tokenBytes == nullptr) {
        return false;
    }
    *tokenBytes = 0;

    bool extraFound = false;
    BufferSlice extra{};
    if (!TryFindExtraSlice(
            buffers,
            bufferCount,
            encryptedInput,
            encryptedInputBytes,
            &extraFound,
            &extra)) {
        return false;
    }
    if (extraFound) {
        RetainInputSlice(
            encryptedInput,
            encryptedInputBytes,
            extra);
        *tokenBytes = extra.bytes;
        return true;
    }

    BufferSlice slices[MaximumSchannelBuffers]{};
    std::size_t sliceCount = 0;
    std::size_t rangeStart = encryptedInputBytes;
    std::size_t rangeEnd = 0;
    for (std::size_t index = 0; index < bufferCount; ++index) {
        const auto& buffer = buffers[index];
        if (buffer.cbBuffer == 0) {
            continue;
        }
        if (!IsContinuationSliceType(buffer.BufferType) ||
            sliceCount == MaximumSchannelBuffers ||
            !TryResolveInputSlice(
                buffer,
                encryptedInput,
                encryptedInputBytes,
                false,
                &slices[sliceCount])) {
            return false;
        }
        const auto& slice = slices[sliceCount++];
        if (slice.offset < rangeStart) {
            rangeStart = slice.offset;
        }
        const std::size_t sliceEnd =
            slice.offset + slice.bytes;
        if (sliceEnd > rangeEnd) {
            rangeEnd = sliceEnd;
        }
    }
    if (sliceCount == 0 || rangeStart >= rangeEnd) {
        return false;
    }

    std::size_t coveredThrough = rangeStart;
    for (std::size_t step = 0;
         step < sliceCount && coveredThrough < rangeEnd;
         ++step) {
        std::size_t next = coveredThrough;
        for (std::size_t index = 0;
             index < sliceCount;
             ++index) {
            const auto& slice = slices[index];
            if (slice.offset <= coveredThrough &&
                slice.offset + slice.bytes > next) {
                next = slice.offset + slice.bytes;
            }
        }
        if (next == coveredThrough) {
            return false;
        }
        coveredThrough = next;
    }
    if (coveredThrough != rangeEnd) {
        return false;
    }

    const BufferSlice token{
        rangeStart,
        rangeEnd - rangeStart,
    };
    RetainInputSlice(
        encryptedInput,
        encryptedInputBytes,
        token);
    *tokenBytes = token.bytes;
    return true;
}

bool TryRetainSchannelExtraBuffer(
    const SecBuffer* buffers,
    std::size_t bufferCount,
    void* encryptedInput,
    std::size_t encryptedInputBytes,
    std::size_t encryptedInputCapacity,
    bool* found,
    std::size_t* retainedBytes) noexcept {
    if (buffers == nullptr ||
        bufferCount == 0 ||
        bufferCount > MaximumSchannelBuffers ||
        encryptedInput == nullptr ||
        encryptedInputBytes == 0 ||
        encryptedInputBytes > encryptedInputCapacity ||
        found == nullptr ||
        retainedBytes == nullptr) {
        return false;
    }

    *found = false;
    *retainedBytes = 0;
    BufferSlice extra{};
    if (!TryFindExtraSlice(
            buffers,
            bufferCount,
            encryptedInput,
            encryptedInputBytes,
            found,
            &extra)) {
        return false;
    }
    if (!*found) {
        return true;
    }
    RetainInputSlice(
        encryptedInput,
        encryptedInputBytes,
        extra);
    *retainedBytes = extra.bytes;
    return true;
}

} // namespace godswar::network::schannel_detail
