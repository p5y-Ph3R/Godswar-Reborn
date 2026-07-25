#include "SecureOuterControlTests.h"

#include "SecureGameControlTestSupport.h"

#include "../src/ClientRoute.h"
#include "../src/SecureGameGrantRegistry.h"
#include "../src/SecureOuterStream.h"

#include <Windows.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <utility>
#include <vector>

namespace {

using godswar::network::ByteStreamIoStatus;
using godswar::network::ClientRoute;
using godswar::network::DeadlineStreamResult;
using godswar::network::DeadlineStreamStatus;
using godswar::network::IDeadlinePlaintextStream;
using godswar::network::SecureBindResultBytes;
using godswar::network::SecureEndpointRole;
using godswar::network::SecureFrameDirection;
using godswar::network::SecureFrameHeader;
using godswar::network::SecureFrameHeaderBytes;
using godswar::network::SecureFrameType;
using godswar::network::SecureGameBindBytes;
using godswar::network::SecureGameGrant;
using godswar::network::SecureGameGrantClaim;
using godswar::network::SecureGameGrantPolicy;
using godswar::network::SecureGameGrantRegistry;
using godswar::network::SecureGameGrantResult;
using godswar::network::SecureGameGrantState;
using godswar::network::SecureOuterFailure;
using godswar::network::SecureOuterStream;
using godswar::network::SecureServerPrefaceBytes;
using godswar::network::TryCopyClientRoute;
using godswar::network::TryDecodeSecureFrameHeader;
using godswar::network::TryEncodeSecureFrameHeader;
using godswar::network::tests::BuildSecureGrantTestBytes;
using godswar::network::tests::BuildSecureGrantTestManifest;
using godswar::network::tests::DecodeSecureGrantForTest;
using godswar::network::tests::SecureGrantTestBytes;
using godswar::network::tests::SecureGrantTestClock;
using godswar::network::tests::TestClock;
using godswar::network::tests::WriteTestUInt16;
using godswar::network::tests::WriteTestUInt32;

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
        bytes,
        sourceBytes);
    *destination = std::move(expanded);
}

std::vector<std::uint8_t> ServerPreface(
    SecureEndpointRole role) {
    std::vector<std::uint8_t> bytes(
        SecureServerPrefaceBytes,
        0);
    std::memcpy(bytes.data(), "GWSS", 4);
    WriteTestUInt16(
        bytes.data() + 4,
        static_cast<std::uint16_t>(SecureServerPrefaceBytes));
    WriteTestUInt16(bytes.data() + 6, 1);
    WriteTestUInt16(bytes.data() + 8, 0);
    bytes[10] = 0;
    bytes[11] = static_cast<std::uint8_t>(role);
    WriteTestUInt32(bytes.data() + 16, 16 * 1024);
    WriteTestUInt16(bytes.data() + 20, 30);
    WriteTestUInt16(bytes.data() + 22, 90);
    for (std::size_t index = 24; index < bytes.size(); ++index) {
        bytes[index] =
            static_cast<std::uint8_t>(index - 23);
    }
    return bytes;
}

void AppendFrame(
    std::vector<std::uint8_t>* destination,
    SecureEndpointRole role,
    SecureFrameDirection direction,
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
            role,
            direction,
            header,
            sizeof(header)),
        "scripted secure control frame did not encode");
    Append(destination, header, sizeof(header));
    Append(destination, payload, payloadBytes);
}

class ScriptedStream final : public IDeadlinePlaintextStream {
public:
    explicit ScriptedStream(
        std::vector<std::uint8_t> input) noexcept
        : input_(std::move(input)),
          output(128 * 1024) {
    }

