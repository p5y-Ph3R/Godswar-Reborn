#include "EndpointManifestTests.h"

#include "EndpointManifestLoaderTests.h"
#include "EndpointManifestTestSupport.h"

#include "../src/EndpointManifest.h"

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::EndpointManifest;
using godswar::network::EndpointManifestAllowsAudience;
using godswar::network::EndpointManifestAllowsGameHost;
using godswar::network::EndpointManifestAllowsServerId;
using godswar::network::EndpointManifestEnvironment;
using godswar::network::EndpointManifestError;
using godswar::network::EndpointManifestMaximumValiditySeconds;
using godswar::network::ParseAndVerifyEndpointManifest;
using godswar::network::tests::BuildTestManifest;
using godswar::network::tests::EndpointManifestTestBytes;
using godswar::network::tests::EndpointManifestTestSigner;
using godswar::network::tests::EndpointManifestValidationFixture;
using godswar::network::tests::MakeTestValidation;
using godswar::network::tests::SignTestManifest;
using godswar::network::tests::TestCurrentKeyId;
using godswar::network::tests::TestManifestNow;
using godswar::network::tests::TestNextKeyId;
using godswar::network::tests::WriteTestUint16;
using godswar::network::tests::WriteTestUint32;
using godswar::network::tests::WriteTestUint64;

int Failures = 0;

void Check(bool condition, const char* message) noexcept {
    if (condition) {
        return;
    }
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++Failures;
}

EndpointManifestError Parse(
    const EndpointManifestTestBytes& bytes,
    EndpointManifestValidationFixture* fixture,
    EndpointManifest* manifest,
    EndpointManifestEnvironment environment =
        EndpointManifestEnvironment::Production) noexcept {
    return ParseAndVerifyEndpointManifest(
        bytes.bytes,
        bytes.byteCount,
        MakeTestValidation(fixture, environment),
        manifest);
}

void ExpectError(
    const EndpointManifestTestBytes& bytes,
    EndpointManifestValidationFixture* fixture,
    EndpointManifestError expected,
    const char* message,
    EndpointManifestEnvironment environment =
        EndpointManifestEnvironment::Production) noexcept {
    EndpointManifest output{};
    output.sequence = 999;
    Check(
        Parse(bytes, fixture, &output, environment) == expected,
        message);
    Check(
        output.sequence == 999,
        "failed validation changed caller output");
}

void TestValidManifest(
    const EndpointManifestTestSigner& signer,
    EndpointManifestValidationFixture* fixture) noexcept {
    EndpointManifestTestBytes bytes{};
    Check(
        BuildTestManifest(&bytes, signer),
        "valid manifest signing failed");
    Check(
        bytes.bytes[4] == 0 &&
            bytes.bytes[5] == 0 &&
            bytes.bytes[6] == 0 &&
            bytes.bytes[7] ==
                static_cast<std::uint8_t>(bytes.byteCount) &&
            bytes.bytes[24] == 0 &&
            bytes.bytes[31] == 12 &&
            bytes.bytes[52] == 0x17 &&
            bytes.bytes[53] == 0x6F,
        "GWEM golden vector is not big-endian");

    EndpointManifest manifest{};
    Check(
        Parse(bytes, fixture, &manifest) ==
            EndpointManifestError::Success,
        "valid signed manifest was rejected");
    Check(
        manifest.sequence == 12 &&
            manifest.logicalLoginPort == 5999 &&
            manifest.tlsLoginPort == 6599 &&
            std::strcmp(
                manifest.logicalLoginHost.bytes,
                "127.0.0.1") == 0 &&
            std::strcmp(
                manifest.tlsLoginHost.bytes,
                "login.reborn.test") == 0,
        "validated manifest fields changed");
    Check(
        EndpointManifestAllowsGameHost(
            manifest,
            "reborn.test",
            11) &&
        EndpointManifestAllowsGameHost(
            manifest,
            "game.reborn.test",
            16) &&
        !EndpointManifestAllowsGameHost(
            manifest,
            "evil-reborn.test",
            16) &&
        !EndpointManifestAllowsGameHost(
            manifest,
            "GAME.REBORN.TEST",
            16),
        "game suffix boundary/canonical matching changed");
    Check(
        EndpointManifestAllowsAudience(
            manifest,
            "reborn-game",
            11) &&
        !EndpointManifestAllowsAudience(manifest, "other", 5) &&
        EndpointManifestAllowsServerId(manifest, 100) &&
        !EndpointManifestAllowsServerId(manifest, 0) &&
        !EndpointManifestAllowsServerId(manifest, 101),
        "audience/server grant matching changed");

    Check(
        BuildTestManifest(
            &bytes,
            signer,
            EndpointManifestEnvironment::Production,
            TestNextKeyId) &&
        Parse(bytes, fixture, &manifest) ==
            EndpointManifestError::Success,
        "trusted next verification key was rejected");
}

