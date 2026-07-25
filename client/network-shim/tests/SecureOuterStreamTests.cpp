#include "SecureOuterStreamTests.h"

#include "../src/SecureOuterStream.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <vector>

namespace {

using godswar::network::ByteStreamIoStatus;
using godswar::network::DeadlineStreamResult;
using godswar::network::DeadlineStreamStatus;
using godswar::network::IDeadlinePlaintextStream;
using godswar::network::SecureClientPrefaceBytes;
using godswar::network::SecureEndpointRole;
using godswar::network::SecureFrameDirection;
using godswar::network::SecureFrameHeader;
using godswar::network::SecureFrameHeaderBytes;
using godswar::network::SecureFrameType;
using godswar::network::SecureOuterFailure;
using godswar::network::SecureOuterStream;
using godswar::network::SecureServerPrefaceBytes;
using godswar::network::SecureServerPrefaceStatus;
using godswar::network::TryDecodeSecureFrameHeader;
using godswar::network::TryEncodeSecureFrameHeader;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void WriteUInt16(
    std::uint8_t* destination,
    std::uint16_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value >> 8U);
    destination[1] = static_cast<std::uint8_t>(value);
}

void WriteUInt32(
    std::uint8_t* destination,
    std::uint32_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value >> 24U);
    destination[1] = static_cast<std::uint8_t>(value >> 16U);
    destination[2] = static_cast<std::uint8_t>(value >> 8U);
    destination[3] = static_cast<std::uint8_t>(value);
}

void AppendBytes(
    std::vector<std::uint8_t>* destination,
    const std::uint8_t* source,
    std::size_t sourceBytes) {
    if (destination == nullptr ||
        source == nullptr ||
        sourceBytes == 0) {
        return;
    }
    const std::size_t previousBytes = destination->size();
    std::vector<std::uint8_t> expanded(
        previousBytes + sourceBytes);
    if (previousBytes > 0) {
        std::memcpy(
            expanded.data(),
            destination->data(),
            previousBytes);
    }
    std::memcpy(
        expanded.data() + previousBytes,
        source,
        sourceBytes);
    *destination =
        static_cast<std::vector<std::uint8_t>&&>(expanded);
}

std::vector<std::uint8_t> ServerPreface(
    SecureServerPrefaceStatus status =
        SecureServerPrefaceStatus::Ok,
    SecureEndpointRole role =
        SecureEndpointRole::Login) {
    std::vector<std::uint8_t> bytes(
        SecureServerPrefaceBytes,
        0);
    std::memcpy(bytes.data(), "GWSS", 4);
    WriteUInt16(bytes.data() + 4, SecureServerPrefaceBytes);
    WriteUInt16(bytes.data() + 6, 1);
    WriteUInt16(bytes.data() + 8, 0);
    bytes[10] = static_cast<std::uint8_t>(status);
    bytes[11] = static_cast<std::uint8_t>(role);
    WriteUInt32(bytes.data() + 16, 16 * 1024);
    WriteUInt16(bytes.data() + 20, 30);
    WriteUInt16(bytes.data() + 22, 90);
    if (status == SecureServerPrefaceStatus::Ok) {
        for (std::size_t index = 24; index < bytes.size(); ++index) {
            bytes[index] = static_cast<std::uint8_t>(index - 23);
        }
    }
    return bytes;
}

void AppendFrame(
    std::vector<std::uint8_t>* destination,
    SecureFrameType type,
    std::uint64_t sequence,
    const std::vector<std::uint8_t>& payload) {
    if (destination == nullptr) {
        return;
    }
    std::uint8_t header[SecureFrameHeaderBytes]{};
    const bool encoded = TryEncodeSecureFrameHeader(
        SecureFrameHeader{
            static_cast<std::uint32_t>(payload.size()),
            type,
            sequence},
        SecureEndpointRole::Login,
        SecureFrameDirection::ServerToClient,
        header,
        sizeof(header));
    Check(encoded, "scripted server frame did not encode");
    AppendBytes(destination, header, sizeof(header));
    AppendBytes(
        destination,
        payload.data(),
        payload.size());
}

class ScriptedStream final : public IDeadlinePlaintextStream {
public:
    explicit ScriptedStream(
        std::vector<std::uint8_t> input) noexcept
        : input_(static_cast<std::vector<std::uint8_t>&&>(input)) {
    }

