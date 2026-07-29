#include "SecureLegacyCommandResultStreamTests.h"

#include "SecureGameControlTestSupport.h"

#include "../src/SecureOuterStream.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

namespace {

using godswar::network::ByteStreamIoStatus;
using godswar::network::DeadlineStreamResult;
using godswar::network::DeadlineStreamStatus;
using godswar::network::IDeadlinePlaintextStream;
using godswar::network::LegacyPacketDescriptor;
using godswar::network::SecureBindResultBytes;
using godswar::network::SecureClientPrefaceBytes;
using godswar::network::SecureEndpointRole;
using godswar::network::SecureFrameDirection;
using godswar::network::SecureFrameHeader;
using godswar::network::SecureFrameHeaderBytes;
using godswar::network::SecureFrameType;
using godswar::network::SecureGameGrant;
using godswar::network::SecureLegacyCommandDisposition;
using godswar::network::SecureLegacyCommandFamily;
using godswar::network::SecureLegacyCommandResult;
using godswar::network::SecureOperationRegistryResult;
using godswar::network::SecureOuterFailure;
using godswar::network::SecureOuterStream;
using godswar::network::SecurePendingOperationRegistry;
using godswar::network::SecureServerPrefaceBytes;
using godswar::network::TryEncodeSecureFrameHeader;
using godswar::network::TryEncodeSecureLegacyCommandResult;
using godswar::network::tests::BuildSecureGrantTestBytes;
using godswar::network::tests::DecodeSecureGrantForTest;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void Write16(
    std::uint8_t* destination,
    std::uint16_t value) {
    destination[0] =
        static_cast<std::uint8_t>(value >> 8U);
    destination[1] = static_cast<std::uint8_t>(value);
}

void Write32Little(
    std::uint8_t* destination,
    std::uint32_t value) {
    for (std::size_t index = 0; index < 4; ++index) {
        destination[index] =
            static_cast<std::uint8_t>(
                value >> (index * 8U));
    }
}

void Append(
    std::vector<std::uint8_t>* destination,
    const void* source,
    std::size_t sourceBytes) {
    if (destination == nullptr ||
        source == nullptr ||
        sourceBytes == 0) {
        return;
    }
    const auto* bytes =
        static_cast<const std::uint8_t*>(source);
    const std::size_t previousBytes = destination->size();
    std::vector<std::uint8_t> expanded(
        previousBytes + sourceBytes);
    if (previousBytes != 0) {
        std::memcpy(
            expanded.data(),
            destination->data(),
            previousBytes);
    }
    std::memcpy(
        expanded.data() + previousBytes,
        bytes,
        sourceBytes);
    *destination =
        static_cast<std::vector<std::uint8_t>&&>(expanded);
}

std::vector<std::uint8_t> GamePreface() {
    std::vector<std::uint8_t> bytes(
        SecureServerPrefaceBytes,
        0);
    std::memcpy(bytes.data(), "GWSS", 4);
    Write16(bytes.data() + 4, SecureServerPrefaceBytes);
    Write16(bytes.data() + 6, 1);
    Write16(bytes.data() + 8, 0);
    bytes[10] = 0;
    bytes[11] =
        static_cast<std::uint8_t>(SecureEndpointRole::Game);
    bytes[16] = 0;
    bytes[17] = 0;
    bytes[18] = 0x40;
    bytes[19] = 0;
    Write16(bytes.data() + 20, 30);
    Write16(bytes.data() + 22, 90);
    for (std::size_t index = 24; index < 40; ++index) {
        bytes[index] =
            static_cast<std::uint8_t>(index);
    }
    return bytes;
}

void AppendFrame(
    std::vector<std::uint8_t>* input,
    SecureFrameType type,
    std::uint64_t sequence,
    const void* payload,
    std::size_t payloadBytes) {
    std::uint8_t header[SecureFrameHeaderBytes]{};
    Check(
        TryEncodeSecureFrameHeader(
            SecureFrameHeader{
                static_cast<std::uint32_t>(payloadBytes),
                type,
                sequence},
            SecureEndpointRole::Game,
            SecureFrameDirection::ServerToClient,
            header,
            sizeof(header)),
        "result-stream frame did not encode");
    Append(input, header, sizeof(header));
    Append(input, payload, payloadBytes);
}

class ScriptedStream final
    : public IDeadlinePlaintextStream {
public:
    explicit ScriptedStream(
        std::vector<std::uint8_t> input) noexcept
        : input_(static_cast<std::vector<std::uint8_t>&&>(
              input)) {
    }

    DeadlineStreamResult Read(
        void* destination,
        std::size_t destinationBytes,
        ULONGLONG) noexcept override {
        if (stopped ||
            destination == nullptr ||
            destinationBytes == 0) {
            return {DeadlineStreamStatus::Failed, 0};
        }
        if (offset_ == input_.size()) {
            return {DeadlineStreamStatus::EndOfStream, 0};
        }
        const std::size_t bytes = (std::min)(
            destinationBytes,
            input_.size() - offset_);
        std::memcpy(
            destination,
            input_.data() + offset_,
            bytes);
        offset_ += bytes;
        return {DeadlineStreamStatus::Success, bytes};
    }

    bool WriteAll(
        const void* source,
        std::size_t sourceBytes,
        ULONGLONG) noexcept override {
        if (stopped ||
            source == nullptr ||
            sourceBytes == 0 ||
            outputBytes + sourceBytes > sizeof(output)) {
            return false;
        }
        std::memcpy(
            output + outputBytes,
            source,
            sourceBytes);
        outputBytes += sourceBytes;
        return true;
    }

    void Stop() noexcept override {
        stopped = true;
    }

    bool stopped = false;
    std::uint8_t output[1024]{};
    std::size_t outputBytes = 0;

private:
    std::vector<std::uint8_t> input_;
    std::size_t offset_ = 0;
};

bool EstablishAndBind(
    SecureOuterStream* outer) {
    std::uint8_t instance[16]{};
    std::uint8_t origin[32]{};
    instance[0] = 1;
    origin[0] = 2;
    SecureGameGrant grant = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes());
    return outer->Establish(
            SecureEndpointRole::Game,
            instance,
            sizeof(instance),
            origin,
            sizeof(origin)) &&
        outer->PresentGameBind(&grant);
}

