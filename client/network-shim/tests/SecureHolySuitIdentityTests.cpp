#include "SecureHolySuitIdentityTests.h"

int RunSecureHolySuitParserTests();
int RunSecureHolySuitRegistryTests();

int RunSecureHolySuitIdentityTests() {
    return RunSecureHolySuitParserTests() +
        RunSecureHolySuitRegistryTests();
}