void TestHeaderAndSignatureFailures(
    const EndpointManifestTestSigner& signer,
    EndpointManifestValidationFixture* fixture) noexcept {
    EndpointManifestTestBytes bytes{};
    BuildTestManifest(&bytes, signer);
    bytes.bytes[0] = 'X';
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidMagic,
        "invalid magic was accepted");

    BuildTestManifest(&bytes, signer);
    WriteTestUint16(bytes.bytes + 8, 71);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidHeader,
        "invalid header size was accepted");
    BuildTestManifest(&bytes, signer);
    WriteTestUint16(bytes.bytes + 10, 2);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::UnsupportedFormat,
        "unknown format version was accepted");
    BuildTestManifest(&bytes, signer);
    WriteTestUint16(bytes.bytes + 16, 2);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::UnsupportedSignatureAlgorithm,
        "unknown signature algorithm was accepted");
    BuildTestManifest(&bytes, signer);
    WriteTestUint16(bytes.bytes + 18, 99);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::UnknownPublicKey,
        "unknown key ID was accepted");
    BuildTestManifest(&bytes, signer);
    bytes.bytes[20] = 1;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidHeader,
        "nonzero reserved header byte was accepted");

    BuildTestManifest(&bytes, signer);
    bytes.bytes[bytes.byteCount - 1] ^= 0x80;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::SignatureVerificationFailed,
        "tampered P1363 signature was accepted");
    BuildTestManifest(&bytes, signer);
    WriteTestUint32(
        bytes.bytes + 64,
        static_cast<std::uint32_t>(bytes.signedByteCount + 1));
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidLength,
        "inconsistent signed-byte length was accepted");
}

void TestEnvironmentAndSequence(
    const EndpointManifestTestSigner& signer,
    EndpointManifestValidationFixture* fixture) noexcept {
    EndpointManifestTestBytes bytes{};
    EndpointManifest manifest{};
    BuildTestManifest(
        &bytes,
        signer,
        EndpointManifestEnvironment::Development);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::EnvironmentMismatch,
        "wrong environment was accepted");
    bytes.bytes[15] = 1;
    Check(SignTestManifest(&bytes, signer), "dev re-sign failed");
    Check(
        Parse(
            bytes,
            fixture,
            &manifest,
            EndpointManifestEnvironment::Development) ==
            EndpointManifestError::Success,
        "development passthrough flag was rejected");

    BuildTestManifest(
        &bytes,
        signer,
        EndpointManifestEnvironment::Staging);
    bytes.bytes[15] = 1;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidFlags,
        "staging accepted the dev-only passthrough flag",
        EndpointManifestEnvironment::Staging);
    BuildTestManifest(&bytes, signer);
    bytes.bytes[15] = 1;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidFlags,
        "production accepted the dev-only passthrough flag");
    BuildTestManifest(&bytes, signer);
    bytes.bytes[15] = 2;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidFlags,
        "unknown manifest flag was accepted");

    BuildTestManifest(&bytes, signer);
    WriteTestUint64(bytes.bytes + 24, 10);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidSequence,
        "installed rollback floor was ignored");
    BuildTestManifest(&bytes, signer);
    WriteTestUint64(bytes.bytes + 24, 0);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidSequence,
        "zero manifest sequence was accepted");
    fixture->compiledMinimum = 13;
    BuildTestManifest(&bytes, signer);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidSequence,
        "compiled rollback floor was ignored");
    fixture->compiledMinimum = 10;
    fixture->sequenceLookupSucceeds = false;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::SequenceLookupFailed,
        "missing sequence state did not fail closed");
    fixture->sequenceLookupSucceeds = true;
    fixture->keyLookupSucceeds = false;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::UnknownPublicKey,
        "key lookup failure did not fail closed");
    fixture->keyLookupSucceeds = true;
}

