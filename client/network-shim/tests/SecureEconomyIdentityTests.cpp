#include "SecureEconomyIdentityTests.h"

#include "SecureEquipmentBagTransferIdentityTests.h"
#include "SecureForgeCommandIdentityTests.h"
#include "SecureForgeResultIdentityTests.h"
#include "SecureGearEnhancerIdentityTests.h"
#include "SecureGearMentorCommandIdentityTests.h"
#include "SecureGearMentorDecomposeIdentityTests.h"
#include "SecureHolyStoneIdentityTests.h"
#include "SecureKitBagItemDeleteIdentityTests.h"
#include "SecureKitBagItemMoveIdentityTests.h"
#include "SecureZodiacSkillGridUpgradeIdentityTests.h"

int RunSecureEconomyIdentityTests() {
    return RunSecureEquipmentBagTransferIdentityTests() +
        RunSecureForgeCommandIdentityTests() +
        RunSecureForgeResultIdentityTests() +
        RunSecureGearEnhancerIdentityTests() +
        RunSecureGearMentorCommandIdentityTests() +
        RunSecureGearMentorDecomposeIdentityTests() +
        RunSecureHolyStoneIdentityTests() +
        RunSecureKitBagItemDeleteIdentityTests() +
        RunSecureKitBagItemMoveIdentityTests() +
        RunSecureZodiacSkillGridUpgradeIdentityTests();
}
