#include "AvatarPreviewGateTests.h"
#include "AvatarPreloadTests.h"
#include "BoundedChunkQueueTests.h"
#include "EndpointManifestTests.h"
#include "ExternalTcpConnectorTests.h"
#include "LoopbackAcceptorTests.h"
#include "LoopbackPeerOwnerTests.h"
#include "NativeClientBridgeTests.h"
#include "NativeClientCoordinatorTests.h"
#include "OpaqueDuplexPumpTests.h"
#include "SchannelClientStreamTests.h"
#include "SecureClientRuntimeTests.h"
#include "SecureClientSessionTests.h"
#include "SecureClientProtocolTests.h"
#include "SecureGameControlTests.h"
#include "SecureGameGrantRegistryTests.h"
#include "SecureManifestProbe.h"
#include "SecureOuterControlTests.h"
#include "SecureOuterStreamTests.h"
#include "SecureOuterUdpGrantTests.h"
#include "SecureUdpBindingGrantTests.h"
#include "VerifiedImageFileTests.h"
#include "WinSocketByteStreamTests.h"

#include "../src/LegacyClientApi.h"
#include "../src/NetClientProxy.h"

#include <Windows.h>

#include <cstdint>
#include <cwchar>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::ILegacyNetClient;
using godswar::network::ClientRoute;
using godswar::network::ClientRouteDecision;
using godswar::network::ClientRoutePolicy;
using godswar::network::ClientRoutesEqual;
using godswar::network::NativeClientCoordinator;
using godswar::network::NativeClientRegistryCapacity;
using godswar::network::NativeProxyId;
using godswar::network::NetClientProxy;
using godswar::network::TryCopyClientRoute;
using Factory = void*(__cdecl*)();

int Failures = 0;

void Check(bool condition, const char* message) {
    if (condition) {
        return;
    }

    std::fprintf(stderr, "FAIL: %s\n", message);
    ++Failures;
}

class FakeLegacyClient final : public ILegacyNetClient {
public:
    std::uint32_t Release() override {
        ++releaseCalls;
        return 73;
    }

    void SetHost(const char* hostValue, std::uint16_t portValue) override {
        ++setHostCalls;
        host = hostValue;
        port = portValue;
    }

    bool Connect() override {
        ++connectCalls;
        return true;
    }

    void DisConnect() override {
        ++disconnectCalls;
    }

    void Process() override {
        ++processCalls;
    }

    std::uint32_t GetStatus() const override {
        ++statusCalls;
        return 0x12345678;
    }

    void* PickMsg() override {
        ++pickCalls;
        return &messageValue;
    }

    bool SendMsg(const void* dataValue, int sizeValue) override {
        ++sendCalls;
        data = dataValue;
        size = sizeValue;
        return true;
    }

    long GetMsgNum() override {
        ++messageCountCalls;
        return messageCountResult;
    }

    int releaseCalls = 0;
    int setHostCalls = 0;
    int connectCalls = 0;
    int disconnectCalls = 0;
    int processCalls = 0;
    mutable int statusCalls = 0;
    int pickCalls = 0;
    int sendCalls = 0;
    int messageCountCalls = 0;
    const char* host = nullptr;
    std::uint16_t port = 0;
    const void* data = nullptr;
    int size = 0;
    std::uint32_t messageValue = 0xABCDEF01;
    long messageCountResult = 19;
};

bool IsAddressInModule(const void* address, HMODULE module) {
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(address, &memory, sizeof(memory)) == 0) {
        return false;
    }

    return memory.AllocationBase == module;
}