    DeadlineStreamResult Read(
        void* destination,
        std::size_t destinationBytes,
        ULONGLONG absoluteDeadline) noexcept override {
        if (deadlineCount <
            sizeof(deadlines) / sizeof(deadlines[0])) {
            deadlines[deadlineCount++] = absoluteDeadline;
        }
        if (stopped || destination == nullptr || destinationBytes == 0) {
            return {DeadlineStreamStatus::Failed, 0};
        }
        if (forceSuccessWithZero) {
            forceSuccessWithZero = false;
            return {DeadlineStreamStatus::Success, 0};
        }
        if (forceTimeout) {
            forceTimeout = false;
            return {DeadlineStreamStatus::TimedOut, 0};
        }
        if (offset_ == input_.size()) {
            return {DeadlineStreamStatus::EndOfStream, 0};
        }

        const std::size_t count = (std::min)(
            (std::min)(destinationBytes, maximumReadBytes),
            input_.size() - offset_);
        std::memcpy(
            destination,
            input_.data() + offset_,
            count);
        offset_ += count;
        return {DeadlineStreamStatus::Success, count};
    }

    bool WriteAll(
        const void* source,
        std::size_t sourceBytes,
        ULONGLONG absoluteDeadline) noexcept override {
        if (deadlineCount <
            sizeof(deadlines) / sizeof(deadlines[0])) {
            deadlines[deadlineCount++] = absoluteDeadline;
        }
        if (stopped ||
            failWrites ||
            source == nullptr ||
            sourceBytes == 0) {
            return false;
        }
        if (sourceBytes > output.size() - outputBytes) {
            return false;
        }
        std::memcpy(
            output.data() + outputBytes,
            source,
            sourceBytes);
        outputBytes += sourceBytes;
        return true;
    }

    void Stop() noexcept override {
        stopped = true;
    }

    std::size_t maximumReadBytes =
        (std::numeric_limits<std::size_t>::max)();
    bool forceSuccessWithZero = false;
    bool forceTimeout = false;
    bool failWrites = false;
    bool stopped = false;
    std::vector<std::uint8_t> output =
        std::vector<std::uint8_t>(128 * 1024);
    std::size_t outputBytes = 0;
    ULONGLONG deadlines[128]{};
    std::size_t deadlineCount = 0;

private:
    std::vector<std::uint8_t> input_;
    std::size_t offset_ = 0;
};

bool Establish(SecureOuterStream* outer) {
    std::uint8_t instance[16]{};
    std::uint8_t build[32]{};
    instance[0] = 1;
    build[0] = 2;
    return outer != nullptr &&
        outer->Establish(
            SecureEndpointRole::Login,
            instance,
            sizeof(instance),
            build,
            sizeof(build));
}

void CheckPrefaceAndLegacyStream() {
    auto input = ServerPreface();
    AppendFrame(
        &input,
        SecureFrameType::LegacyBytes,
        1,
        {1, 2, 3, 4, 5});
    AppendFrame(
        &input,
        SecureFrameType::LegacyBytes,
        2,
        {6, 7});
    ScriptedStream stream(
        static_cast<std::vector<std::uint8_t>&&>(input));
    stream.maximumReadBytes = 3;
    SecureOuterStream outer(&stream);

    Check(Establish(&outer), "partial secure preface failed");
    Check(
        stream.outputBytes == SecureClientPrefaceBytes &&
            std::memcmp(stream.output.data(), "GWSC", 4) == 0,
        "client preface bytes were not emitted exactly once");

    std::uint8_t bytes[8]{};
    auto first = outer.Read(bytes, 2);
    auto second = outer.Read(bytes + 2, 4);
    auto third = outer.Read(bytes + 5, 3);
    Check(
        first.status == ByteStreamIoStatus::Success &&
            first.bytesTransferred == 2 &&
            second.status == ByteStreamIoStatus::Success &&
            second.bytesTransferred == 3 &&
            third.status == ByteStreamIoStatus::Success &&
            third.bytesTransferred == 2 &&
            std::memcmp(bytes, "\x01\x02\x03\x04\x05\x06\x07", 7) == 0,
        "partial/coalesced legacy frames changed stream bytes");

    const auto snapshot = outer.Snapshot();
    Check(
        snapshot.established &&
            snapshot.nextInboundSequence == 3 &&
            snapshot.nextOutboundSequence == 1,
        "secure stream sequence snapshot was incorrect");
}

