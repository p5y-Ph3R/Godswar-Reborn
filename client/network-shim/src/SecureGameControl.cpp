#include "SecureGameControl.h"

#include <Windows.h>

#include <algorithm>
#include <cstring>
#include <utility>

namespace godswar::network {
namespace {

std::uint16_t ReadUInt16(const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        (static_cast<std::uint16_t>(source[0]) << 8U) |
        source[1]);
}

std::uint32_t ReadUInt32(const std::uint8_t* source) noexcept {
    return
        (static_cast<std::uint32_t>(source[0]) << 24U) |
        (static_cast<std::uint32_t>(source[1]) << 16U) |
        (static_cast<std::uint32_t>(source[2]) << 8U) |
        source[3];
}

std::uint64_t ReadUInt64(const std::uint8_t* source) noexcept {
    std::uint64_t result = 0;
    for (std::size_t index = 0; index < 8; ++index) {
        result = (result << 8U) | source[index];
    }
    return result;
}

bool IsAllZero(
    const std::uint8_t* source,
    std::size_t sourceBytes) noexcept {
    if (source == nullptr || sourceBytes == 0) {
        return true;
    }
    std::uint8_t combined = 0;
    for (std::size_t index = 0; index < sourceBytes; ++index) {
        combined |= source[index];
    }
    return combined == 0;
}

bool IsCanonicalDnsName(
    const std::uint8_t* source,
    std::size_t sourceBytes,
    std::size_t maximumBytes) noexcept {
    if (source == nullptr ||
        sourceBytes == 0 ||
        sourceBytes > maximumBytes) {
        return false;
    }

    std::size_t labelBytes = 0;
    for (std::size_t index = 0; index < sourceBytes; ++index) {
        const std::uint8_t value = source[index];
        if (value == '.') {
            if (labelBytes == 0 || source[index - 1] == '-') {
                return false;
            }
            labelBytes = 0;
            continue;
        }

        const bool lower = value >= 'a' && value <= 'z';
        const bool digit = value >= '0' && value <= '9';
        if ((!lower && !digit && value != '-') ||
            (labelBytes == 0 && value == '-')) {
            return false;
        }
        ++labelBytes;
        if (labelBytes > 63) {
            return false;
        }
    }

    return labelBytes > 0 && source[sourceBytes - 1] != '-';
}

bool IsAudience(
    const std::uint8_t* source,
    std::size_t sourceBytes) noexcept {
    if (source == nullptr ||
        sourceBytes == 0 ||
        sourceBytes > SecureGameAudienceMaximumBytes) {
        return false;
    }
    for (std::size_t index = 0; index < sourceBytes; ++index) {
        const std::uint8_t value = source[index];
        const bool letter =
            (value >= 'a' && value <= 'z') ||
            (value >= 'A' && value <= 'Z');
        const bool digit = value >= '0' && value <= '9';
        if (!letter &&
            !digit &&
            value != '.' &&
            value != '_' &&
            value != '-') {
            return false;
        }
    }
    return true;
}

bool IsBindStatus(SecureBindStatus status) noexcept {
    return status >= SecureBindStatus::Accepted &&
        status <= SecureBindStatus::PolicyRejected;
}

void WipeDestination(
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (destination == nullptr || destinationBytes == 0) {
        return;
    }
    SecureZeroMemory(
        destination,
        (std::min)(destinationBytes, SecureGameBindBytes));
}

} // namespace

SecureGameGrant::~SecureGameGrant() noexcept {
    Clear();
}

SecureGameGrant::SecureGameGrant(
    SecureGameGrant&& other) noexcept {
    MoveFrom(&other);
}

SecureGameGrant& SecureGameGrant::operator=(
    SecureGameGrant&& other) noexcept {
    if (this != &other) {
        Clear();
        MoveFrom(&other);
    }
    return *this;
}

bool SecureGameGrant::IsValid() const noexcept {
    return valid_;
}

const char* SecureGameGrant::RouteHost() const noexcept {
    return routeHost_;
}

std::size_t SecureGameGrant::RouteHostLength() const noexcept {
    return routeHostLength_;
}

