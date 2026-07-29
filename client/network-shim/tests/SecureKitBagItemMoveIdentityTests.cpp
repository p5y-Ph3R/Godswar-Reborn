#include "SecureKitBagItemMoveIdentityTests.h"

int RunSecureKitBagItemMoveParserTests();
int RunSecureKitBagItemMoveRegistryTests();

int RunSecureKitBagItemMoveIdentityTests() {
    return RunSecureKitBagItemMoveParserTests() +
        RunSecureKitBagItemMoveRegistryTests();
}
