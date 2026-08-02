#include "SecureEconomyIdentityTests.h"

#include "SecureClassSuitIdentityTests.h"
#include "SecureEquipmentBagTransferIdentityTests.h"
#include "SecureForgeCommandIdentityTests.h"
#include "SecureForgeResultIdentityTests.h"
#include "SecureGearEnhancerIdentityTests.h"
#include "SecureGearMentorCommandIdentityTests.h"
#include "SecureGearMentorDecomposeIdentityTests.h"
#include "SecureHolyStoneIdentityTests.h"
#include "SecureHolySuitIdentityTests.h"
#include "SecureKitBagItemDeleteIdentityTests.h"
#include "SecureKitBagItemMoveIdentityTests.h"
#include "SecureZodiacSkillGridSelectionIdentityTests.h"
#include "SecureZodiacSkillGridUpgradeIdentityTests.h"

int RunSecureEconomyIdentityTests() {
    return RunSecureClassSuitIdentityTests() +
        RunSecureEquipmentBagTransferIdentityTests() +
        RunSecureForgeCommandIdentityTests() +
        RunSecureForgeResultIdentityTests() +
        RunSecureGearEnhancerIdentityTests() +
        RunSecureGearMentorCommandIdentityTests() +
        RunSecureGearMentorDecomposeIdentityTests() +
        RunSecureHolyStoneIdentityTests() +
        RunSecureHolySuitIdentityTests() +
        RunSecureKitBagItemDeleteIdentityTests() +
        RunSecureKitBagItemMoveIdentityTests() +
        RunSecureZodiacSkillGridSelectionIdentityTests() +
        RunSecureZodiacSkillGridUpgradeIdentityTests();
}