const char* SecureGameGrant::TlsHost() const noexcept {
    return tlsHost_;
}

std::size_t SecureGameGrant::TlsHostLength() const noexcept {
    return tlsHostLength_;
}

const char* SecureGameGrant::Audience() const noexcept {
    return audience_;
}

std::size_t SecureGameGrant::AudienceLength() const noexcept {
    return audienceLength_;
}

std::uint16_t SecureGameGrant::RoutePort() const noexcept {
    return routePort_;
}

std::uint16_t SecureGameGrant::TlsPort() const noexcept {
    return tlsPort_;
}

std::uint32_t SecureGameGrant::TargetServerId() const noexcept {
    return targetServerId_;
}

std::uint64_t SecureGameGrant::ExpiryUnixMilliseconds() const noexcept {
    return expiryUnixMilliseconds_;
}

bool SecureGameGrant::TryCopySecrets(
    void* grantId,
    std::size_t grantIdBytes,
    void* ticket,
    std::size_t ticketBytes) const noexcept {
    if (grantId != nullptr && grantIdBytes > 0) {
        SecureZeroMemory(
            grantId,
            (std::min)(grantIdBytes, SecureGameGrantIdBytes));
    }
    if (ticket != nullptr && ticketBytes > 0) {
        SecureZeroMemory(
            ticket,
            (std::min)(ticketBytes, SecureGameTicketBytes));
    }
    if (!valid_ ||
        grantId == nullptr ||
        grantIdBytes < SecureGameGrantIdBytes ||
        ticket == nullptr ||
        ticketBytes < SecureGameTicketBytes) {
        return false;
    }
    std::memcpy(grantId, grantId_, SecureGameGrantIdBytes);
    std::memcpy(ticket, ticket_, SecureGameTicketBytes);
    return true;
}

void SecureGameGrant::Clear() noexcept {
    SecureZeroMemory(routeHost_, sizeof(routeHost_));
    SecureZeroMemory(tlsHost_, sizeof(tlsHost_));
    SecureZeroMemory(audience_, sizeof(audience_));
    SecureZeroMemory(grantId_, sizeof(grantId_));
    SecureZeroMemory(ticket_, sizeof(ticket_));
    routeHostLength_ = 0;
    tlsHostLength_ = 0;
    audienceLength_ = 0;
    routePort_ = 0;
    tlsPort_ = 0;
    targetServerId_ = 0;
    expiryUnixMilliseconds_ = 0;
    valid_ = false;
}

void SecureGameGrant::MoveFrom(SecureGameGrant* other) noexcept {
    if (other == nullptr || !other->valid_) {
        Clear();
        return;
    }
    std::memcpy(routeHost_, other->routeHost_, sizeof(routeHost_));
    std::memcpy(tlsHost_, other->tlsHost_, sizeof(tlsHost_));
    std::memcpy(audience_, other->audience_, sizeof(audience_));
    std::memcpy(grantId_, other->grantId_, sizeof(grantId_));
    std::memcpy(ticket_, other->ticket_, sizeof(ticket_));
    routeHostLength_ = other->routeHostLength_;
    tlsHostLength_ = other->tlsHostLength_;
    audienceLength_ = other->audienceLength_;
    routePort_ = other->routePort_;
    tlsPort_ = other->tlsPort_;
    targetServerId_ = other->targetServerId_;
    expiryUnixMilliseconds_ = other->expiryUnixMilliseconds_;
    valid_ = true;
    other->Clear();
}

