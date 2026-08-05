#include "SecureHolyStoneIdentityTests.h"
#include "SecureHolyStoneAdvancedDrillParserTests.h"
#include "SecureHolyStoneCombineIdentityTests.h"
#include "SecureHolyStoneImplementSpiritIdentityTests.h"
#include "SecureHolyStoneUpgradeIdentityTests.h"

int RunSecureHolyStoneParserTests();
int RunSecureHolyStoneRegistryTests();

int RunSecureHolyStoneIdentityTests() {
    return RunSecureHolyStoneParserTests() +
        RunSecureHolyStoneAdvancedDrillParserTests() +
        RunSecureHolyStoneCombineIdentityTests() +
        RunSecureHolyStoneImplementSpiritIdentityTests() +
        RunSecureHolyStoneUpgradeIdentityTests() +
        RunSecureHolyStoneRegistryTests();
}
