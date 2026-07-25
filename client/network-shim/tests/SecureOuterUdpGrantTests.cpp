#include "SecureOuterUdpGrantTests.h"

#include "SecureGameControlTestSupport.h"

#include "../src/SecureOuterStream.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <utility>
#include <vector>

namespace {

using godswar::network::ByteStreamIoStatus;
using godswar::network::DeadlineStreamResult;
using godswar::network::DeadlineStreamStatus;
using godswar::network::IDeadlinePlaintextStream;
using godswar::network::SecureBindResultBytes;
using godswar::network::SecureEndpointRole;
using godswar::network::SecureFrameDirection;
using godswar::network::SecureFrameHeader;
using godswar::network::SecureFrameHeaderBytes;
using godswar::network::SecureFrameType;
using godswar::network::SecureGameGrant;
using godswar::network::SecureOuterFailure;
using godswar::network::SecureOuterStream;
using godswar::network::SecureServerPrefaceBytes;
using godswar::network::SecureUdpBindingGrant;
using godswar::network::SecureUdpBindingGrantBytes;
using godswar::network::SecureUdpConnectionIdBytes;
using godswar::network::SecureUdpProofKeyBytes;
using godswar::network::TryEncodeSecureFrameHeader;
using godswar::network::tests::BuildSecureGrantTestBytes;
using godswar::network::tests::DecodeSecureGrantForTest;
using godswar::network::tests::WriteTestUInt16;
using godswar::network::tests::WriteTestUInt32;
using godswar::network::tests::WriteTestUInt64;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
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
    const auto* bytes = static_cast<const std::uint8_t*>(source);
    const auto previousBytes = destination->size();
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
        bytes,
        sourceBytes);
    *destination = std::move(expanded);
}

std::vector<std::uint8_t> ServerPreface() {
    std::vector<std::uint8_t> bytes(
        SecureServerPrefaceBytes,
        0);
    std::memcpy(bytes.data(), "GWSS", 4);
    WriteTestUInt16(bytes.data() + 4, SecureServerPrefaceBytes);
    WriteTestUInt16(bytes.data() + 6, 1);
    bytes[10] = 0;
    bytes[11] =
        static_cast<std::uint8_t>(SecureEndpointRole::Game);
    WriteTestUInt32(bytes.data() + 16, 16 * 1024);
    WriteTestUInt16(bytes.data() + 20, 30);
    WriteTestUInt16(bytes.data() + 22, 90);
    for (std::size_t index = 0;
         index < SecureUdpConnectionIdBytes;
         ++index) {
        bytes[24 + index] =
            static_cast<std::uint8_t>(index + 1);
    }
    return bytes;
}

std::array<std::uint8_t, SecureUdpBindingGrantBytes>
UdpGrant(bool matchingConnection = true) {
    std::array<std::uint8_t, SecureUdpBindingGrantBytes> bytes{};
    std::memcpy(bytes.data(), "GWUG", 4);
    WriteTestUInt16(bytes.data() + 4, 1);
    WriteTestUInt16(bytes.data() + 8, 7444);
    WriteTestUInt32(bytes.data() + 12, 100);
    WriteTestUInt64(bytes.data() + 16, 1'900'000'000'123ULL);
    for (std::size_t index = 0;
         index < SecureUdpConnectionIdBytes;
         ++index) {
        bytes[24 + index] =
            static_cast<std::uint8_t>(index + 1);
    }
    if (!matchingConnection) {
        bytes[24] ^= 1;
    }
    for (std::size_t index = 0;
         index < SecureUdpProofKeyBytes;
         ++index) {
        bytes[40 + index] =
            static_cast<std::uint8_t>(0x80U + index);
    }
    return bytes;
}

void AppendFrame(
    std::vector<std::uint8_t>* destination,
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
        "scripted UDP control frame did not encode");
    Append(destination, header, sizeof(header));
    Append(destination, payload, payloadBytes);
}