void CheckPingPongAndWrite() {
    auto input = ServerPreface();
    const std::vector<std::uint8_t> nonce{
        9, 8, 7, 6, 5, 4, 3, 2};
    AppendFrame(&input, SecureFrameType::Ping, 1, nonce);
    AppendFrame(
        &input,
        SecureFrameType::LegacyBytes,
        2,
        {0xAA});
    ScriptedStream stream(
        static_cast<std::vector<std::uint8_t>&&>(input));
    SecureOuterStream outer(&stream);
    Check(Establish(&outer), "ping fixture preface failed");

    std::uint8_t received = 0;
    const auto read = outer.Read(&received, 1);
    Check(
        read.status == ByteStreamIoStatus::Success &&
            received == 0xAA,
        "Ping did not remain outside the legacy byte stream");

    const std::size_t pongOffset = SecureClientPrefaceBytes;
    Check(
        stream.outputBytes ==
            pongOffset + SecureFrameHeaderBytes + nonce.size(),
        "Pong output length was incorrect");
    SecureFrameHeader pong{};
    Check(
        TryDecodeSecureFrameHeader(
            stream.output.data() + pongOffset,
            SecureFrameHeaderBytes,
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            1,
            &pong) &&
            pong.type == SecureFrameType::Pong &&
            std::memcmp(
                stream.output.data() +
                    pongOffset +
                    SecureFrameHeaderBytes,
                nonce.data(),
                nonce.size()) == 0,
        "Pong did not echo the exact nonce and sequence");

    const std::uint8_t legacy[] = {0x10, 0x20, 0x30};
    const auto write = outer.Write(legacy, sizeof(legacy));
    Check(
        write.status == ByteStreamIoStatus::Success &&
            write.bytesTransferred == sizeof(legacy),
        "legacy payload write failed");
    const std::size_t legacyOffset =
        pongOffset + SecureFrameHeaderBytes + nonce.size();
    SecureFrameHeader legacyHeader{};
    Check(
        TryDecodeSecureFrameHeader(
            stream.output.data() + legacyOffset,
            SecureFrameHeaderBytes,
            SecureEndpointRole::Login,
            SecureFrameDirection::ClientToServer,
            2,
            &legacyHeader) &&
            legacyHeader.type == SecureFrameType::LegacyBytes,
        "legacy payload used the wrong outbound sequence");
}

void CheckCloseAndMalformedInput() {
    auto closeInput = ServerPreface();
    AppendFrame(
        &closeInput,
        SecureFrameType::Close,
        1,
        {0, 0, 0, 0});
    ScriptedStream closeStream(
        static_cast<std::vector<std::uint8_t>&&>(closeInput));
    SecureOuterStream closeOuter(&closeStream);
    Check(Establish(&closeOuter), "close fixture preface failed");
    std::uint8_t byte = 0;
    Check(
        closeOuter.Read(&byte, 1).status ==
            ByteStreamIoStatus::EndOfStream,
        "canonical Close did not end the outer stream");

    auto truncatedInput = ServerPreface();
    std::uint8_t header[SecureFrameHeaderBytes]{};
    Check(
        TryEncodeSecureFrameHeader(
            SecureFrameHeader{
                1,
                SecureFrameType::LegacyBytes,
                1},
            SecureEndpointRole::Login,
            SecureFrameDirection::ServerToClient,
            header,
            sizeof(header)),
        "truncated header fixture did not encode");
    AppendBytes(&truncatedInput, header, 5);
    ScriptedStream truncatedStream(
        static_cast<std::vector<std::uint8_t>&&>(truncatedInput));
    SecureOuterStream truncatedOuter(&truncatedStream);
    Check(Establish(&truncatedOuter), "truncated fixture preface failed");
    Check(
        truncatedOuter.Read(&byte, 1).status ==
            ByteStreamIoStatus::Failed &&
            truncatedOuter.Snapshot().failure ==
                SecureOuterFailure::FrameHeader,
        "partial header EOF was treated as graceful EOF");

    ScriptedStream zeroStream(ServerPreface());
    SecureOuterStream zeroOuter(&zeroStream);
    Check(Establish(&zeroOuter), "zero-read fixture preface failed");
    zeroStream.forceSuccessWithZero = true;
    Check(
        zeroOuter.Read(&byte, 1).status ==
            ByteStreamIoStatus::Failed,
        "successful zero-byte read was accepted");
}

void CheckFailureAndStop() {
    ScriptedStream rejected(
        ServerPreface(
            SecureServerPrefaceStatus::UnsupportedBuild));
    SecureOuterStream rejectedOuter(&rejected);
    Check(
        !Establish(&rejectedOuter) &&
            rejectedOuter.Snapshot().failure ==
                SecureOuterFailure::PrefaceRejected &&
            rejected.stopped,
        "rejected preface did not fail closed");

    ScriptedStream timed(ServerPreface());
    timed.forceTimeout = true;
    SecureOuterStream timedOuter(&timed);
    Check(
        !Establish(&timedOuter) &&
            timedOuter.Snapshot().failure ==
                SecureOuterFailure::OperationDeadline,
        "preface timeout did not report a finite failure");

    ScriptedStream stopped(ServerPreface());
    SecureOuterStream stoppedOuter(&stopped);
    Check(Establish(&stoppedOuter), "stop fixture preface failed");
    stoppedOuter.Stop();
    Check(
        stoppedOuter.Snapshot().stopped &&
            stopped.stopped,
        "Stop did not propagate to the plaintext stream");
}

} // namespace

int RunSecureOuterStreamTests() {
    Failures = 0;
    CheckPrefaceAndLegacyStream();
    CheckPingPongAndWrite();
    CheckCloseAndMalformedInput();
    CheckFailureAndStop();
    return Failures;
}