void RunProxyUnitChecks() {
    NativeClientCoordinator coordinator;
    const auto beforeCreate = coordinator.Snapshot();
    Check(
        beforeCreate.capacity == NativeClientRegistryCapacity,
        "process coordinator capacity changed");

    FakeLegacyClient legacy;
    auto* client =
        NetClientProxy::CreateWithCoordinatorForTesting(
            &legacy,
            &coordinator);
    Check(client != nullptr, "proxy factory returned null");
    if (client == nullptr) {
        return;
    }

    const auto afterCreate = coordinator.Snapshot();
    Check(
        afterCreate.registered == beforeCreate.registered + 1,
        "proxy factory did not register exactly one native client");

    const std::uint8_t payload[] = {1, 2, 3, 4};
    client->SetHost("127.0.0.1", 5999);
    const auto afterSetHost = coordinator.Snapshot();
    Check(
        afterSetHost.hostReady == beforeCreate.hostReady + 1,
        "proxy SetHost did not publish one bounded route");
    Check(client->Connect(), "Connect return value was not delegated");
    const auto afterConnect = coordinator.Snapshot();
    Check(
        afterConnect.connected == beforeCreate.connected + 1,
        "pass-through proxy did not enter connected state");
    client->Process();
    Check(
        client->GetStatus() == 0x12345678,
        "GetStatus return value was not delegated");
    Check(
        client->PickMsg() == &legacy.messageValue,
        "PickMsg return value was not delegated");
    Check(
        client->SendMsg(payload, sizeof(payload)),
        "SendMsg return value was not delegated");
    Check(
        client->GetMsgNum() == 19,
        "GetMsgNum return value was not delegated");
    client->DisConnect();
    const auto afterDisconnect = coordinator.Snapshot();
    Check(
        afterDisconnect.connected == beforeCreate.connected &&
            afterDisconnect.hostReady == beforeCreate.hostReady,
        "proxy disconnect did not reset coordinator state");

    Check(legacy.setHostCalls == 1, "SetHost call count changed");
    Check(
        legacy.host != nullptr &&
            std::strcmp(legacy.host, "127.0.0.1") == 0,
        "SetHost host argument changed");
    Check(legacy.port == 5999, "SetHost port argument changed");
    Check(legacy.connectCalls == 1, "Connect call count changed");
    Check(legacy.processCalls == 1, "Process call count changed");
    Check(legacy.statusCalls == 1, "GetStatus call count changed");
    Check(legacy.pickCalls == 1, "PickMsg call count changed");
    Check(legacy.sendCalls == 1, "SendMsg call count changed");
    Check(legacy.data == payload, "SendMsg data pointer changed");
    Check(
        legacy.size == static_cast<int>(sizeof(payload)),
        "SendMsg size changed");
    Check(
        legacy.messageCountCalls == 1,
        "GetMsgNum call count changed");
    Check(
        legacy.disconnectCalls == 1,
        "DisConnect call count changed");

    Check(client->Release() == 73, "Release return value was not delegated");
    Check(legacy.releaseCalls == 1, "Release call count changed");
    const auto afterRelease = coordinator.Snapshot();
    Check(
        afterRelease.registered == beforeCreate.registered,
        "proxy release did not unregister before stock release");
}

struct ProxyRoutePolicyContext final {
    ClientRoute route{};
    ClientRouteDecision decision = ClientRouteDecision::Reject;
};

ClientRouteDecision ClassifyProxyTestRoute(
    void* contextValue,
    NativeProxyId,
    const ClientRoute& route) noexcept {
    auto* context =
        static_cast<ProxyRoutePolicyContext*>(contextValue);
    return context != nullptr &&
        ClientRoutesEqual(context->route, route)
        ? context->decision
        : ClientRouteDecision::Reject;
}

void RunProxyNoDowngradeChecks() {
    constexpr ClientRouteDecision decisions[] = {
        ClientRouteDecision::Login,
        ClientRouteDecision::Game,
        ClientRouteDecision::Reject,
    };

    for (const auto decision : decisions) {
        ProxyRoutePolicyContext context{};
        context.decision = decision;
        Check(
            TryCopyClientRoute(
                "secure.reborn.test",
                6599,
                &context.route),
            "proxy no-downgrade route setup failed");
        NativeClientCoordinator coordinator(ClientRoutePolicy{
            &context,
            ClassifyProxyTestRoute,
        });
        FakeLegacyClient legacy;
        auto* client =
            NetClientProxy::CreateWithCoordinatorForTesting(
                &legacy,
                &coordinator);
        Check(
            client != nullptr,
            "coordinator-injected proxy factory failed");
        if (client == nullptr) {
            continue;
        }

        client->SetHost("secure.reborn.test", 6599);
        SetLastError(ERROR_SUCCESS);
        Check(
            !client->Connect(),
            "secure route downgraded to raw stock Connect");
        Check(
            legacy.setHostCalls == 0 &&
                legacy.connectCalls == 0,
            "secure/rejected route touched the raw stock endpoint");
        Check(
            GetLastError() == ERROR_ACCESS_DENIED,
            "secure/rejected route returned an unstable error");
        client->Release();
        Check(
            legacy.releaseCalls == 1 &&
                coordinator.Snapshot().registered == 0,
            "injected proxy did not release/unregister");
    }

    ProxyRoutePolicyContext context{};
    context.decision = ClientRouteDecision::Login;
    Check(
        TryCopyClientRoute(
            "secure.reborn.test",
            6599,
            &context.route),
        "invalid-route reset setup failed");
    NativeClientCoordinator coordinator(ClientRoutePolicy{
        &context,
        ClassifyProxyTestRoute,
    });
    FakeLegacyClient legacy;
    auto* client = NetClientProxy::CreateWithCoordinatorForTesting(
        &legacy,
        &coordinator);
    Check(client != nullptr, "invalid-route proxy fixture failed");
    if (client == nullptr) {
        return;
    }

    client->SetHost("secure.reborn.test", 6599);
    char overlong[255]{};
    std::memset(overlong, 'x', sizeof(overlong) - 1);
    client->SetHost(overlong, 6599);
    Check(
        !client->Connect() &&
            legacy.setHostCalls == 0 &&
            legacy.connectCalls == 0,
        "invalid SetHost reused an older authoritative route");
    client->Release();
}

