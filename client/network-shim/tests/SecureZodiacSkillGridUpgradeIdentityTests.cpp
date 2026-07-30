#include "SecureZodiacSkillGridUpgradeIdentityTests.h"

int RunSecureZodiacSkillGridUpgradeParserTests();
int RunSecureZodiacSkillGridUpgradeRegistryTests();

int RunSecureZodiacSkillGridUpgradeIdentityTests() {
    return RunSecureZodiacSkillGridUpgradeParserTests() +
        RunSecureZodiacSkillGridUpgradeRegistryTests();
}
