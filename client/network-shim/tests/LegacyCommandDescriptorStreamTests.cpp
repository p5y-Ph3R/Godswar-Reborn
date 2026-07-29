#include "LegacyCommandDescriptorStreamTests.h"

#include "../src/LegacyCommandDescriptorStream.h"
#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::ByteStreamIoResult;
using godswar::network::ByteStreamIoStatus;
using godswar::network::ISecureLegacyFrameStream;
using godswar::network::LegacyCommandDescriptorStream;
using godswar::network::LegacyDescriptorFailure;
using godswar::network::LegacyDescriptorQueueCapacity;
using godswar::network::LegacyPacketDescriptor;
using godswar::network::SecureLegacyCommandOperation;
using godswar::network::SecureOperationRegistryResult;
using godswar::network::SecurePendingOperationRegistry;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

class RecordingFrameStream final
    : public ISecureLegacyFrameStream {
public:
    struct WriteRecord final {
        bool hasOperation = false;
        std::uint8_t operationId[16]{};
        std::size_t offset = 0;
        std::size_t bytes = 0;
    };

    ByteStreamIoResult Read(
        void*,
        std::size_t) noexcept override {
        return {ByteStreamIoStatus::EndOfStream, 0};
    }

    ByteStreamIoResult Write(
        const void* source,
        std::size_t sourceBytes) noexcept override {
        return WriteDescribedLegacyBytes(
            nullptr,
            source,
            sourceBytes);
    }

    ByteStreamIoResult WriteDescribedLegacyBytes(
        const SecureLegacyCommandOperation* operation,
        const void* source,
        std::size_t sourceBytes) noexcept override {
        if (stopped ||
            failWrites ||
            source == nullptr ||
            sourceBytes == 0 ||
            recordCount >= 32 ||
            outputBytes + sourceBytes > sizeof(output)) {
            return {ByteStreamIoStatus::Failed, 0};
        }

        auto& record = records[recordCount++];
        record.hasOperation = operation != nullptr;
        if (operation != nullptr) {
            std::memcpy(
                record.operationId,
                operation->operationId,
                sizeof(record.operationId));
        }
        record.offset = outputBytes;
        record.bytes = sourceBytes;
        std::memcpy(
            output + outputBytes,
            source,
            sourceBytes);
        outputBytes += sourceBytes;
        return {ByteStreamIoStatus::Success, sourceBytes};
    }

    void Stop() noexcept override {
        stopped = true;
    }

    bool failWrites = false;
    bool stopped = false;
    std::uint8_t output[512]{};
    std::size_t outputBytes = 0;
    WriteRecord records[32]{};
    std::size_t recordCount = 0;
};

LegacyPacketDescriptor Descriptor(
    std::uint16_t bytes,
    std::uint16_t opcode,
    std::uint8_t operationSeed = 0) {
    LegacyPacketDescriptor descriptor{};
    descriptor.packetBytes = bytes;
    descriptor.opcode = opcode;
    if (operationSeed != 0) {
        descriptor.hasOperation = true;
        descriptor.operation.packetBytes = bytes;
        descriptor.operation.opcode = opcode;
        for (std::size_t index = 0; index < 16; ++index) {
            descriptor.operation.operationId[index] =
                static_cast<std::uint8_t>(
                    operationSeed + index);
        }
    }
    return descriptor;
}

