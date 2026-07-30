#include "SecureZodiacSkillGridSelectionIdentityTests.h"

int RunSecureZodiacSkillGridSelectionParserTests();
int RunSecureZodiacSkillGridSelectionRegistryTests();

int RunSecureZodiacSkillGridSelectionIdentityTests() {
    return RunSecureZodiacSkillGridSelectionParserTests() +
        RunSecureZodiacSkillGridSelectionRegistryTests();
}