bool TryDecodeSecureGameGrant(
    const void* source,
    std::size_t sourceBytes,
    SecureGameGrant* grant) noexcept {
    if (grant == nullptr) {
        return false;
    }

    SecureGameGrant candidate;
    if (source == nullptr ||
        sourceBytes < SecureGameGrantMinimumBytes ||
        sourceBytes > SecureGameGrantMaximumBytes) {
        grant->Clear();
        return false;
    }

    const auto* input = static_cast<const std::uint8_t*>(source);
    const std::size_t routeBytes = input[1];
    const std::size_t tlsBytes = input[2];
    const std::size_t audienceBytes = input[3];
    const std::size_t variableBytes =
        routeBytes + tlsBytes + audienceBytes;
    if (input[0] != 1 ||
        routeBytes == 0 ||
        routeBytes > SecureGameRouteHostMaximumBytes ||
        tlsBytes == 0 ||
        tlsBytes > SecureGameTlsHostMaximumBytes ||
        audienceBytes == 0 ||
        audienceBytes > SecureGameAudienceMaximumBytes ||
        variableBytes >
            SecureGameGrantMaximumBytes -
                SecureGameGrantFixedBytes ||
        sourceBytes != SecureGameGrantFixedBytes + variableBytes) {
        grant->Clear();
        return false;
    }

    const std::uint16_t routePort = ReadUInt16(input + 4);
    const std::uint16_t tlsPort = ReadUInt16(input + 6);
    const std::uint32_t serverId = ReadUInt32(input + 8);
    if (routePort == 0 ||
        tlsPort == 0 ||
        serverId == 0 ||
        IsAllZero(input + 20, SecureGameGrantIdBytes) ||
        IsAllZero(input + 36, SecureGameTicketBytes)) {
        grant->Clear();
        return false;
    }

    const auto* route = input + SecureGameGrantFixedBytes;
    const auto* tlsHost = route + routeBytes;
    const auto* audience = tlsHost + tlsBytes;
    if (!IsCanonicalDnsName(
            route,
            routeBytes,
            SecureGameRouteHostMaximumBytes) ||
        !IsCanonicalDnsName(
            tlsHost,
            tlsBytes,
            SecureGameTlsHostMaximumBytes) ||
        !IsAudience(audience, audienceBytes)) {
        grant->Clear();
        return false;
    }

    std::memcpy(candidate.routeHost_, route, routeBytes);
    std::memcpy(candidate.tlsHost_, tlsHost, tlsBytes);
    std::memcpy(candidate.audience_, audience, audienceBytes);
    candidate.routeHostLength_ =
        static_cast<std::uint16_t>(routeBytes);
    candidate.tlsHostLength_ =
        static_cast<std::uint16_t>(tlsBytes);
    candidate.audienceLength_ =
        static_cast<std::uint8_t>(audienceBytes);
    candidate.routePort_ = routePort;
    candidate.tlsPort_ = tlsPort;
    candidate.targetServerId_ = serverId;
    candidate.expiryUnixMilliseconds_ = ReadUInt64(input + 12);
    std::memcpy(
        candidate.grantId_,
        input + 20,
        SecureGameGrantIdBytes);
    std::memcpy(
        candidate.ticket_,
        input + 36,
        SecureGameTicketBytes);
    candidate.valid_ = true;
    *grant = std::move(candidate);
    return true;
}

bool TryEncodeSecureGameBind(
    const SecureGameGrant& grant,
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (destination == nullptr ||
        destinationBytes < SecureGameBindBytes ||
        !grant.IsValid()) {
        WipeDestination(destination, destinationBytes);
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    SecureZeroMemory(output, SecureGameBindBytes);
    output[0] = 1;
    if (!grant.TryCopySecrets(
            output + 4,
            SecureGameGrantIdBytes,
            output + 20,
            SecureGameTicketBytes)) {
        SecureZeroMemory(output, SecureGameBindBytes);
        return false;
    }
    return true;
}

bool TryDecodeSecureBindResult(
    const void* source,
    std::size_t sourceBytes,
    SecureBindStatus* status) noexcept {
    if (status == nullptr) {
        return false;
    }
    *status = SecureBindStatus::PolicyRejected;
    if (source == nullptr ||
        sourceBytes != SecureBindResultBytes) {
        return false;
    }

    const auto* input = static_cast<const std::uint8_t*>(source);
    const auto decoded =
        static_cast<SecureBindStatus>(ReadUInt16(input));
    if (input[2] != 0 ||
        input[3] != 0 ||
        !IsBindStatus(decoded)) {
        return false;
    }
    *status = decoded;
    return true;
}

} // namespace godswar::network