void WriteLegacyHeader(
    std::uint8_t* packet,
    std::uint16_t bytes,
    std::uint16_t opcode) {
    packet[0] = static_cast<std::uint8_t>(bytes);
    packet[1] =
        static_cast<std::uint8_t>(bytes >> 8U);
    packet[2] = static_cast<std::uint8_t>(opcode);
    packet[3] =
        static_cast<std::uint8_t>(opcode >> 8U);
}

LegacyPacketDescriptor SeedPending(
    SecurePendingOperationRegistry* registry) {
    std::uint8_t login[36]{};
    WriteLegacyHeader(login, sizeof(login), 10000);
    std::memcpy(login + 4, "test2", 5);
    std::uint8_t selection[16]{};
    WriteLegacyHeader(selection, sizeof(selection), 10193);
    Write32Little(selection + 4, 0);
    Write32Little(selection + 8, 5);
    selection[12] = 1;
    std::uint8_t final[92]{};
    WriteLegacyHeader(final, sizeof(final), 10069);
    Write32Little(final + 4, 5067);
    Write32Little(final + 8, 4);
    Write32Little(final + 16, 4);
    LegacyPacketDescriptor descriptor{};
    Check(
        registry->DescribePacket(
            login,
             sizeof(login),
             &descriptor) ==
                 SecureOperationRegistryResult::Success &&
            registry->SetCharacter(505) ==
                SecureOperationRegistryResult::Success &&
            registry->DescribePacket(
                selection,
                sizeof(selection),
                &descriptor) ==
                SecureOperationRegistryResult::Success &&
            registry->DescribePacket(
                final,
                sizeof(final),
                &descriptor) ==
                SecureOperationRegistryResult::Success &&
            descriptor.hasOperation,
        "result-stream pending operation setup failed");
    return descriptor;
}