    ~ScriptedStream() noexcept {
        if (!input_.empty()) {
            SecureZeroMemory(input_.data(), input_.size());
        }
        if (!output.empty()) {
            SecureZeroMemory(output.data(), output.size());
        }
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
        if (forceTimeout) {
            forceTimeout = false;
            return {DeadlineStreamStatus::TimedOut, 0};
        }
        if (offset_ == input_.size()) {
            return {DeadlineStreamStatus::EndOfStream, 0};
        }
        const std::size_t copied = (std::min)(
            (std::min)(destinationBytes, maximumReadBytes),
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
            failWrites ||
            source == nullptr ||
            sourceBytes == 0 ||
            sourceBytes > output.size() - outputBytes) {
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
    bool failWrites = false;
    bool forceTimeout = false;
    bool stopped = false;
    std::vector<std::uint8_t> output;
    std::size_t outputBytes = 0;

private:
    std::vector<std::uint8_t> input_;
    std::size_t offset_ = 0;
};

bool Establish(
    SecureOuterStream* outer,
    SecureEndpointRole role) noexcept {
    std::uint8_t instance[16]{};
    std::uint8_t build[32]{};
    instance[0] = 1;
    build[0] = 2;
    return outer != nullptr &&
        outer->Establish(
            role,
            instance,
            sizeof(instance),
            build,
            sizeof(build));
}

SecureGameGrantPolicy Policy(
    SecureGrantTestClock* clock) noexcept {
    return SecureGameGrantPolicy{
        BuildSecureGrantTestManifest(),
        clock,
        TestClock};
}

ClientRoute GameRoute() noexcept {
    ClientRoute route{};
    static_cast<void>(TryCopyClientRoute(
        "game-route.reborn.test",
        7000,
        &route));
    return route;
}

SecureGameGrant PresentableGrant(
    SecureGameGrantRegistry* registry) noexcept {
    SecureGameGrant output;
    if (registry == nullptr) {
        return output;
    }
    auto grant = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes());
    SecureGameGrantClaim claim{};
    if (registry->Commit(std::move(grant)) !=
            SecureGameGrantResult::Success ||
        registry->Claim(7, 1, GameRoute(), &claim) !=
            SecureGameGrantResult::Success ||
        registry->BeginPresentation(claim, &output) !=
            SecureGameGrantResult::Success) {
        output.Clear();
    }
    return output;
}

void CheckGrantBeforeLegacyBytes() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(Policy(&clock));
    const auto grant = BuildSecureGrantTestBytes();
    auto input = ServerPreface(SecureEndpointRole::Login);
    AppendFrame(
        &input,
        SecureEndpointRole::Login,
        SecureFrameDirection::ServerToClient,
        SecureFrameType::GameGrant,
        1,
        grant.bytes,
        grant.byteCount);
    const std::uint8_t redirect[] = {0xAA, 0xBB};
    AppendFrame(
        &input,
        SecureEndpointRole::Login,
        SecureFrameDirection::ServerToClient,
        SecureFrameType::LegacyBytes,
        2,
        redirect,
        sizeof(redirect));

    ScriptedStream stream(std::move(input));
    stream.maximumReadBytes = 3;
    {
        SecureOuterStream outer(&stream, &registry);
        Check(
            Establish(&outer, SecureEndpointRole::Login),
            "login control stream preface failed");
        std::uint8_t received[2]{};
        const auto read = outer.Read(received, sizeof(received));
        Check(
            read.status == ByteStreamIoStatus::Success &&
                read.bytesTransferred == sizeof(received) &&
                std::memcmp(
                    received,
                    redirect,
                    sizeof(redirect)) == 0 &&
                registry.Snapshot().state ==
                    SecureGameGrantState::Pending &&
                outer.Snapshot().nextInboundSequence == 3,
            "GameGrant was not committed before redirect bytes");
    }
    Check(
        registry.Snapshot().state ==
            SecureGameGrantState::Pending,
        "expected login close erased an exposed grant");
}

