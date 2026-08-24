#include "SecureWarehouseIdentityTests.h"

int RunSecureWarehouseParserTests();
int RunSecureWarehouseRegistryTests();

int RunSecureWarehouseIdentityTests() {
    return RunSecureWarehouseParserTests() +
        RunSecureWarehouseRegistryTests();
}
