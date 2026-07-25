#include "SecureGameControlTests.h"

#include "SecureGameControlTestSupport.h"

#include "../src/SecureGameControl.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <new>
#include <utility>

namespace {

using godswar::network::SecureBindResultBytes;
using godswar::network::SecureBindStatus;
using godswar::network::SecureGameBindBytes;
using godswar::network::SecureGameGrant;
using godswar::network::SecureGameGrantIdBytes;
using godswar::network::SecureGameGrantMaximumBytes;
using godswar::network::SecureGameTicketBytes;
using godswar::network::TryDecodeSecureBindResult;
using godswar::network::TryDecodeSecureGameGrant;
using godswar::network::TryEncodeSecureGameBind;
using godswar::network::tests::BuildSecureGrantTestBytes;
using godswar::network::tests::SecureGrantTestBytes;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

bool ContainsSequence(
    const std::uint8_t* bytes,
    std::size_t byteCount,
    const std::uint8_t* sequence,
    std::size_t sequenceBytes) noexcept {
    if (bytes == nullptr ||
        sequence == nullptr ||
        sequenceBytes == 0 ||
        sequenceBytes > byteCount) {
        return false;
    }
    for (std::size_t offset = 0;
         offset <= byteCount - sequenceBytes;
         ++offset) {
        if (std::memcmp(
                bytes + offset,
                sequence,
                sequenceBytes) == 0) {
            return true;
        }
    }
    return false;
}

void CheckGoldenVectors() {
    constexpr std::uint8_t Grant[] = {
        0x01, 0x01, 0x01, 0x01, 0x17, 0x6F, 0x1D, 0x13,
        0x00, 0x00, 0x00, 0x2A, 0x01, 0x02, 0x03, 0x04,
        0x05, 0x06, 0x07, 0x08,
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
        0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
        0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
        0x61, 0x62, 0x63,
    };
    SecureGameGrant grant;
    Check(
        TryDecodeSecureGameGrant(
            Grant,
            sizeof(Grant),
            &grant),
        "server golden GameGrant did not decode");
    Check(
        grant.IsValid() &&
            std::strcmp(grant.RouteHost(), "a") == 0 &&
            std::strcmp(grant.TlsHost(), "b") == 0 &&
            std::strcmp(grant.Audience(), "c") == 0 &&
            grant.RoutePort() == 5999 &&
            grant.TlsPort() == 7443 &&
            grant.TargetServerId() == 42 &&
            grant.ExpiryUnixMilliseconds() ==
                0x0102030405060708ULL,
        "server golden GameGrant fields changed");

    std::uint8_t grantId[SecureGameGrantIdBytes]{};
    std::uint8_t ticket[SecureGameTicketBytes]{};
    Check(
        grant.TryCopySecrets(
            grantId,
            sizeof(grantId),
            ticket,
            sizeof(ticket)) &&
            grantId[0] == 1 &&
            grantId[15] == 0x10 &&
            ticket[0] == 0x20 &&
            ticket[31] == 0x3F,
        "server golden GameGrant secrets changed");
    SecureZeroMemory(grantId, sizeof(grantId));
    SecureZeroMemory(ticket, sizeof(ticket));

    constexpr std::uint8_t ExpectedBind[] = {
        0x01, 0x00, 0x00, 0x00,
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
        0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
        0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
    };
    std::uint8_t bind[SecureGameBindBytes]{};
    Check(
        TryEncodeSecureGameBind(grant, bind, sizeof(bind)) &&
            std::memcmp(
                bind,
                ExpectedBind,
                sizeof(ExpectedBind)) == 0,
        "server golden GameBind bytes changed");
    SecureZeroMemory(bind, sizeof(bind));

    for (std::uint16_t value = 0; value <= 3; ++value) {
        const std::uint8_t result[] = {
            0,
            static_cast<std::uint8_t>(value),
            0,
            0,
        };
        SecureBindStatus status = SecureBindStatus::PolicyRejected;
        Check(
            TryDecodeSecureBindResult(
                result,
                sizeof(result),
                &status) &&
                static_cast<std::uint16_t>(status) == value,
            "canonical BindResult status did not decode");
    }
}

void CheckMalformedAndBounds() {
    const auto valid = BuildSecureGrantTestBytes();
    SecureGameGrant grant;
    for (std::size_t length = 0;
         length < valid.byteCount;
         ++length) {
        Check(
            !TryDecodeSecureGameGrant(
                valid.bytes,
                length,
                &grant) &&
                !grant.IsValid(),
            "truncated GameGrant was accepted");
    }
    Check(
        TryDecodeSecureGameGrant(
            valid.bytes,
            valid.byteCount,
            &grant),
        "valid bounded GameGrant was rejected");

    char maximumRoute[24]{};
    char maximumTls[254]{};
    char maximumAudience[65]{};
    std::memset(maximumRoute, 'r', sizeof(maximumRoute) - 1);
    std::memset(maximumTls, 'g', sizeof(maximumTls) - 1);
    maximumTls[63] = '.';
    maximumTls[127] = '.';
    maximumTls[191] = '.';
    std::memset(
        maximumAudience,
        'A',
        sizeof(maximumAudience) - 1);
    const auto maximum = BuildSecureGrantTestBytes(
        maximumRoute,
        maximumTls,
        maximumAudience);
    Check(
        maximum.byteCount == SecureGameGrantMaximumBytes &&
            TryDecodeSecureGameGrant(
                maximum.bytes,
                maximum.byteCount,
                &grant),
        "maximum 408-byte GameGrant was rejected");
    std::uint8_t oversized[SecureGameGrantMaximumBytes + 1]{};
    std::memcpy(
        oversized,
        maximum.bytes,
        maximum.byteCount);
    Check(
        !TryDecodeSecureGameGrant(
            oversized,
            sizeof(oversized),
            &grant),
        "oversized GameGrant was accepted");

    constexpr std::size_t MutationCount = 11;
    for (std::size_t mutation = 0;
         mutation < MutationCount;
         ++mutation) {
        auto malformed = valid;
        switch (mutation) {
            case 0:
                malformed.bytes[0] = 2;
                break;
            case 1:
                malformed.bytes[1] = 0;
                break;
            case 2:
                malformed.bytes[4] = 0;
                malformed.bytes[5] = 0;
                break;
            case 3:
                malformed.bytes[8] = 0;
                malformed.bytes[9] = 0;
                malformed.bytes[10] = 0;
                malformed.bytes[11] = 0;
                break;
            case 4:
                std::memset(
                    malformed.bytes + 20,
                    0,
                    SecureGameGrantIdBytes);
                break;
            case 5:
                std::memset(
                    malformed.bytes + 36,
                    0,
                    SecureGameTicketBytes);
                break;
            case 6:
                malformed.bytes[68] = 'G';
                break;
            case 7:
                malformed.bytes[68] = '-';
                break;
            case 8: {
                const std::size_t audienceOffset =
                    68 + malformed.bytes[1] + malformed.bytes[2];
                malformed.bytes[audienceOffset] = '/';
                break;
            }
            case 9:
                ++malformed.bytes[3];
                break;
            case 10:
                malformed.bytes[6] = 0;
                malformed.bytes[7] = 0;
                break;
        }
        Check(
            !TryDecodeSecureGameGrant(
                malformed.bytes,
                malformed.byteCount,
                &grant) &&
                !grant.IsValid(),
            "malformed GameGrant was accepted or retained old secrets");
    }

    std::uint8_t random[SecureGameGrantMaximumBytes + 1]{};
    std::uint32_t state = 0xA5C31F27U;
    for (std::size_t iteration = 0;
         iteration < 4096;
         ++iteration) {
        state = state * 1664525U + 1013904223U;
        const std::size_t length =
            state % (sizeof(random) + 1);
        for (std::size_t index = 0; index < length; ++index) {
            state = state * 1664525U + 1013904223U;
            random[index] = static_cast<std::uint8_t>(state >> 24U);
        }
        const bool decoded = TryDecodeSecureGameGrant(
            random,
            length,
            &grant);
        Check(
            decoded == grant.IsValid(),
            "random GameGrant decoder returned inconsistent ownership");
    }

    std::uint8_t result[SecureBindResultBytes] = {0, 0, 0, 0};
    SecureBindStatus status = SecureBindStatus::Rejected;
    Check(
        !TryDecodeSecureBindResult(
            result,
            SecureBindResultBytes - 1,
            &status),
        "truncated BindResult was accepted");
    result[1] = 4;
    Check(
        !TryDecodeSecureBindResult(
            result,
            sizeof(result),
            &status),
        "unknown BindResult status was accepted");
    result[1] = 0;
    result[3] = 1;
    Check(
        !TryDecodeSecureBindResult(
            result,
            sizeof(result),
            &status),
        "nonzero BindResult reserved byte was accepted");
}

void CheckMoveAndZeroing() {
    const auto bytes = BuildSecureGrantTestBytes();
    SecureGameGrant source;
    Check(
        TryDecodeSecureGameGrant(
            bytes.bytes,
            bytes.byteCount,
            &source),
        "move fixture GameGrant did not decode");
    SecureGameGrant moved(std::move(source));
    Check(
        moved.IsValid() && !source.IsValid(),
        "GameGrant move did not transfer exclusive ownership");

    std::uint8_t rejectedGrantId[SecureGameGrantIdBytes];
    std::uint8_t rejectedTicket[SecureGameTicketBytes];
    std::memset(
        rejectedGrantId,
        0xCD,
        sizeof(rejectedGrantId));
    std::memset(
        rejectedTicket,
        0xCD,
        sizeof(rejectedTicket));
    Check(
        !source.TryCopySecrets(
            rejectedGrantId,
            sizeof(rejectedGrantId),
            rejectedTicket,
            sizeof(rejectedTicket)),
        "moved-from GameGrant exposed secrets");
    bool rejectedCopiesCleared = true;
    for (const auto value : rejectedGrantId) {
        rejectedCopiesCleared =
            rejectedCopiesCleared && value == 0;
    }
    for (const auto value : rejectedTicket) {
        rejectedCopiesCleared =
            rejectedCopiesCleared && value == 0;
    }
    Check(
        rejectedCopiesCleared,
        "failed secret copy retained caller buffer bytes");

    std::uint8_t bind[SecureGameBindBytes];
    std::memset(bind, 0xCD, sizeof(bind));
    source.Clear();
    Check(
        !TryEncodeSecureGameBind(
            source,
            bind,
            sizeof(bind)),
        "disposed GameGrant encoded a bind");
    bool bindCleared = true;
    for (const auto value : bind) {
        bindCleared = bindCleared && value == 0;
    }
    Check(
        bindCleared,
        "failed GameBind encoding retained destination bytes");

    alignas(SecureGameGrant)
        std::uint8_t storage[sizeof(SecureGameGrant)]{};
    auto* owned = new (storage) SecureGameGrant();
    Check(
        TryDecodeSecureGameGrant(
            bytes.bytes,
            bytes.byteCount,
            owned),
        "destructor zero fixture did not decode");
    const std::uint8_t* expectedGrantId = bytes.bytes + 20;
    const std::uint8_t* expectedTicket = bytes.bytes + 36;
    Check(
        ContainsSequence(
            storage,
            sizeof(storage),
            expectedGrantId,
            SecureGameGrantIdBytes) &&
            ContainsSequence(
                storage,
                sizeof(storage),
                expectedTicket,
                SecureGameTicketBytes),
        "secret patterns were not owned by the RAII grant");
    owned->~SecureGameGrant();
    Check(
        !ContainsSequence(
            storage,
            sizeof(storage),
            expectedGrantId,
            SecureGameGrantIdBytes) &&
            !ContainsSequence(
                storage,
                sizeof(storage),
                expectedTicket,
                SecureGameTicketBytes),
        "GameGrant destruction did not wipe secret patterns");
    SecureZeroMemory(storage, sizeof(storage));
}

} // namespace

int RunSecureGameControlTests() {
    Failures = 0;
    CheckGoldenVectors();
    CheckMalformedAndBounds();
    CheckMoveAndZeroing();
    return Failures;
}