void RunInstalledShimProbe(const wchar_t* shimPath) {
    const auto module = LoadLibraryExW(
        shimPath,
        nullptr,
        LOAD_WITH_ALTERED_SEARCH_PATH);
    Check(module != nullptr, "could not load staged Net.dll");
    if (module == nullptr) {
        std::fprintf(
            stderr,
            "Win32 error: %lu\n",
            static_cast<unsigned long>(GetLastError()));
        return;
    }

    const auto clientByName = GetProcAddress(module, "NetClientCreate");
    const auto clientByOrdinal =
        GetProcAddress(module, MAKEINTRESOURCEA(1));
    const auto serviceByName = GetProcAddress(module, "NetServiceCreate");
    const auto serviceByOrdinal =
        GetProcAddress(module, MAKEINTRESOURCEA(2));

    Check(
        clientByName != nullptr && clientByName == clientByOrdinal,
        "NetClientCreate name/ordinal 1 contract changed");
    Check(
        serviceByName != nullptr && serviceByName == serviceByOrdinal,
        "NetServiceCreate name/ordinal 2 contract changed");

    if (clientByOrdinal != nullptr) {
        const auto factory =
            reinterpret_cast<Factory>(clientByOrdinal);

        for (int iteration = 0; iteration < 32; ++iteration) {
            auto* client =
                static_cast<ILegacyNetClient*>(factory());
            Check(client != nullptr, "stock client factory returned null");
            if (client == nullptr) {
                break;
            }

            auto** vtable = *reinterpret_cast<void***>(client);
            Check(vtable != nullptr, "proxy vtable was null");
            if (vtable != nullptr) {
                for (int slot = 0; slot < 9; ++slot) {
                    Check(vtable[slot] != nullptr, "proxy vtable slot was null");
                    Check(
                        IsAddressInModule(vtable[slot], module),
                        "proxy vtable slot was not owned by Net.dll");
                }
            }

            client->SetHost("127.0.0.1", 5999);
            static_cast<void>(client->GetStatus());
            client->Process();
            static_cast<void>(client->GetMsgNum());
            static_cast<void>(client->PickMsg());
            client->DisConnect();
            client->Release();
        }
    }

    FreeLibrary(module);
}

void RunRejectedShimProbe(
    const wchar_t* shimPath,
    DWORD expectedError) {
    const auto module = LoadLibraryExW(
        shimPath,
        nullptr,
        LOAD_WITH_ALTERED_SEARCH_PATH);
    Check(module != nullptr, "could not load rejection-test Net.dll");
    if (module == nullptr) {
        return;
    }

    const auto clientFactory = reinterpret_cast<Factory>(
        GetProcAddress(module, MAKEINTRESOURCEA(1)));
    const auto serviceFactory = reinterpret_cast<Factory>(
        GetProcAddress(module, MAKEINTRESOURCEA(2)));
    Check(clientFactory != nullptr, "rejection-test client factory missing");
    Check(serviceFactory != nullptr, "rejection-test service factory missing");

    for (int iteration = 0; iteration < 2; ++iteration) {
        if (clientFactory != nullptr) {
            SetLastError(ERROR_SUCCESS);
            Check(
                clientFactory() == nullptr,
                "unsupported legacy client factory did not fail closed");
            const auto actualError = GetLastError();
            Check(
                actualError == expectedError,
                "unsupported legacy client returned an unstable Win32 error");
        }

        if (serviceFactory != nullptr) {
            SetLastError(ERROR_SUCCESS);
            Check(
                serviceFactory() == nullptr,
                "unsupported legacy service factory did not fail closed");
            const auto actualError = GetLastError();
            Check(
                actualError == expectedError,
                "unsupported legacy service returned an unstable Win32 error");
        }
    }

    FreeLibrary(module);
}

} // namespace

