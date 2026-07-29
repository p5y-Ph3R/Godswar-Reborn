#include "SecureGearEnhancerIdentityTests.h"

#include "SecureGearEnhancerResultIdentityTests.h"
#include "SecureGearMentorAttributeIdentityTests.h"
#include "SecureOriginEnhancerIdentityTests.h"
#include "SecureOriginEnhancerPacketTests.h"

int RunSecureGearEnhancerIdentityTests() {
    return RunSecureGearMentorAttributeIdentityTests() +
        RunSecureOriginEnhancerIdentityTests() +
        RunSecureOriginEnhancerPacketTests() +
        RunSecureGearEnhancerResultIdentityTests();
}
