#include "SecurePetIdentityTestSuite.h"

#include "SecurePetAppearanceIdentityTests.h"
#include "SecurePetBasicSavvyIdentityTests.h"
#include "SecurePetBindIdentityTests.h"
#include "SecurePetCommandIdentityTests.h"
#include "SecurePetOwnerMergeIdentityTests.h"
#include "SecurePetManagerUtilityIdentityTests.h"
#include "SecurePetRebirthIdentityTests.h"
#include "SecurePetSoulContractIdentityTests.h"
#include "SecurePetToPetMergeIdentityTests.h"

int RunSecurePetIdentityTestSuite() {
    int failures = 0;
    failures += RunSecurePetCommandIdentityTests();
    failures += RunSecurePetBasicSavvyIdentityTests();
    failures += RunSecurePetAppearanceIdentityTests();
    failures += RunSecurePetBindIdentityTests();
    failures += RunSecurePetOwnerMergeIdentityTests();
    failures += RunSecurePetManagerUtilityIdentityTests();
    failures += RunSecurePetRebirthIdentityTests();
    failures += RunSecurePetSoulContractIdentityTests();
    failures += RunSecurePetToPetMergeIdentityTests();
    return failures;
}