std::vector<std::uint8_t> BoundInput(
    const std::array<std::uint8_t, SecureUdpBindingGrantBytes>&
        udpGrant,
    bool duplicate = false,
    bool includeLegacy = true) {
    auto input = ServerPreface();
    const std::uint8_t bindResult[SecureBindResultBytes]{};
    AppendFrame(
        &input,
        SecureFrameType::BindResult,
        1,
        bindResult,
        sizeof(bindResult));
    AppendFrame(
        &input,
        SecureFrameType::UdpBindingGrant,
        2,
        udpGrant.data(),
        udpGrant.size());
    std::uint64_t sequence = 3;
    if (duplicate) {
        AppendFrame(
            &input,
            SecureFrameType::UdpBindingGrant,
            sequence++,
            udpGrant.data(),
            udpGrant.size());
    }
    if (includeLegacy) {
        constexpr std::uint8_t legacy = 0xA5;
        AppendFrame(
            &input,
            SecureFrameType::LegacyBytes,
            sequence,
            &legacy,
            sizeof(legacy));
    }
    return input;
}

class ScriptedStream final : public IDeadlinePlaintextStream {
public:
    explicit ScriptedStream(
        std::vector<std::uint8_t> input) noexcept
        : input_(std::move(input)) {
    }

    ~ScriptedStream() noexcept {
        if (!input_.empty()) {
            SecureZeroMemory(input_.data(), input_.size());
        }
        SecureZeroMemory(output.data(), output.size());
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
        const auto copied = (std::min)(
            destinationBytes,
            input_.size() - offset_);
        std::memcpy(
            destination,
            input_.data() + offset_,
            copied);
        offset_ += copied;
        return {DeadlineStreamStatus::Success, copied};
    }