void CheckSplitCoalescedAndOrder() {
    RecordingFrameStream frameStream;
    LegacyCommandDescriptorStream stream(&frameStream);
    std::uint64_t firstToken = 0;
    std::uint64_t secondToken = 0;
    Check(
        stream.Enqueue(
            Descriptor(4, 10001),
            &firstToken) &&
            stream.Enqueue(
                Descriptor(6, 10069, 0x40),
                &secondToken) &&
            firstToken != secondToken,
        "descriptor queue setup failed");

    const std::uint8_t coalesced[] = {
        1, 2, 3, 4,
        5, 6, 7, 8, 9, 10};
    const auto result =
        stream.Write(coalesced, sizeof(coalesced));
    Check(
        result.status == ByteStreamIoStatus::Success &&
            result.bytesTransferred == sizeof(coalesced) &&
            frameStream.recordCount == 2 &&
            !frameStream.records[0].hasOperation &&
            frameStream.records[0].bytes == 4 &&
            frameStream.records[1].hasOperation &&
            frameStream.records[1].bytes == 6 &&
            std::memcmp(
                frameStream.output,
                coalesced,
                sizeof(coalesced)) == 0,
        "coalesced packets changed marker order or ciphertext");

    RecordingFrameStream splitFrameStream;
    LegacyCommandDescriptorStream splitStream(
        &splitFrameStream);
    std::uint64_t splitToken = 0;
    Check(
        splitStream.Enqueue(
            Descriptor(6, 10069, 0x70),
            &splitToken),
        "split descriptor enqueue failed");
    Check(
        splitStream.Write(coalesced, 2).status ==
                ByteStreamIoStatus::Success &&
            splitStream.Write(coalesced + 2, 4).status ==
                ByteStreamIoStatus::Success &&
            splitFrameStream.recordCount == 2 &&
            splitFrameStream.records[0].hasOperation &&
            !splitFrameStream.records[1].hasOperation &&
            splitFrameStream.records[0].bytes == 2 &&
            splitFrameStream.records[1].bytes == 4 &&
            std::memcmp(
                splitFrameStream.output,
                coalesced,
                6) == 0,
        "split packet repeated or reordered its marker");
}

void CheckCancellationAndMismatch() {
    RecordingFrameStream frameStream;
    LegacyCommandDescriptorStream stream(&frameStream);
    std::uint64_t first = 0;
    std::uint64_t second = 0;
    Check(
        stream.Enqueue(Descriptor(4, 1), &first) &&
            stream.Enqueue(Descriptor(4, 2), &second) &&
            !stream.CancelUnstarted(first) &&
            stream.CancelUnstarted(second),
        "descriptor cancellation was not tail-and-unstarted");

    const std::uint8_t packet[] = {4, 0, 1, 0};
    Check(
        stream.Write(packet, sizeof(packet)).status ==
                ByteStreamIoStatus::Success &&
            stream.Write(packet, sizeof(packet)).status ==
                ByteStreamIoStatus::Failed &&
            stream.Snapshot().failure ==
                LegacyDescriptorFailure::DescriptorMissing &&
            frameStream.stopped,
        "missing descriptor did not fail closed");

    RecordingFrameStream partialFrameStream;
    LegacyCommandDescriptorStream partialStream(
        &partialFrameStream);
    std::uint64_t token = 0;
    Check(
        partialStream.Enqueue(
            Descriptor(8, 3),
            &token) &&
            partialStream.Write(packet, sizeof(packet)).status ==
                ByteStreamIoStatus::Success,
        "partial descriptor fixture failed");
    const std::size_t partialBytesBeforeFailure =
        partialFrameStream.outputBytes;
    Check(
        !partialStream.CancelUnstartedOrStop(token) &&
            partialStream.Snapshot().failure ==
                LegacyDescriptorFailure::PartialPacket &&
            partialFrameStream.stopped &&
            partialStream.Write(packet, sizeof(packet)).status ==
                ByteStreamIoStatus::Failed &&
            partialFrameStream.outputBytes ==
                partialBytesBeforeFailure,
        "partial-start stock failure allowed the next send");
}

void CheckCapacity() {
    RecordingFrameStream frameStream;
    LegacyCommandDescriptorStream stream(&frameStream);
    bool withinCapacity = true;
    for (std::size_t index = 0;
         index < LegacyDescriptorQueueCapacity;
         ++index) {
        std::uint64_t token = 0;
        withinCapacity =
            withinCapacity &&
            stream.Enqueue(
                Descriptor(
                    4,
                    static_cast<std::uint16_t>(index)),
                &token);
    }
    std::uint64_t overflowToken = 0;
    Check(
        withinCapacity &&
            !stream.Enqueue(
                Descriptor(4, 99),
                &overflowToken) &&
            stream.Snapshot().failure ==
                LegacyDescriptorFailure::QueueFull &&
            frameStream.stopped,
        "descriptor queue capacity was not fail-closed");
}

