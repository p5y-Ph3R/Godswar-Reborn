#include "SecurePendingOperationRegistry.h"

#include <cstring>

namespace godswar::network {

SecurePendingOperationSnapshot
SecurePendingOperationRegistry::Snapshot() noexcept {
    SecurePendingOperationSnapshot snapshot{};
    std::uint64_t now = 0;
    if (!ReadNow(&now)) {
        return snapshot;
    }

    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    for (const auto& entry : entries_) {
        if (entry.occupied) {
            ++snapshot.pending;
        }
    }
    for (const auto& tombstone : tombstones_) {
        if (tombstone.occupied) {
            ++snapshot.resolved;
        }
    }
    snapshot.hasPrincipal = hasPrincipal_;
    snapshot.hasCharacter = hasCharacter_;
    snapshot.characterId = characterId_;
    snapshot.hasSelection =
        TryGetIdentitySelection(
            snapshot.selectedBagSlots,
            &snapshot.selectionCount);
    snapshot.selectedBagSlot = snapshot.hasSelection
        ? snapshot.selectedBagSlots[0]
        : -1;
    snapshot.combinePageArmed = combinePageArmed_;
    snapshot.combineNpcId = combineNpcId_;
    snapshot.hasForgeEquipment =
        forgeEquipmentBagSlot_ >= 0;
    snapshot.forgeEquipmentBagSlot =
        forgeEquipmentBagSlot_;
    snapshot.hasForgePrimaryMaterial =
        forgePrimaryMaterialBagSlot_ >= 0;
    snapshot.forgePrimaryMaterialBagSlot =
        forgePrimaryMaterialBagSlot_;
    snapshot.forgeOddsCount = forgeOddsCount_;
    std::memcpy(
        snapshot.forgeOdds,
        forgeOdds_,
        sizeof(snapshot.forgeOdds));
    for (std::size_t index = 0;
         index < forgeOddsCount_;
         ++index) {
        snapshot.forgeOddsTotal +=
            forgeOdds_[index].quantity;
        if (forgeOdds_[index].quantity != 0 &&
            !forgeOdds_[index].descriptorLinked) {
            snapshot.forgeOddsFullyLinked = false;
        }
    }
    ReleaseSRWLockExclusive(&lock_);
    return snapshot;
}

} // namespace godswar::network