int wmain(int argumentCount, wchar_t** arguments) {
    const bool full =
        argumentCount == 1;
    const bool offlineOnly =
        argumentCount == 2 &&
        std::wcscmp(arguments[1], L"--offline") == 0;
    const bool offlineProbe =
        argumentCount == 3 &&
        std::wcscmp(arguments[1], L"--offline-probe") == 0;
    const bool offline = offlineOnly || offlineProbe;
    const bool probe =
        argumentCount == 3 &&
        (std::wcscmp(arguments[1], L"--probe") == 0 ||
            offlineProbe);
    const bool rejectedProbe =
        argumentCount == 4 &&
        std::wcscmp(arguments[1], L"--probe-rejected") == 0;
    const bool manifestProbe =
        argumentCount == 4 &&
        std::wcscmp(
            arguments[1],
            L"--offline-manifest-probe") == 0;
    const bool contractProbe =
        argumentCount == 3 &&
        std::wcscmp(
            arguments[1],
            L"--offline-contract-probe") == 0;
    const bool foreignPeerHelper =
        argumentCount == 4 &&
        std::wcscmp(
            arguments[1],
            L"--foreign-loopback-connect") == 0;
    if (!full && !offline && !probe && !rejectedProbe &&
        !manifestProbe && !contractProbe &&
        !foreignPeerHelper) {
        std::fprintf(
            stderr,
            "Usage: Godswar.NetShim.Checks.exe "
            "[--offline | --offline-probe <Net.dll> | "
            "--offline-contract-probe <Net.dll> | "
            "--offline-manifest-probe <Net.dll> <RebornNetwork.gwem> | "
            "--probe <Net.dll> | "
            "--probe-rejected <Net.dll> <Win32Error>]\n");
        return 2;
    }
    if (foreignPeerHelper) {
        return RunForeignLoopbackPeerHelper(
            arguments[2],
            arguments[3]);
    }
    if (contractProbe) {
        return RunSecureCandidateContractProbe(arguments[2]);
    }
    if (manifestProbe) {
        return RunSecureManifestProbe(
            arguments[2],
            arguments[3]);
    }

    DWORD expectedProbeError = ERROR_SUCCESS;
    if (rejectedProbe) {
        wchar_t* parseEnd = nullptr;
        const auto parsed = std::wcstoull(
            arguments[3],
            &parseEnd,
            10);
        if (parseEnd == arguments[3] ||
            *parseEnd != L'\0' ||
            parsed > MAXDWORD) {
            std::fprintf(
                stderr,
                "Expected error must be a decimal DWORD.\n");
            return 2;
        }
        expectedProbeError = static_cast<DWORD>(parsed);
    }

    RunProxyUnitChecks();
    RunProxyNoDowngradeChecks();
    Failures += RunAvatarPreviewGateTests();
    Failures += RunAvatarPreloadTests();
    Failures += RunBoundedChunkQueueTests();
    Failures += RunEndpointManifestTests();
    Failures += RunLoopbackPeerOwnerTests();
    Failures += RunOpaqueDuplexPumpTests();
    Failures += RunSchannelClientStreamTests(!offline);
    Failures += RunSecureClientRuntimeTests();
    Failures += RunSecureClientSessionTests();
    Failures += RunSecureClientProtocolTests();
    Failures += RunSecureGameControlTests();
    Failures += RunSecureGameGrantRegistryTests();
    Failures += RunSecureOuterControlTests();
    Failures += RunSecureOuterStreamTests();
    Failures += RunSecureOuterUdpGrantTests();
    Failures += RunSecureUdpBindingGrantTests();
    Failures += RunVerifiedImageFileTests();
    Failures += RunNativeClientCoordinatorTests();
    if (!offline) {
        Failures += RunExternalTcpConnectorTests();
        Failures += RunWinSocketByteStreamTests();
        Failures += RunLoopbackAcceptorTests();
        Failures += RunNativeClientBridgeTests();
    }

    if (probe) {
        RunInstalledShimProbe(arguments[2]);
    } else if (rejectedProbe) {
        RunRejectedShimProbe(
            arguments[2],
            expectedProbeError);
    }

    if (Failures != 0) {
        std::fprintf(stderr, "%d network-shim check(s) failed.\n", Failures);
        return 1;
    }

    std::puts(
        offline
            ? "All offline network-shim checks passed."
            : "All network-shim checks passed.");
    return 0;
}
