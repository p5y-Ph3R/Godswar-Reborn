#include "LoopbackPeerOwnerTests.h"

#include "../src/LoopbackPeerOwner.h"

#include <iphlpapi.h>

#include <cstdio>

int RunLoopbackPeerOwnerTests() {
    using godswar::network::IsMatchingLoopbackPeerOwner;
    using godswar::network::LoopbackConnectionTuple;
    using godswar::network::LoopbackTcpOwnerEntry;

    int failures = 0;
    const DWORD processId = 4242;
    const LoopbackConnectionTuple connection{
        INADDR_LOOPBACK,
        43100,
        INADDR_LOOPBACK,
        52100,
    };
    const LoopbackTcpOwnerEntry owned{
        INADDR_LOOPBACK,
        52100,
        INADDR_LOOPBACK,
        43100,
        MIB_TCP_STATE_ESTAB,
        processId,
    };
    if (!IsMatchingLoopbackPeerOwner(
            connection,
            owned,
            processId)) {
        std::fprintf(stderr, "same-process loopback tuple was rejected\n");
        ++failures;
    }

    auto foreign = owned;
    foreign.processId = processId + 1;
    if (IsMatchingLoopbackPeerOwner(
            connection,
            foreign,
            processId)) {
        std::fprintf(stderr, "foreign-process loopback tuple was accepted\n");
        ++failures;
    }

    auto reversed = owned;
    reversed.localPort = connection.serverPort;
    reversed.remotePort = connection.peerPort;
    if (IsMatchingLoopbackPeerOwner(
            connection,
            reversed,
            processId)) {
        std::fprintf(stderr, "server-side tuple was accepted as the peer\n");
        ++failures;
    }
    return failures;
}