std::vector<std::uint8_t> ResultInput(
    const std::uint8_t* resultPayload,
    const std::uint8_t* legacy = nullptr,
    bool duplicateResult = false) {
    auto input = GamePreface();
    const std::uint8_t bind[SecureBindResultBytes]{};
    AppendFrame(
        &input,
        SecureFrameType::BindResult,
        1,
        bind,
        sizeof(bind));
    AppendFrame(
        &input,
        SecureFrameType::LegacyCommandResult,
        2,
        resultPayload,
        32);
    std::uint64_t nextSequence = 3;
    if (duplicateResult) {
        AppendFrame(
            &input,
            SecureFrameType::LegacyCommandResult,
            nextSequence,
            resultPayload,
            32);
        ++nextSequence;
    }
    if (legacy != nullptr) {
        AppendFrame(
            &input,
            SecureFrameType::LegacyBytes,
            nextSequence,
            legacy,
            1);
    }
    return input;
}

void CheckConsumedResult() {
    SecurePendingOperationRegistry registry;
    const auto pending = SeedPending(&registry);
    SecureLegacyCommandResult result{};
    result.disposition =
        SecureLegacyCommandDisposition::Applied;
    result.commandFamily =
        SecureLegacyCommandFamily::MakeAttributeStone;
    result.resultCode = 1017;
    result.inventoryRevision = 12;
    std::memcpy(
        result.operationId,
        pending.operation.operationId,
        16);
    std::uint8_t payload[32]{};
    Check(
        TryEncodeSecureLegacyCommandResult(
            result,
            payload,
            sizeof(payload)),
        "result-stream payload did not encode");
    constexpr std::uint8_t legacy = 0xD7;
    ScriptedStream stream(
        ResultInput(payload, &legacy, true));
    SecureOuterStream outer(
        &stream,
        nullptr,
        &registry);
    std::uint8_t received = 0;
    Check(
        EstablishAndBind(&outer) &&
            outer.Read(&received, 1).status ==
                ByteStreamIoStatus::Success &&
            received == legacy &&
            registry.Snapshot().pending == 0 &&
            registry.Snapshot().resolved == 1 &&
            outer.Snapshot().nextInboundSequence == 5,
        "result or duplicate leaked to stock or did not resolve");
}

void CheckRejectedResults() {
    for (int scenario = 0; scenario < 3; ++scenario) {
        SecurePendingOperationRegistry registry;
        const auto pending = SeedPending(&registry);
        SecureLegacyCommandResult result{};
        result.disposition =
            SecureLegacyCommandDisposition::Rejected;
        result.commandFamily =
            SecureLegacyCommandFamily::MakeAttributeStone;
        std::memcpy(
            result.operationId,
            pending.operation.operationId,
            16);
        std::uint8_t payload[32]{};
        Check(
            TryEncodeSecureLegacyCommandResult(
                result,
                payload,
                sizeof(payload)),
            "rejected-result payload setup failed");
        if (scenario == 0) {
            payload[16] ^= 0x5A;
        } else if (scenario == 1) {
            payload[0] = 2;
        } else {
            payload[2] = 0;
            payload[3] = 7;
        }

        ScriptedStream stream(ResultInput(payload));
        SecureOuterStream outer(
            &stream,
            nullptr,
            &registry);
        std::uint8_t received = 0;
        Check(
            EstablishAndBind(&outer) &&
                outer.Read(&received, 1).status ==
                    ByteStreamIoStatus::Failed &&
                outer.Snapshot().failure ==
                    SecureOuterFailure::
                        LegacyCommandResult &&
                registry.Snapshot().pending == 1,
            "unknown or malformed result did not fail closed");
    }
}

} // namespace

int RunSecureLegacyCommandResultStreamTests() {
    Failures = 0;
    CheckConsumedResult();
    CheckRejectedResults();
    return Failures;
}
