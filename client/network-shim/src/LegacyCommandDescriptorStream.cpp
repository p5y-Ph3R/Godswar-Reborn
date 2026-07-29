#include "LegacyCommandDescriptorStream.h"

#include <algorithm>
#include <cstring>

namespace godswar::network {

LegacyCommandDescriptorStream::
LegacyCommandDescriptorStream(
    ISecureLegacyFrameStream* outerStream) noexcept
    : outerStream_(outerStream) {
    InitializeSRWLock(&lock_);
}

LegacyCommandDescriptorStream::
~LegacyCommandDescriptorStream() noexcept {
    Stop();
    AcquireSRWLockExclusive(&lock_);
    SecureZeroMemory(queue_, sizeof(queue_));
    SecureZeroMemory(&current_, sizeof(current_));
    currentRemaining_ = 0;
    currentStarted_ = false;
    ReleaseSRWLockExclusive(&lock_);
}

bool LegacyCommandDescriptorStream::Enqueue(
    const LegacyPacketDescriptor& descriptor,
    std::uint64_t* token) noexcept {
    if (token == nullptr ||
        descriptor.packetBytes < 4 ||
        descriptor.packetBytes >
            SecureLegacyMaximumPacketBytes ||
        (descriptor.hasOperation &&
            (descriptor.operation.packetBytes !=
                    descriptor.packetBytes ||
                descriptor.operation.opcode !=
                    descriptor.opcode)) ||
        IsStopped()) {
        Fail(LegacyDescriptorFailure::InvalidArgument);
        return false;
    }

    AcquireSRWLockExclusive(&lock_);
    if (count_ == LegacyDescriptorQueueCapacity ||
        nextToken_ == 0) {
        ReleaseSRWLockExclusive(&lock_);
        Fail(LegacyDescriptorFailure::QueueFull);
        return false;
    }

    const std::size_t tail =
        (head_ + count_) % LegacyDescriptorQueueCapacity;
    queue_[tail].descriptor = descriptor;
    queue_[tail].token = nextToken_;
    *token = nextToken_;
    ++nextToken_;
    ++count_;
    ReleaseSRWLockExclusive(&lock_);
    return true;
}

bool LegacyCommandDescriptorStream::CancelUnstarted(
    std::uint64_t token) noexcept {
    if (token == 0 || IsStopped()) {
        return false;
    }

    AcquireSRWLockExclusive(&lock_);
    if (count_ == 0) {
        ReleaseSRWLockExclusive(&lock_);
        return false;
    }
    const std::size_t tail =
        (head_ + count_ - 1) %
        LegacyDescriptorQueueCapacity;
    if (queue_[tail].token != token) {
        ReleaseSRWLockExclusive(&lock_);
        return false;
    }

    SecureZeroMemory(
        &queue_[tail],
        sizeof(queue_[tail]));
    --count_;
    ReleaseSRWLockExclusive(&lock_);
    return true;
}

bool LegacyCommandDescriptorStream::CancelUnstartedOrStop(
    std::uint64_t token) noexcept {
    if (CancelUnstarted(token)) {
        return true;
    }

    Stop();
    return false;
}

ByteStreamIoResult LegacyCommandDescriptorStream::Read(
    void* destination,
    std::size_t destinationCapacity) noexcept {
    if (outerStream_ == nullptr || IsStopped()) {
        return {ByteStreamIoStatus::Failed, 0};
    }
    const auto result =
        outerStream_->Read(destination, destinationCapacity);
    if (result.status == ByteStreamIoStatus::Failed) {
        Fail(LegacyDescriptorFailure::PacketWrite);
    }
    return result;
}

ByteStreamIoResult LegacyCommandDescriptorStream::Write(
    const void* source,
    std::size_t sourceBytes) noexcept {
    if (source == nullptr ||
        sourceBytes == 0 ||
        outerStream_ == nullptr ||
        IsStopped()) {
        return {ByteStreamIoStatus::Failed, 0};
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(source);
    std::size_t offset = 0;
    while (offset < sourceBytes) {
        AcquireSRWLockShared(&lock_);
        const bool needsDescriptor = currentRemaining_ == 0;
        ReleaseSRWLockShared(&lock_);
        if (needsDescriptor && !BeginNextDescriptor()) {
            if (!IsStopped()) {
                Fail(
                    LegacyDescriptorFailure::
                        DescriptorMissing);
            }
            return {ByteStreamIoStatus::Failed, offset};
        }

        SecureLegacyCommandOperation operationValue{};
        bool hasOperation = false;
        std::size_t segment = 0;
        AcquireSRWLockExclusive(&lock_);
        if (IsStopped() || currentRemaining_ == 0) {
            ReleaseSRWLockExclusive(&lock_);
            SecureZeroMemory(
                &operationValue,
                sizeof(operationValue));
            return {ByteStreamIoStatus::Failed, offset};
        }
        segment = (std::min)(
            currentRemaining_,
            sourceBytes - offset);
        hasOperation =
            !currentStarted_ && current_.hasOperation;
        if (hasOperation) {
            operationValue = current_.operation;
        }
        currentStarted_ = true;
        ReleaseSRWLockExclusive(&lock_);

        const auto written =
            outerStream_->WriteDescribedLegacyBytes(
                hasOperation ? &operationValue : nullptr,
                bytes + offset,
                segment);
        SecureZeroMemory(
            &operationValue,
            sizeof(operationValue));
        if (written.status != ByteStreamIoStatus::Success ||
            written.bytesTransferred != segment) {
            Fail(LegacyDescriptorFailure::PacketWrite);
            return {ByteStreamIoStatus::Failed, offset};
        }

        AcquireSRWLockExclusive(&lock_);
        if (segment > currentRemaining_) {
            ReleaseSRWLockExclusive(&lock_);
            Fail(LegacyDescriptorFailure::PartialPacket);
            return {ByteStreamIoStatus::Failed, offset};
        }
        currentRemaining_ -= segment;
        offset += segment;
        if (currentRemaining_ == 0) {
            SecureZeroMemory(&current_, sizeof(current_));
            currentStarted_ = false;
        }
        ReleaseSRWLockExclusive(&lock_);
    }

    return {ByteStreamIoStatus::Success, sourceBytes};
}

void LegacyCommandDescriptorStream::Stop() noexcept {
    if (InterlockedCompareExchange(&stopped_, 1, 0) != 0) {
        return;
    }

    AcquireSRWLockExclusive(&lock_);
    if (currentRemaining_ != 0 && currentStarted_) {
        InterlockedCompareExchange(
            &failure_,
            static_cast<LONG>(
                LegacyDescriptorFailure::PartialPacket),
            static_cast<LONG>(
                LegacyDescriptorFailure::None));
    }
    for (auto& queued : queue_) {
        SecureZeroMemory(&queued, sizeof(queued));
    }
    head_ = 0;
    count_ = 0;
    ReleaseSRWLockExclusive(&lock_);

    InterlockedCompareExchange(
        &failure_,
        static_cast<LONG>(LegacyDescriptorFailure::Stopped),
        static_cast<LONG>(LegacyDescriptorFailure::None));
    if (outerStream_ != nullptr) {
        outerStream_->Stop();
    }
}

LegacyDescriptorStreamSnapshot
LegacyCommandDescriptorStream::Snapshot() const noexcept {
    LegacyDescriptorStreamSnapshot snapshot{};
    AcquireSRWLockShared(&lock_);
    snapshot.queuedDescriptors = count_;
    snapshot.currentPacketRemaining = currentRemaining_;
    ReleaseSRWLockShared(&lock_);
    snapshot.failure = static_cast<LegacyDescriptorFailure>(
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&failure_),
            0,
            0));
    snapshot.stopped = IsStopped();
    return snapshot;
}

