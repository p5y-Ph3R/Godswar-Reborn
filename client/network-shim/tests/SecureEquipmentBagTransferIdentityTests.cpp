#include "SecureEquipmentBagTransferIdentityTests.h"

int RunSecureEquipmentBagTransferParserTests();
int RunSecureEquipmentBagTransferRegistryTests();

int RunSecureEquipmentBagTransferIdentityTests() {
    return RunSecureEquipmentBagTransferParserTests() +
        RunSecureEquipmentBagTransferRegistryTests();
}
