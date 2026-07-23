namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public async Task<GameCharacter?> MoveEquipmentToKitBagAsync(
        int accountId,
        int characterId,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return null;
            }

            var equipmentEntry = EquipmentSlots.GetEntry(character.Equipment, character.Profession, equipmentSlot);
            if (equipmentEntry == "[]"
                || kitBagSlot is < 0 or >= 96
                || !KitBagSlots.GetItem(character.KitBag, kitBagSlot).IsEmpty)
            {
                return Clone(character);
            }

            var unequipEligibility = EquipmentEligibility.ValidateUnequip(
                character.Profession,
                character.Equipment,
                equipmentSlot);
            if (!unequipEligibility.Allowed)
            {
                return Clone(character);
            }

            character.Equipment = EquipmentSlots.ClearSlot(character.Equipment, character.Profession, equipmentSlot);
            character.KitBag = KitBagSlots.SetSlot(character.KitBag, kitBagSlot, equipmentEntry);

            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> MoveKitBagToEquipmentAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        int requestedEquipmentSlot,
        CancellationToken cancellationToken = default,
        bool requireEmptyEquipmentSlot = false)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return null;
            }

            var kitBagEntry = KitBagSlots.GetEntry(character.KitBag, kitBagSlot);
            var item = CompactItemEntry.Parse(kitBagEntry);
            if (item.IsEmpty
                || kitBagEntry == "[]"
                || !EquipmentSlots.TryGetAuthoritativeSlot(item.Id, out var defaultEquipmentSlot))
            {
                return null;
            }

            var equipmentSlot = EquipmentSlots.ResolveSlotForItem(
                item.Id,
                requestedEquipmentSlot,
                character.Equipment,
                character.Profession,
                defaultEquipmentSlot);
            if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot))
            {
                return null;
            }

            var equipEligibility = EquipmentEligibility.ValidateEquip(
                character.Profession,
                character.Level,
                character.Equipment,
                item.Id,
                equipmentSlot);
            if (!equipEligibility.Allowed)
            {
                return Clone(character);
            }

            var previousEquipmentEntry = EquipmentSlots.GetEntry(character.Equipment, character.Profession, equipmentSlot);
            if (requireEmptyEquipmentSlot && previousEquipmentEntry != "[]")
            {
                return Clone(character);
            }

            character.Equipment = EquipmentSlots.SetSlot(character.Equipment, character.Profession, equipmentSlot, kitBagEntry);
            character.KitBag = previousEquipmentEntry == "[]"
                ? KitBagSlots.ClearSlot(character.KitBag, kitBagSlot)
                : KitBagSlots.SetSlot(character.KitBag, kitBagSlot, previousEquipmentEntry);

            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> MoveKitBagItemAsync(
        int accountId,
        int characterId,
        int sourceSlot,
        int destinationSlot,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return null;
            }

            if (sourceSlot != destinationSlot)
            {
                var sourceEntry = KitBagSlots.GetEntry(character.KitBag, sourceSlot);
                if (sourceEntry != "[]")
                {
                    var destinationEntry = KitBagSlots.GetEntry(character.KitBag, destinationSlot);
                    var updatedKitBag = KitBagSlots.SetSlot(character.KitBag, destinationSlot, sourceEntry);
                    character.KitBag = destinationEntry == "[]"
                        ? KitBagSlots.ClearSlot(updatedKitBag, sourceSlot)
                        : KitBagSlots.SetSlot(updatedKitBag, sourceSlot, destinationEntry);
                }
            }

            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> DeleteKitBagItemAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return null;
            }

            character.KitBag = KitBagSlots.ClearSlot(character.KitBag, kitBagSlot);
            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> ClearKitBagAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.Id == characterId);
            if (character is null)
            {
                return null;
            }

            character.KitBag = GameDefaults.EmptyKitBag;
            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KitBagItemGrantResult> AddForgingMaterialAsync(
        int accountId,
        int characterId,
        uint itemId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        if (!DeveloperGrantMaterialCatalog.TryResolve(itemId, out var material))
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemId),
                "Item is not in the developer material allowlist.");
        }

        if (quantity is < 1 or > KitBagItemGrantPlanner.MaximumQuantity)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return new KitBagItemGrantResult(KitBagItemGrantStatus.CharacterNotFound, null);
            }

            if (!KitBagItemGrantPlanner.TryAdd(
                    character.KitBag,
                    itemId,
                    quantity,
                    material.StackCap,
                    material.GrantedBound,
                    out var updatedKitBag))
            {
                return new KitBagItemGrantResult(
                    KitBagItemGrantStatus.InsufficientCapacity,
                    Clone(character));
            }

            character.KitBag = updatedKitBag;
            await SaveUnsafeAsync(db, cancellationToken);
            return new KitBagItemGrantResult(KitBagItemGrantStatus.Added, Clone(character));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KitBagItemGrantResult> AddDeveloperMountAsync(
        int accountId,
        int characterId,
        uint itemId,
        CancellationToken cancellationToken = default)
    {
        if (!DeveloperMountCatalog.TryResolveGrantable(itemId, out _))
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemId),
                "Item is not in the developer mount allowlist.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.Id == characterId);
            if (character is null)
            {
                return new KitBagItemGrantResult(
                    KitBagItemGrantStatus.CharacterNotFound,
                    null);
            }

            if (!KitBagItemGrantPlanner.TryAdd(
                    character.KitBag,
                    itemId,
                    quantity: 1,
                    stackCap: 1,
                    bound: 1,
                    out var updatedKitBag))
            {
                return new KitBagItemGrantResult(
                    KitBagItemGrantStatus.InsufficientCapacity,
                    Clone(character));
            }

            character.KitBag = updatedKitBag;
            await SaveUnsafeAsync(db, cancellationToken);
            return new KitBagItemGrantResult(
                KitBagItemGrantStatus.Added,
                Clone(character));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ForgeTransactionResult> ForgeEquipmentAsync(
        int accountId,
        int characterId,
        ForgeTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c =>
                c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return new ForgeTransactionResult(
                    ForgeTransactionStatus.CharacterNotFound,
                    null,
                    0,
                    0,
                    0,
                    CompactItemEntry.Empty,
                    CompactItemEntry.Empty,
                    "Character was not found.");
            }

            var equipmentBefore = request is not null &&
                                  request.Equipment.KitBagSlot is >= 0 and < 96
                ? KitBagSlots.GetItem(character.KitBag, request.Equipment.KitBagSlot)
                : CompactItemEntry.Empty;
            if (!ForgePersistencePlanner.TryCreate(
                    character.KitBag,
                    character.Silver,
                    request,
                    System.Security.Cryptography.RandomNumberGenerator.GetInt32(100),
                    out var plan,
                    out var rejectionStatus,
                    out var rejectionReason))
            {
                return new ForgeTransactionResult(
                    rejectionStatus,
                    Clone(character),
                    0,
                    0,
                    0,
                    equipmentBefore,
                    equipmentBefore,
                    rejectionReason);
            }

            character.KitBag = plan!.UpdatedKitBag;
            character.Silver = plan.UpdatedSilver;
            await SaveUnsafeAsync(db, cancellationToken);

            return new ForgeTransactionResult(
                plan.Succeeded
                    ? ForgeTransactionStatus.Succeeded
                    : ForgeTransactionStatus.FailedRoll,
                Clone(character),
                (int)plan.Calculation.Operation,
                plan.Calculation.SuccessProbability,
                plan.Calculation.SilverCost,
                equipmentBefore,
                plan.Succeeded
                    ? plan.Calculation.SuccessEquipment
                    : plan.Calculation.FailureEquipment);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GearEnhancementTransactionResult> EnhanceGearAsync(
        int accountId,
        int characterId,
        GearEnhancementRequest request,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c =>
                c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return new GearEnhancementTransactionResult(null, null);
            }

            var enhancement = GearEnhancementPlanner.Create(character.KitBag, request);
            if (!enhancement.Committed)
            {
                return new GearEnhancementTransactionResult(
                    enhancement,
                    Clone(character));
            }

            character.KitBag = enhancement.UpdatedKitBag;
            await SaveUnsafeAsync(db, cancellationToken);

            return new GearEnhancementTransactionResult(
                enhancement,
                Clone(character));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GearMentorTransactionResult> ProcessGearMentorAsync(
        int accountId,
        int characterId,
        GearMentorRequest request,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c =>
                c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return new GearMentorTransactionResult(null, null);
            }

            var result = GearMentorPlanner.Create(
                character.KitBag,
                character.Level,
                request);
            if (!result.Committed)
            {
                return new GearMentorTransactionResult(result, Clone(character));
            }

            character.KitBag = result.UpdatedKitBag;
            await SaveUnsafeAsync(db, cancellationToken);
            return new GearMentorTransactionResult(result, Clone(character));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> ApplyWeaponHolyStoneAsync(
        int accountId,
        int characterId,
        HolyStoneOperation operation,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return null;
            }

            if (!HolyStoneItemMutator.TryApply(
                    character.Equipment,
                    character.KitBag,
                    character.Profession,
                    operation,
                    targetKitBagSlot,
                    socketIndex,
                    stoneKitBagSlot,
                    destinationKitBagSlot,
                    out var equipment,
                    out var kitBag,
                    out _))
            {
                return null;
            }

            character.Equipment = equipment;
            character.KitBag = kitBag;
            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

}
