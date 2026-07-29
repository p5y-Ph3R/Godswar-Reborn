#include "SecureEconomyIdentityTests.h"

#include "SecureEquipmentBagTransferIdentityTests.h"
#include "SecureForgeCommandIdentityTests.h"
#include "SecureForgeResultIdentityTests.h"
#include "SecureGearEnhancerIdentityTests.h"
#include "SecureGearMentorCommandIdentityTests.h"
#include "SecureGearMentorDecomposeIdentityTests.h"
#include "SecureKitBagItemDeleteIdentityTests.h"
#include "SecureKitBagItemMoveIdentityTests.h"

int RunSecureEconomyIdentityTests() {
    return RunSecureEquipmentBagTransferIdentityTests() +
        RunSecureForgeCommandIdentityTests() +
        RunSecureForgeResultIdentityTests() +
        RunSecureGearEnhancerIdentityTests() +
        RunSecureGearMentorCommandIdentityTests() +
        RunSecureGearMentorDecomposeIdentityTests() +
        RunSecureKitBagItemDeleteIdentityTests() +
        RunSecureKitBagItemMoveIdentityTests();
}
