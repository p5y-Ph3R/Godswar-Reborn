#include "SecureOuterStream.h"

#include <Windows.h>

#include <cstring>
#include <utility>

namespace godswar::network {

bool SecureOuterStream::TryCopyConnectionId(
    std::uint8_t* destination,
    std::size_t destinationBytes) const noexcept {
    if (destination == nullptr ||
        destinationBytes < sizeof(connectionId_)) {
        return false;
    }

    bool copied = false;
    AcquireSRWLockShared(&snapshotLock_);
    if (connectionIdRetained_) {
        std::memcpy(
            destination,
            connectionId_,
            sizeof(connectionId_));
        copied = true;
    } else {
        SecureZeroMemory(destination, sizeof(connectionId_));
    }
    ReleaseSRWLockShared(&snapshotLock_);
    return copied;
}

bool SecureOuterStream::TryTakeUdpBindingGrant(
    SecureUdpBindingGrant* grant) noexcept {
    if (grant == nullptr) {
        return false;
    }
    grant->Clear();

    bool taken = false;
    AcquireSRWLockExclusive(&snapshotLock_);
    if (udpBindingGrantAvailable_) {
        *grant = std::move(udpBindingGrant_);
        udpBindingGrantAvailable_ = false;
        taken = true;
    }
    ReleaseSRWLockExclusive(&snapshotLock_);
    return taken;
}

bool SecureOuterStream::TryRetainUdpBindingGrant(
    const void* payload,
    std::size_t payloadBytes,
    SecureOuterFailure* failure) noexcept {
    if (failure == nullptr) {
        return false;
    }
    *failure = SecureOuterFailure::UdpGrantDecode;

    SecureUdpBindingGrant grant;
    if (!TryDecodeSecureUdpBindingGrant(
            payload,
            payloadBytes,
            &grant)) {
        return false;
    }

    bool retained = false;
    AcquireSRWLockExclusive(&snapshotLock_);
    if (role_ != SecureEndpointRole::Game ||
        !gameBound_ ||
        !connectionIdRetained_ ||
        udpBindingGrantReceived_) {
        *failure = SecureOuterFailure::UdpGrantState;
    } else if (!grant.ConnectionIdEquals(
                   connectionId_,
                   sizeof(connectionId_))) {
        *failure = SecureOuterFailure::UdpGrantConnection;
    } else {
        udpBindingGrant_ = std::move(grant);
        udpBindingGrantReceived_ = true;
        udpBindingGrantAvailable_ = true;
        retained = true;
    }
    ReleaseSRWLockExclusive(&snapshotLock_);
    return retained;
}

void SecureOuterStream::ClearUdpBindingState() noexcept {
    AcquireSRWLockExclusive(&snapshotLock_);
    SecureZeroMemory(connectionId_, sizeof(connectionId_));
    connectionIdRetained_ = false;
    udpBindingGrant_.Clear();
    udpBindingGrantReceived_ = false;
    udpBindingGrantAvailable_ = false;
    ReleaseSRWLockExclusive(&snapshotLock_);
}

} // namespace godswar::network