bool LegacyCommandDescriptorStream::
BeginNextDescriptor() noexcept {
    AcquireSRWLockExclusive(&lock_);
    if (count_ == 0) {
        ReleaseSRWLockExclusive(&lock_);
        return false;
    }

    current_ = queue_[head_].descriptor;
    SecureZeroMemory(
        &queue_[head_],
        sizeof(queue_[head_]));
    head_ = (head_ + 1) % LegacyDescriptorQueueCapacity;
    --count_;
    currentRemaining_ = current_.packetBytes;
    currentStarted_ = false;
    ReleaseSRWLockExclusive(&lock_);
    return true;
}

void LegacyCommandDescriptorStream::Fail(
    LegacyDescriptorFailure failure) noexcept {
    InterlockedCompareExchange(
        &failure_,
        static_cast<LONG>(failure),
        static_cast<LONG>(LegacyDescriptorFailure::None));
    if (InterlockedCompareExchange(&stopped_, 1, 0) == 0 &&
        outerStream_ != nullptr) {
        outerStream_->Stop();
    }
}

bool LegacyCommandDescriptorStream::IsStopped() const noexcept {
    return InterlockedCompareExchange(
        const_cast<volatile LONG*>(&stopped_),
        0,
        0) != 0;
}

} // namespace godswar::network