void TestValidityProtocolAndPorts(
    const EndpointManifestTestSigner& signer,
    EndpointManifestValidationFixture* fixture) noexcept {
    EndpointManifestTestBytes bytes{};
    EndpointManifest manifest{};
    BuildTestManifest(&bytes, signer);
    fixture->clockSucceeds = false;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::ClockLookupFailed,
        "clock failure did not fail closed");
    fixture->clockSucceeds = true;
    fixture->now = TestManifestNow - 60;
    Check(
        Parse(bytes, fixture, &manifest) ==
            EndpointManifestError::Success,
        "inclusive not-before boundary was rejected");
    fixture->now = TestManifestNow + 60;
    Check(
        Parse(bytes, fixture, &manifest) ==
            EndpointManifestError::Success,
        "inclusive not-after boundary was rejected");
    fixture->now = TestManifestNow;
    BuildTestManifest(&bytes, signer);
    WriteTestUint64(bytes.bytes + 32, TestManifestNow);
    WriteTestUint64(
        bytes.bytes + 40,
        TestManifestNow +
            EndpointManifestMaximumValiditySeconds);
    Check(
        SignTestManifest(&bytes, signer) &&
            Parse(bytes, fixture, &manifest) ==
                EndpointManifestError::Success,
        "exact 31-day validity window was rejected");

    BuildTestManifest(&bytes, signer);
    WriteTestUint64(bytes.bytes + 32, TestManifestNow + 1);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidValidity,
        "future manifest was accepted");
    BuildTestManifest(&bytes, signer);
    WriteTestUint64(bytes.bytes + 40, TestManifestNow - 1);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidValidity,
        "expired manifest was accepted");
    BuildTestManifest(&bytes, signer);
    WriteTestUint64(bytes.bytes + 32, TestManifestNow - 1);
    WriteTestUint64(
        bytes.bytes + 40,
        TestManifestNow - 1 +
            EndpointManifestMaximumValiditySeconds + 1);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidValidity,
        "overlong validity window was accepted");
    BuildTestManifest(&bytes, signer);
    WriteTestUint64(bytes.bytes + 32, TestManifestNow);
    WriteTestUint64(bytes.bytes + 40, TestManifestNow);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidValidity,
        "empty validity interval was accepted");

    BuildTestManifest(&bytes, signer);
    WriteTestUint16(bytes.bytes + 50, 1);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::UnsupportedProtocol,
        "unsupported minimum protocol was accepted");
    BuildTestManifest(&bytes, signer);
    WriteTestUint16(bytes.bytes + 54, 0);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidPort,
        "zero TLS port was accepted");
}

void TestBoundsAndTruncation(
    const EndpointManifestTestSigner& signer,
    EndpointManifestValidationFixture* fixture) noexcept {
    EndpointManifestTestBytes bytes{};
    BuildTestManifest(&bytes, signer);
    EndpointManifest output{};
    Check(
        ParseAndVerifyEndpointManifest(
            nullptr,
            bytes.byteCount,
            MakeTestValidation(fixture),
            &output) == EndpointManifestError::InvalidArgument,
        "null manifest buffer was accepted");

    for (std::size_t length = 0;
         length < bytes.byteCount;
         ++length) {
        output.sequence = 444;
        const auto result = ParseAndVerifyEndpointManifest(
            bytes.bytes,
            length,
            MakeTestValidation(fixture),
            &output);
        Check(
            result != EndpointManifestError::Success &&
                output.sequence == 444,
            "truncated manifest was accepted or published");
    }

    BuildTestManifest(&bytes, signer);
    WriteTestUint16(bytes.bytes + 56, 254);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidCount,
        "overlong logical host length was accepted");
    BuildTestManifest(&bytes, signer);
    bytes.bytes[60] = 9;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidCount,
        "too many DNS suffixes were accepted");
    BuildTestManifest(&bytes, signer);
    bytes.bytes[61] = 9;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidCount,
        "too many audiences were accepted");
    BuildTestManifest(&bytes, signer);
    bytes.bytes[62] = 17;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidCount,
        "too many server IDs were accepted");
}