void Write16(
    std::uint8_t* destination,
    std::uint16_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] =
        static_cast<std::uint8_t>(value >> 8U);
}

void Write32(
    std::uint8_t* destination,
    std::uint32_t value) {
    for (std::size_t index = 0; index < 4; ++index) {
        destination[index] = static_cast<std::uint8_t>(
            value >> (index * 8U));
    }
}

struct IdentityHooks final {
    std::uint64_t now = 10;
    std::uint8_t seed = 1;
};

bool IdentityRandom(
    void* contextValue,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* context =
        static_cast<IdentityHooks*>(contextValue);
    if (context == nullptr ||
        destination == nullptr ||
        destinationBytes != 16) {
        return false;
    }
    auto* bytes = static_cast<std::uint8_t*>(destination);
    for (std::size_t index = 0; index < 16; ++index) {
        bytes[index] =
            static_cast<std::uint8_t>(
                context->seed + index);
    }
    ++context->seed;
    return true;
}

bool IdentityClock(
    void* contextValue,
    std::uint64_t* now) noexcept {
    auto* context =
        static_cast<IdentityHooks*>(contextValue);
    if (context == nullptr || now == nullptr) {
        return false;
    }
    *now = context->now;
    return true;
}

void CheckIdentityToWireOrdering() {
    IdentityHooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        IdentityRandom,
        &hooks,
        IdentityClock);
    RecordingFrameStream frameStream;
    LegacyCommandDescriptorStream stream(&frameStream);

    std::uint8_t login[36]{};
    Write16(login, sizeof(login));
    Write16(login + 2, 10000);
    std::memcpy(login + 4, "test2", 5);
    std::uint8_t select[16]{};
    Write16(select, sizeof(select));
    Write16(select + 2, 10193);
    Write32(select + 4, 1);
    Write32(select + 8, 3);
    select[12] = 1;
    std::uint8_t final[92]{};
    Write16(final, sizeof(final));
    Write16(final + 2, 10069);
    Write32(final + 4, 5067);
    Write32(final + 8, 4);
    Write32(final + 16, 4);

    const std::uint8_t* packets[] = {
        login,
        select,
        final};
    const std::size_t lengths[] = {
        sizeof(login),
        sizeof(select),
        sizeof(final)};
    bool prepared = true;
    for (std::size_t index = 0; index < 3; ++index) {
        LegacyPacketDescriptor descriptor{};
        std::uint64_t token = 0;
        prepared =
            prepared &&
            registry.DescribePacket(
                packets[index],
                lengths[index],
                &descriptor) ==
                SecureOperationRegistryResult::Success;
        if (index == 0) {
            prepared =
                prepared &&
                registry.SetCharacter(404) ==
                    SecureOperationRegistryResult::Success;
        }
        prepared =
            prepared &&
            stream.Enqueue(descriptor, &token);
    }

    std::uint8_t encrypted[144]{};
    for (std::size_t index = 0; index < sizeof(encrypted); ++index) {
        encrypted[index] =
            static_cast<std::uint8_t>(index ^ 0xA5U);
    }
    Check(
        prepared &&
            stream.Write(
                encrypted,
                sizeof(encrypted)).status ==
                ByteStreamIoStatus::Success &&
            frameStream.recordCount == 3 &&
            !frameStream.records[0].hasOperation &&
            !frameStream.records[1].hasOperation &&
            frameStream.records[2].hasOperation &&
            frameStream.records[0].bytes == sizeof(login) &&
            frameStream.records[1].bytes == sizeof(select) &&
            frameStream.records[2].bytes == sizeof(final) &&
            std::memcmp(
                frameStream.output,
                encrypted,
                sizeof(encrypted)) == 0,
        "proxy-style identity-to-wire path misplaced marker");
}

} // namespace

int RunLegacyCommandDescriptorStreamTests() {
    Failures = 0;
    CheckSplitCoalescedAndOrder();
    CheckCancellationAndMismatch();
    CheckCapacity();
    CheckIdentityToWireOrdering();
    return Failures;
}
