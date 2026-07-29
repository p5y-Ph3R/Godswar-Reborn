#include "SecureHolyStoneIdentityTests.h"

int RunSecureHolyStoneParserTests();
int RunSecureHolyStoneRegistryTests();

int RunSecureHolyStoneIdentityTests() {
    return RunSecureHolyStoneParserTests() +
        RunSecureHolyStoneRegistryTests();
}