void TestBodyFailures(
    const EndpointManifestTestSigner& signer,
    EndpointManifestValidationFixture* fixture) noexcept {
    EndpointManifestTestBytes bytes{};
    BuildTestManifest(&bytes, signer);
    constexpr char invalidIpv4[] = "01.0.0.01";
    std::memcpy(
        bytes.bytes + bytes.logicalHostOffset,
        invalidIpv4,
        sizeof(invalidIpv4) - 1);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidHost,
        "noncanonical dotted IPv4 was accepted");
    BuildTestManifest(&bytes, signer);
    bytes.bytes[bytes.tlsHostOffset] = 'L';
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidHost,
        "uppercase TLS DNS host was accepted");
    BuildTestManifest(&bytes, signer);
    constexpr char numericTlsHost[] = "111.111.111.11111";
    std::memcpy(
        bytes.bytes + bytes.tlsHostOffset,
        numericTlsHost,
        sizeof(numericTlsHost) - 1);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidHost,
        "numeric TLS host was accepted");

    BuildTestManifest(&bytes, signer);
    bytes.bytes[bytes.suffixOffsets[0]] = '-';
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidSuffix,
        "invalid DNS suffix was accepted");
    BuildTestManifest(&bytes, signer);
    std::memcpy(
        bytes.bytes + bytes.suffixOffsets[1],
        bytes.bytes + bytes.suffixOffsets[0],
        11);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::DuplicateValue,
        "duplicate suffix was accepted");

    BuildTestManifest(&bytes, signer);
    bytes.bytes[bytes.audienceOffsets[0] + 1] = '/';
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidAudience,
        "invalid audience character was accepted");
    BuildTestManifest(&bytes, signer);
    std::memcpy(
        bytes.bytes + bytes.audienceOffsets[1],
        bytes.bytes + bytes.audienceOffsets[0],
        11);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::DuplicateValue,
        "duplicate audience was accepted");

    BuildTestManifest(&bytes, signer);
    WriteTestUint32(bytes.bytes + bytes.serverIdOffsets[0], 0);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidServerId,
        "zero server ID was accepted");
    BuildTestManifest(&bytes, signer);
    WriteTestUint32(bytes.bytes + bytes.serverIdOffsets[1], 100);
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::DuplicateValue,
        "duplicate server ID was accepted");

    BuildTestManifest(&bytes, signer);
    ++bytes.signedByteCount;
    ++bytes.byteCount;
    WriteTestUint32(
        bytes.bytes + 4,
        static_cast<std::uint32_t>(bytes.byteCount));
    WriteTestUint32(
        bytes.bytes + 64,
        static_cast<std::uint32_t>(bytes.signedByteCount));
    Check(
        SignTestManifest(&bytes, signer),
        "trailing-byte re-sign failed");
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::TrailingBodyBytes,
        "trailing body byte was accepted");
    BuildTestManifest(&bytes, signer);
    bytes.bytes[60] = 0;
    ExpectError(
        bytes,
        fixture,
        EndpointManifestError::InvalidCount,
        "zero suffix count was accepted");
}

} // namespace

int RunEndpointManifestTests() {
    Failures = 0;
    EndpointManifestTestSigner signer;
    Check(
        signer.IsValid(),
        "test P-256 signer initialization failed");
    if (!signer.IsValid()) {
        return Failures;
    }

    EndpointManifestValidationFixture fixture{};
    fixture.publicKey = signer.PublicKey();
    TestValidManifest(signer, &fixture);
    TestHeaderAndSignatureFailures(signer, &fixture);
    TestEnvironmentAndSequence(signer, &fixture);
    TestValidityProtocolAndPorts(signer, &fixture);
    TestBoundsAndTruncation(signer, &fixture);
    TestBodyFailures(signer, &fixture);
    Failures += RunEndpointManifestLoaderTests(signer, &fixture);

    if (Failures == 0) {
        std::printf("Endpoint manifest checks passed.\n");
    }
    return Failures;
}
