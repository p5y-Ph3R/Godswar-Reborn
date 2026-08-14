#include "NetworkShimTestSuite.h"

#include "AvatarPreloadLifecycleTests.h"
#include "AvatarPreloadTests.h"
#include "AvatarPreviewGateTests.h"
#include "BoundedChunkQueueTests.h"
#include "EndpointManifestTests.h"
#include "ExternalTcpConnectorTests.h"
#include "LegacyCommandDescriptorStreamTests.h"
#include "LoopbackAcceptorTests.h"
#include "LoopbackPeerOwnerTests.h"
#include "NativeClientBridgeTests.h"
#include "NativeClientCoordinatorTests.h"
#include "OpaqueDuplexPumpTests.h"
#include "SchannelClientStreamTests.h"
#include "SecureCharacterLifecycleIdentityTests.h"
#include "SecureClientProtocolTests.h"
#include "SecureClientRuntimeTests.h"
#include "SecureClientSessionTests.h"
#include "SecureEconomyIdentityTests.h"
#include "SecureGameControlTests.h"
#include "SecureGameGrantRegistryTests.h"
#include "SecureLegacyCommandResultStreamTests.h"
#include "SecureOuterControlTests.h"
#include "SecureOuterStreamTests.h"
#include "SecureOuterUdpGrantTests.h"
#include "SecurePendingOperationRegistryTests.h"
#include "SecurePetIdentityTestSuite.h"
#include "SecureRealtimeMovementChannelTests.h"
#include "SecureRealtimeMovementTests.h"
#include "SecureUdpBindingGrantTests.h"
#include "SecureUdpClientChannelTests.h"
#include "SecureUdpClientWorkerTests.h"
#include "SecureUdpProtectedProtocolTests.h"
#include "VerifiedImageFileTests.h"
#include "WinSocketByteStreamTests.h"

int RunNetworkShimTestSuite(bool offline) {
    int failures = 0;
    failures += RunAvatarPreviewGateTests();
    failures += RunAvatarPreloadLifecycleTests();
    failures += RunAvatarPreloadTests();
    failures += RunBoundedChunkQueueTests();
    failures += RunEndpointManifestTests();
    failures += RunLoopbackPeerOwnerTests();
    failures += RunLegacyCommandDescriptorStreamTests();
    failures += RunOpaqueDuplexPumpTests();
    failures += RunSchannelClientStreamTests(!offline);
    failures += RunSecureClientRuntimeTests();
    failures += RunSecureClientSessionTests();
    failures += RunSecureClientProtocolTests();
    failures += RunSecureCharacterLifecycleIdentityTests();
    failures += RunSecurePetIdentityTestSuite();
    failures += RunSecurePendingOperationRegistryTests();
    failures += RunSecureGameControlTests();
    failures += RunSecureGameGrantRegistryTests();
    failures += RunSecureEconomyIdentityTests();
    failures += RunSecureLegacyCommandResultStreamTests();
    failures += RunSecureRealtimeMovementTests();
    failures += RunSecureRealtimeMovementChannelTests();
    failures += RunSecureOuterControlTests();
    failures += RunSecureOuterStreamTests();
    failures += RunSecureOuterUdpGrantTests();
    failures += RunSecureUdpBindingGrantTests();
    failures += RunSecureUdpClientChannelTests();
    failures += RunSecureUdpClientWorkerTests();
    failures += RunSecureUdpProtectedProtocolTests();
    failures += RunVerifiedImageFileTests();
    failures += RunNativeClientCoordinatorTests();
    if (!offline) {
        failures += RunExternalTcpConnectorTests();
        failures += RunWinSocketByteStreamTests();
        failures += RunLoopbackAcceptorTests();
        failures += RunNativeClientBridgeTests();
    }

    return failures;
}