void CheckRejectedGrantBlocksLegacyBytes() {
    constexpr std::uint8_t marker = 0xCC;
    for (std::size_t scenario = 0; scenario < 3; ++scenario) {
        SecureGrantTestClock clock{};
        SecureGameGrantRegistry registry(Policy(&clock));
        auto grant = scenario == 1
            ? BuildSecureGrantTestBytes(
                  "game-route.reborn.test",
                  "evil-reborn.test")
            : BuildSecureGrantTestBytes();
        if (scenario == 0) {
            grant.bytes[0] = 2;
        }
        auto input = ServerPreface(SecureEndpointRole::Login);
        AppendFrame(
            &input,
            SecureEndpointRole::Login,
            SecureFrameDirection::ServerToClient,
            SecureFrameType::GameGrant,
            1,
            grant.bytes,
            grant.byteCount);
        AppendFrame(
            &input,
            SecureEndpointRole::Login,
            SecureFrameDirection::ServerToClient,
            SecureFrameType::LegacyBytes,
            2,
            &marker,
            sizeof(marker));

        ScriptedStream stream(std::move(input));
        SecureOuterStream outer(
            &stream,
            scenario == 2 ? nullptr : &registry);
        Check(
            Establish(&outer, SecureEndpointRole::Login),
            "rejected grant preface failed");
        std::uint8_t received = 0;
        const auto read = outer.Read(&received, 1);
        const auto expected =
            scenario == 0
                ? SecureOuterFailure::GrantDecode
                : SecureOuterFailure::GrantCommit;
        Check(
            read.status == ByteStreamIoStatus::Failed &&
                received == 0 &&
                outer.Snapshot().failure == expected &&
                stream.stopped &&
                registry.Snapshot().state ==
                    SecureGameGrantState::Empty,
            "rejected GameGrant exposed following legacy bytes");
    }
}

void CheckDuplicateGrantInvalidatesUnexposedTicket() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(Policy(&clock));
    const auto grant = BuildSecureGrantTestBytes();
    auto input = ServerPreface(SecureEndpointRole::Login);
    AppendFrame(
        &input,
        SecureEndpointRole::Login,
        SecureFrameDirection::ServerToClient,
        SecureFrameType::GameGrant,
        1,
        grant.bytes,
        grant.byteCount);
    AppendFrame(
        &input,
        SecureEndpointRole::Login,
        SecureFrameDirection::ServerToClient,
        SecureFrameType::GameGrant,
        2,
        grant.bytes,
        grant.byteCount);
    constexpr std::uint8_t redirect = 0xAA;
    AppendFrame(
        &input,
        SecureEndpointRole::Login,
        SecureFrameDirection::ServerToClient,
        SecureFrameType::LegacyBytes,
        3,
        &redirect,
        sizeof(redirect));

    ScriptedStream stream(std::move(input));
    SecureOuterStream outer(&stream, &registry);
    Check(
        Establish(&outer, SecureEndpointRole::Login),
        "duplicate grant preface failed");
    std::uint8_t received = 0;
    Check(
        outer.Read(&received, 1).status ==
                ByteStreamIoStatus::Failed &&
            received == 0 &&
            outer.Snapshot().failure ==
                SecureOuterFailure::UnsupportedControl &&
            registry.Snapshot().state ==
                SecureGameGrantState::Empty,
        "duplicate pre-redirect grant retained an exposed ticket");
}

std::vector<std::uint8_t> GameInput(
    std::uint16_t bindStatus,
    std::uint64_t sequence = 1,
    bool malformedReserved = false,
    bool includeLegacy = false) {
    auto input = ServerPreface(SecureEndpointRole::Game);
    std::uint8_t result[SecureBindResultBytes] = {
        static_cast<std::uint8_t>(bindStatus >> 8U),
        static_cast<std::uint8_t>(bindStatus),
        0,
        static_cast<std::uint8_t>(malformedReserved ? 1 : 0),
    };
    AppendFrame(
        &input,
        SecureEndpointRole::Game,
        SecureFrameDirection::ServerToClient,
        SecureFrameType::BindResult,
        sequence,
        result,
        sizeof(result));
    if (includeLegacy) {
        constexpr std::uint8_t legacy = 0xDE;
        AppendFrame(
            &input,
            SecureEndpointRole::Game,
            SecureFrameDirection::ServerToClient,
            SecureFrameType::LegacyBytes,
            sequence + 1,
            &legacy,
            sizeof(legacy));
    }
    return input;
}

