using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleBreakItemAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine(
                "[equip-re] BreakItem ignored: no active character");
            return;
        }

        if (!TryReadBreakItemEquip(
                packet.Payload,
                out var sourceSlot))
        {
            Console.WriteLine(
                "[equip-re] BreakItem ignored: payload does not " +
                "contain a valid bag page/index");
            return;
        }

        var itemId = KitBagSlots.GetItemId(
            _character.KitBag,
            sourceSlot);

        if (packet.ClientOperationId is { } operationId)
        {
            if (itemId == PetItemCatalog.PackedSealJade)
            {
                await HandleDurablePetManagerUtilityAsync(
                    PetCommandOperationIdentity.SecureClient(operationId),
                    PetManagerUtilityOperation.Unseal,
                    sourceSlot,
                    cancellationToken);
                return;
            }
            await HandleDurableBagItemActivationAsync(
                PetCommandOperationIdentity.SecureClient(operationId),
                sourceSlot,
                cancellationToken);
            return;
        }

        var isPetEgg =
            RequirePetContent().TryGetSpeciesByEggItemId(itemId, out _);
        var isPetShedExpansion = itemId == PetItemCatalog.SpecialPetShed;
        var isPetSkillCellItem = itemId is
            PetItemCatalog.PetEnhanceSpring or
            PetItemCatalog.GoldenAppleJuice;
        var isPetExperienceItem =
            PetExperienceItemPolicy.IsMorningDew(itemId);
        var isReviewedPetSkillBook =
            PetSkillBookActivationPolicy.IsReviewedItem(itemId);
        var isEquipment =
            EquipmentSlots.TryGetAuthoritativeSlot(
                RequireItemContent().Templates,
                itemId,
                out var authoritativeEquipmentSlot);

        if (_session.IsSecure)
        {
            // Opcode 10051 is shared by right-click equipment activation and
            // pet-egg hatching. The secure shim identifies the operation as
            // a generic bag-item activation; the server then classifies the
            // locked authoritative item. A tokenless secure request is an
            // identity downgrade and must never reach compatibility logic.
            // Validated local raw traffic may still use the equipment-only
            // compatibility path below while the legacy transport remains.
            Console.WriteLine(
                "[equip-re] BreakItem ignored: operation identity is " +
                "ambiguous between equipment activation and pet hatch");
            if (isEquipment)
            {
                await SendEquipRejectionRefreshAsync(
                    requestedEquipmentSlot: -1,
                    resolvedEquipmentSlot:
                        authoritativeEquipmentSlot,
                    bagSlot: sourceSlot,
                    cancellationToken);
            }
            else
            {
                await SendKitBagRefreshAsync(cancellationToken);
            }

            return;
        }

        var isPackedSealJade = itemId == PetItemCatalog.PackedSealJade;
        if (isPetEgg || isPetShedExpansion || isPetSkillCellItem ||
            isPetExperienceItem || isReviewedPetSkillBook ||
            isPackedSealJade)
        {
                if (!AllowLegacyPlayerMutationFallback(
                    isPackedSealJade
                        ? "pet_unseal"
                        : isPetEgg
                        ? "pet_egg_hatch"
                        : isPetShedExpansion
                            ? "pet_shed_expand"
                            : isPetSkillCellItem
                                ? "pet_skill_cell_advance"
                                : isPetExperienceItem
                                    ? "pet_experience_item"
                                    : "pet_skill_book_learn"))
            {
                return;
            }

            var identity = PetCommandOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
            if (isPackedSealJade)
            {
                await HandleDurablePetManagerUtilityAsync(
                    identity,
                    PetManagerUtilityOperation.Unseal,
                    sourceSlot,
                    cancellationToken);
            }
            else
            {
                await HandleDurableBagItemActivationAsync(
                    identity,
                    sourceSlot,
                    cancellationToken);
            }
            return;
        }

        if (!isEquipment)
        {
            Console.WriteLine(
                $"[equip-re] BreakItem ignored: sourceSlot={sourceSlot} " +
                $"item={itemId} is not genuine equipment");
            return;
        }

        await HandleEquipItemAsync(
            sourceSlot,
            requestedEquipmentSlot: -1,
            itemIdHint: 0,
            cancellationToken);
    }
}