    bool WriteAll(
        const void* source,
        std::size_t sourceBytes,
        ULONGLONG) noexcept override {
        if (stopped ||
            source == nullptr ||
            sourceBytes == 0 ||
            outputBytes + sourceBytes > output.size()) {
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

    bool stopped = false;
    std::array<std::uint8_t, 4096> output{};
    std::size_t outputBytes = 0;

private:
    std::vector<std::uint8_t> input_;
    std::size_t offset_ = 0;
};

bool EstablishGame(SecureOuterStream* outer) {
    std::uint8_t instance[16]{};
    std::uint8_t build[32]{};
    instance[0] = 1;
    build[0] = 2;
    return outer != nullptr &&
        outer->Establish(
            SecureEndpointRole::Game,
            instance,
            sizeof(instance),
            build,
            sizeof(build));
}

SecureGameGrant PresentableGrant() {
    return DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes());
}

void CheckRetainAndTakeWithoutBlockingLegacy() {
    const auto wireGrant = UdpGrant();
    ScriptedStream stream(BoundInput(wireGrant));
    SecureOuterStream outer(&stream);
    auto gameGrant = PresentableGrant();
    std::uint8_t connectionId[SecureUdpConnectionIdBytes]{};
    Check(
        EstablishGame(&outer) &&
            outer.TryCopyConnectionId(
                connectionId,
                sizeof(connectionId)) &&
            std::memcmp(
                connectionId,
                wireGrant.data() + 24,
                sizeof(connectionId)) == 0,
        "game TLS connection ID was not retained exactly");
    Check(
        outer.PresentGameBind(&gameGrant),
        "UDP grant fixture game bind failed");

    std::uint8_t legacy = 0;
    Check(
        outer.Read(&legacy, 1).status ==
                ByteStreamIoStatus::Success &&
            legacy == 0xA5 &&
            outer.Snapshot().hasUdpBindingGrant,
        "UDP grant blocked or leaked into the legacy TLS stream");

    SecureUdpBindingGrant retained;
    std::uint8_t proofKey[SecureUdpProofKeyBytes]{};
    Check(
        outer.TryTakeUdpBindingGrant(&retained) &&
            retained.IsValid() &&
            retained.UdpPort() == 7444 &&
            retained.ServerId() == 100 &&
            retained.TryCopyProofKey(
                proofKey,
                sizeof(proofKey)) &&
            std::memcmp(
                proofKey,
                wireGrant.data() + 40,
                sizeof(proofKey)) == 0 &&
            !outer.Snapshot().hasUdpBindingGrant,
        "bounded UDP grant take did not transfer exact ownership");
    Check(
        !outer.TryTakeUdpBindingGrant(&retained) &&
            !retained.IsValid(),
        "UDP grant was exposed more than once");
}

void CheckMismatchDuplicateAndOrderingReject() {
    {
        ScriptedStream stream(BoundInput(UdpGrant(false)));
        SecureOuterStream outer(&stream);
        auto gameGrant = PresentableGrant();
        std::uint8_t legacy = 0;
        std::uint8_t retainedId[SecureUdpConnectionIdBytes]{};
        Check(
            EstablishGame(&outer) &&
                outer.PresentGameBind(&gameGrant) &&
                outer.Read(&legacy, 1).status ==
                    ByteStreamIoStatus::Failed &&
                outer.Snapshot().failure ==
                    SecureOuterFailure::UdpGrantConnection &&
                !outer.TryCopyConnectionId(
                    retainedId,
                    sizeof(retainedId)),
            "mismatched TLS connection ID reached UDP state");
    }

    {
        ScriptedStream stream(BoundInput(
            UdpGrant(),
            true));
        SecureOuterStream outer(&stream);
        auto gameGrant = PresentableGrant();
        std::uint8_t legacy = 0;
        SecureUdpBindingGrant retained;
        Check(
            EstablishGame(&outer) &&
                outer.PresentGameBind(&gameGrant) &&
                outer.Read(&legacy, 1).status ==
                    ByteStreamIoStatus::Failed &&
                outer.Snapshot().failure ==
                    SecureOuterFailure::UdpGrantState &&
                !outer.TryTakeUdpBindingGrant(&retained),
            "duplicate UDP grant retained or exposed a proof key");
    }

    {
        auto input = ServerPreface();
        const auto udpGrant = UdpGrant();
        AppendFrame(
            &input,
            SecureFrameType::UdpBindingGrant,
            1,
            udpGrant.data(),
            udpGrant.size());
        ScriptedStream stream(std::move(input));
        SecureOuterStream outer(&stream);
        auto gameGrant = PresentableGrant();
        Check(
            EstablishGame(&outer) &&
                !outer.PresentGameBind(&gameGrant) &&
                outer.Snapshot().failure ==
                    SecureOuterFailure::BindResult,
            "UDP grant before accepted GameBind was accepted");
    }
}

void CheckMalformedAndStopClearOwnedSecrets() {
    auto malformed = UdpGrant();
    malformed[40] = 0;
    std::memset(
        malformed.data() + 40,
        0,
        SecureUdpProofKeyBytes);
    ScriptedStream malformedStream(BoundInput(malformed));
    SecureOuterStream malformedOuter(&malformedStream);
    auto malformedGameGrant = PresentableGrant();
    std::uint8_t legacy = 0;
    Check(
        EstablishGame(&malformedOuter) &&
            malformedOuter.PresentGameBind(
                &malformedGameGrant) &&
            malformedOuter.Read(&legacy, 1).status ==
                ByteStreamIoStatus::Failed &&
            malformedOuter.Snapshot().failure ==
                SecureOuterFailure::UdpGrantDecode,
        "malformed UDP grant was retained");

    const auto wireGrant = UdpGrant();
    ScriptedStream stopStream(BoundInput(wireGrant));
    SecureOuterStream stopOuter(&stopStream);
    auto stopGameGrant = PresentableGrant();
    Check(
        EstablishGame(&stopOuter) &&
            stopOuter.PresentGameBind(&stopGameGrant) &&
            stopOuter.Read(&legacy, 1).status ==
                ByteStreamIoStatus::Success,
        "stop-clear UDP fixture did not reach retained state");
    stopOuter.Stop();
    std::uint8_t connectionId[SecureUdpConnectionIdBytes];
    std::memset(connectionId, 0xCC, sizeof(connectionId));
    SecureUdpBindingGrant retained;
    Check(
        !stopOuter.TryCopyConnectionId(
            connectionId,
            sizeof(connectionId)) &&
            connectionId[0] == 0 &&
            connectionId[sizeof(connectionId) - 1] == 0 &&
            !stopOuter.TryTakeUdpBindingGrant(&retained) &&
            !stopOuter.Snapshot().hasUdpBindingGrant,
        "Stop retained owned UDP connection or proof material");
}

} // namespace

int RunSecureOuterUdpGrantTests() {
    Failures = 0;
    CheckRetainAndTakeWithoutBlockingLegacy();
    CheckMismatchDuplicateAndOrderingReject();
    CheckMalformedAndStopClearOwnedSecrets();
    return Failures;
}