void CheckAcceptedBindGatesGameStream() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(Policy(&clock));
    auto grant = PresentableGrant(&registry);
    ScriptedStream stream(GameInput(0, 1, false, true));
    stream.maximumReadBytes = 2;
    SecureOuterStream outer(&stream);
    Check(
        Establish(&outer, SecureEndpointRole::Game),
        "game bind preface failed");
    std::uint8_t preBindProbe = 0;
    Check(
        outer.Read(&preBindProbe, 1).status ==
                ByteStreamIoStatus::Failed &&
            outer.Write(&preBindProbe, 1).status ==
                ByteStreamIoStatus::Failed &&
            !outer.Snapshot().stopped,
        "game legacy stream opened before bind acceptance");
    Check(
        outer.PresentGameBind(&grant) &&
            !grant.IsValid() &&
            outer.Snapshot().gameBound &&
            outer.Snapshot().nextInboundSequence == 2 &&
            outer.Snapshot().nextOutboundSequence == 2,
        "accepted BindResult did not gate game stream");

    constexpr std::size_t bindHeaderOffset = 72;
    SecureFrameHeader header{};
    Check(
        stream.outputBytes ==
                bindHeaderOffset +
                SecureFrameHeaderBytes +
                SecureGameBindBytes &&
            TryDecodeSecureFrameHeader(
                stream.output.data() + bindHeaderOffset,
                SecureFrameHeaderBytes,
                SecureEndpointRole::Game,
                SecureFrameDirection::ClientToServer,
                1,
                &header) &&
            header.type == SecureFrameType::GameBind &&
            stream.output[
                bindHeaderOffset + SecureFrameHeaderBytes] == 1,
        "game bind did not emit canonical sequence-one frame");

    std::uint8_t legacy = 0;
    Check(
        outer.Read(&legacy, 1).status ==
                ByteStreamIoStatus::Success &&
            legacy == 0xDE,
        "accepted game bind did not release following legacy bytes");
}

void CheckBindFailuresBurnTicket() {
    struct Scenario final {
        std::uint16_t status;
        std::uint64_t sequence;
        bool malformed;
        SecureOuterFailure failure;
    };
    constexpr Scenario scenarios[] = {
        {1, 1, false, SecureOuterFailure::BindRejected},
        {2, 1, false, SecureOuterFailure::BindRejected},
        {3, 1, false, SecureOuterFailure::BindRejected},
        {0, 2, false, SecureOuterFailure::BindResult},
        {0, 1, true, SecureOuterFailure::BindResult},
    };

    for (const auto& scenario : scenarios) {
        SecureGrantTestClock clock{};
        SecureGameGrantRegistry registry(Policy(&clock));
        auto grant = PresentableGrant(&registry);
        ScriptedStream stream(GameInput(
            scenario.status,
            scenario.sequence,
            scenario.malformed));
        SecureOuterStream outer(&stream);
        Check(
            Establish(&outer, SecureEndpointRole::Game),
            "bind failure preface failed");
        Check(
            !outer.PresentGameBind(&grant) &&
                !grant.IsValid() &&
                outer.Snapshot().failure == scenario.failure &&
                outer.Snapshot().stopped &&
                !outer.Snapshot().gameBound,
            "failed game bind retained or reopened ticket");
    }

    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(Policy(&clock));
    auto grant = PresentableGrant(&registry);
    ScriptedStream stream(
        ServerPreface(SecureEndpointRole::Game));
    SecureOuterStream outer(&stream);
    Check(
        Establish(&outer, SecureEndpointRole::Game),
        "bind write failure preface failed");
    stream.failWrites = true;
    Check(
        !outer.PresentGameBind(&grant) &&
            !grant.IsValid() &&
            outer.Snapshot().failure ==
                SecureOuterFailure::BindWrite,
        "failed bind write did not burn ticket");

    SecureGameGrantRegistry timeoutRegistry(Policy(&clock));
    auto timeoutGrant = PresentableGrant(&timeoutRegistry);
    ScriptedStream timeoutStream(
        ServerPreface(SecureEndpointRole::Game));
    SecureOuterStream timeoutOuter(&timeoutStream);
    Check(
        Establish(&timeoutOuter, SecureEndpointRole::Game),
        "bind timeout preface failed");
    timeoutStream.forceTimeout = true;
    Check(
        !timeoutOuter.PresentGameBind(&timeoutGrant) &&
            !timeoutGrant.IsValid() &&
            timeoutOuter.Snapshot().failure ==
                SecureOuterFailure::OperationDeadline,
        "bind-result timeout retained ticket or wrong failure");
}

} // namespace

int RunSecureOuterControlTests() {
    Failures = 0;
    CheckGrantBeforeLegacyBytes();
    CheckRejectedGrantBlocksLegacyBytes();
    CheckDuplicateGrantInvalidatesUnexposedTicket();
    CheckAcceptedBindGatesGameStream();
    CheckBindFailuresBurnTicket();
    return Failures;
}
