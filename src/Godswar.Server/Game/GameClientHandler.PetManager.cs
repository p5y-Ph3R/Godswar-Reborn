using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandlePetManagerAsync(
        GamePacket packet,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        var clientOperationId = packet.ClientOperationId;
        var exactFrame = IsExactPetManagerFrame(
            packet,
            npcId,
            dialogIndex,
            subId);
        var skillSlot = -1;
        var isSkillUnlearnMutation =
            dialogIndex == PetManagerProtocol.DialogIndex &&
            PetManagerProtocol.TryResolveSkillUnlearnMutation(
                subId,
                arguments,
                out skillSlot);
        var growthResetOperation = default(PetGrowthResetRequestOperation);
        var isGrowthResetMutation =
            PetManagerProtocol.TryResolveGrowthResetMutation(
                dialogIndex,
                subId,
                arguments,
                out growthResetOperation);
        var basicSavvyResetOperation =
            default(PetBasicSavvyResetRequestOperation);
        var isBasicSavvyResetMutation =
            PetManagerProtocol.TryResolveBasicSavvyResetMutation(
                dialogIndex,
                subId,
                arguments,
                out basicSavvyResetOperation);
        var appearanceBagSlot = -1;
        var isAppearanceChangeMutation =
            PetManagerProtocol.TryResolveAppearanceChangeMutation(
                dialogIndex,
                subId,
                arguments,
                out appearanceBagSlot);
        var isPetBindMutation =
            PetManagerProtocol.TryResolvePetBindMutation(
                dialogIndex,
                subId,
                arguments);
        var utilityRequestOperation =
            default(PetManagerUtilityRequestOperation);
        var isUtilityMutation =
            PetManagerProtocol.TryResolveUtilityMutation(
                dialogIndex,
                subId,
                arguments,
                out utilityRequestOperation);
        var isGenderPreview =
            PetManagerProtocol.IsGenderPreviewRequest(
                dialogIndex,
                subId,
                arguments);
        if (!clientOperationId.HasValue &&
            exactFrame &&
            (PetManagerProtocol.IsExactNavigationArguments(arguments) ||
             (dialogIndex == PetManagerProtocol.DialogIndex &&
              subId == PetManagerProtocol.SkillUnlearnMenuSubId &&
              !isSkillUnlearnMutation)))
        {
            int[]? responseSubIds = null;
            if (dialogIndex == PetManagerProtocol.DialogIndex &&
                subId == PetManagerProtocol.SkillUnlearnMenuSubId)
            {
                var menu = await ResolvePetSkillUnlearnMenuAsync(
                    cancellationToken);
                responseSubIds = menu.Status switch
                {
                    PetSkillMenuStatus.Available => menu.ResponseSubIds,
                    PetSkillMenuStatus.NoActivePet =>
                        [PetManagerProtocol.NoSummonedPetResultSubId],
                    PetSkillMenuStatus.NoLearnedSkill =>
                        [PetManagerProtocol.EmptySkillSlotResultSubId],
                    _ => null
                };
                if (responseSubIds is null)
                {
                    Console.Error.WriteLine(
                        "[pet-manager] skill menu suppressed " +
                        "reason=invalid_authoritative_projection");
                    return;
                }
            }
            else if (PetManagerProtocol.TryGetInformationPage(
                         dialogIndex,
                         subId,
                         out var informationPage))
            {
                responseSubIds = informationPage;
            }

            if (responseSubIds is not null)
            {
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        responseSubIds),
                    cancellationToken,
                    "PetManagerInformationPage");
                return;
            }
        }

        if (isGenderPreview && !clientOperationId.HasValue)
        {
            if (!exactFrame)
            {
                Console.WriteLine(
                    "[pet-manager] rejected malformed gender preview " +
                    $"npc={npcId} subId={subId} args={arguments.Count}");
                return;
            }
            var resultSubId = await ResolveGenderPreviewResultAsync(
                cancellationToken);
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    resultSubId),
                cancellationToken,
                "PetManagerGenderPreview");
            return;
        }

        if (isUtilityMutation)
        {
            if (!exactFrame)
            {
                Console.WriteLine(
                    "[pet-manager] rejected malformed utility action " +
                    $"npc={npcId} subId={subId} args={arguments.Count}");
                return;
            }

            PetCommandOperationIdentity identity;
            if (clientOperationId is { } operationId)
            {
                identity = PetCommandOperationIdentity.SecureClient(
                    operationId);
            }
            else
            {
                if (!AllowLegacyPlayerMutationFallback(
                        "pet_manager_utility"))
                {
                    return;
                }
                identity = PetCommandOperationIdentity.RawLocalServer(
                    Guid.NewGuid(),
                    _commandConnectionId);
            }

            var receipt = await HandleDurablePetManagerUtilityAsync(
                identity,
                ToDurableUtilityOperation(utilityRequestOperation),
                kitBagSlot: -1,
                cancellationToken);
            if (receipt is null)
            {
                return;
            }

            var responseSubIds = receipt.Status ==
                    PetDurableReceiptStatus.PetGrowthChecked &&
                receipt.PetManagerUtility is
                    { Growth: { IsValid: true } growth }
                ? PetManagerProtocol.BuildGrowthCheckSuccessPage(
                    receipt.PetId,
                    growth.Values)
                : [checked((int)ResolvePetLegacyResultCode(receipt))];
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    responseSubIds),
                cancellationToken,
                "PetManagerUtilityResult");
            return;
        }

        if (isSkillUnlearnMutation)
        {
            if (!exactFrame)
            {
                Console.WriteLine(
                    "[pet-manager] rejected malformed skill-unlearn " +
                    $"npc={npcId} subId={subId} args={arguments.Count}");
                return;
            }

            PetCommandOperationIdentity identity;
            if (clientOperationId is { } operationId)
            {
                identity = PetCommandOperationIdentity.SecureClient(
                    operationId);
            }
            else
            {
                if (!AllowLegacyPlayerMutationFallback(
                        "pet_skill_unlearn"))
                {
                    return;
                }

                identity = PetCommandOperationIdentity.RawLocalServer(
                    Guid.NewGuid(),
                    _commandConnectionId);
            }

            var receipt = await HandleDurablePetSkillUnlearnAsync(
                identity,
                skillSlot,
                cancellationToken);
            if (receipt is null)
            {
                return;
            }

            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    checked((int)ResolvePetLegacyResultCode(receipt))),
                cancellationToken,
                "PetManagerSkillUnlearnResult");
            return;
        }

        if (isAppearanceChangeMutation)
        {
            if (!exactFrame)
            {
                Console.WriteLine(
                    "[pet-manager] rejected malformed appearance change " +
                    $"npc={npcId} subId={subId} args={arguments.Count}");
                return;
            }

            PetCommandOperationIdentity identity;
            if (clientOperationId is { } operationId)
            {
                identity = PetCommandOperationIdentity.SecureClient(
                    operationId);
            }
            else
            {
                if (!AllowLegacyPlayerMutationFallback(
                        "pet_appearance_change"))
                {
                    return;
                }
                identity = PetCommandOperationIdentity.RawLocalServer(
                    Guid.NewGuid(),
                    _commandConnectionId);
            }

            var receipt = await HandleDurablePetAppearanceChangeAsync(
                identity,
                appearanceBagSlot,
                cancellationToken);
            if (receipt is null)
            {
                return;
            }
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    checked((int)ResolvePetLegacyResultCode(receipt))),
                cancellationToken,
                "PetManagerAppearanceChangeResult");
            return;
        }

        if (isPetBindMutation)
        {
            if (!exactFrame)
            {
                Console.WriteLine(
                    "[pet-manager] rejected malformed pet bind " +
                    $"npc={npcId} subId={subId} args={arguments.Count}");
                return;
            }

            PetCommandOperationIdentity identity;
            if (clientOperationId is { } operationId)
            {
                identity = PetCommandOperationIdentity.SecureClient(
                    operationId);
            }
            else
            {
                if (!AllowLegacyPlayerMutationFallback("pet_bind"))
                {
                    return;
                }
                identity = PetCommandOperationIdentity.RawLocalServer(
                    Guid.NewGuid(),
                    _commandConnectionId);
            }

            var receipt = await HandleDurablePetBindAsync(
                identity,
                cancellationToken);
            if (receipt is null)
            {
                return;
            }
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    checked((int)ResolvePetLegacyResultCode(receipt))),
                cancellationToken,
                "PetManagerBindResult");
            return;
        }

        if (isBasicSavvyResetMutation)
        {
            if (!exactFrame)
            {
                Console.WriteLine(
                    "[pet-manager] rejected malformed Basic-Savvy reset " +
                    $"npc={npcId} subId={subId} args={arguments.Count}");
                return;
            }

            PetCommandOperationIdentity identity;
            if (clientOperationId is { } operationId)
            {
                identity = PetCommandOperationIdentity.SecureClient(
                    operationId);
            }
            else
            {
                if (!AllowLegacyPlayerMutationFallback(
                        "pet_basic_savvy_reset"))
                {
                    return;
                }

                identity = PetCommandOperationIdentity.RawLocalServer(
                    Guid.NewGuid(),
                    _commandConnectionId);
            }

            var operation = basicSavvyResetOperation ==
                    PetBasicSavvyResetRequestOperation.Preview
                ? PetBasicSavvyResetOperation.Preview
                : PetBasicSavvyResetOperation.Accept;
            var receipt = await HandleDurablePetBasicSavvyResetAsync(
                identity,
                operation,
                Guid.Empty,
                cancellationToken);
            if (receipt is null)
            {
                return;
            }

            var responseSubIds =
                TryBuildPetBasicSavvyResetResultSubIds(
                    receipt,
                    out var basicSavvyResult)
                ? basicSavvyResult
                : throw new InvalidDataException(
                    "The committed Basic-Savvy reset has no client projection.");
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    responseSubIds),
                cancellationToken,
                "PetManagerBasicSavvyResetResult");
            return;
        }

        if (isGrowthResetMutation)
        {
            if (!exactFrame)
            {
                Console.WriteLine(
                    "[pet-manager] rejected malformed growth-reset " +
                    $"npc={npcId} subId={subId} args={arguments.Count}");
                return;
            }

            PetCommandOperationIdentity identity;
            if (clientOperationId is { } operationId)
            {
                identity = PetCommandOperationIdentity.SecureClient(
                    operationId);
            }
            else
            {
                if (!AllowLegacyPlayerMutationFallback(
                        "pet_growth_reset"))
                {
                    return;
                }

                identity = PetCommandOperationIdentity.RawLocalServer(
                    Guid.NewGuid(),
                    _commandConnectionId);
            }

            var operation = growthResetOperation ==
                    PetGrowthResetRequestOperation.Preview
                ? PetGrowthResetOperation.Preview
                : PetGrowthResetOperation.Accept;
            var previewOperationId = operation ==
                    PetGrowthResetOperation.Accept
                ? BindPetGrowthAcceptOperation(identity.OperationId)
                : Guid.Empty;
            var receipt = await HandleDurablePetGrowthResetAsync(
                identity,
                operation,
                previewOperationId,
                cancellationToken);
            if (receipt is null)
            {
                return;
            }

            if (receipt.Status == PetDurableReceiptStatus.PetGrowthPreviewed &&
                !await TryActivatePetGrowthPreviewAsync(
                    receipt,
                    cancellationToken))
            {
                // A delayed replay of Preview 1 must not overwrite the page
                // for a newer durable Preview 2.
                return;
            }
            if (operation == PetGrowthResetOperation.Accept &&
                _activePetGrowthPreviewOperationId == previewOperationId)
            {
                _activePetGrowthPreviewOperationId = Guid.Empty;
            }
            if (receipt.Status == PetDurableReceiptStatus.PetGrowthAccepted)
            {
                // Native A1 already closes the page. Opcode 10286, sent by
                // the durable projection, is the authoritative commit UI.
                return;
            }

            if (!TryBuildPetGrowthResetResultSubIds(
                    receipt,
                    out var responseSubIds))
            {
                if (receipt.Status ==
                    PetDurableReceiptStatus.PetGrowthPreviewed)
                {
                    // The preview receipt can be a valid replay while its
                    // pet revision is no longer current. Never render a
                    // comparison assembled from two different revisions.
                    return;
                }
                throw new InvalidDataException(
                    "The committed Growth reset has no client projection.");
            }
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    responseSubIds),
                cancellationToken,
                "PetManagerGrowthResetResult");
            return;
        }

        // Unknown point-reset and modal forms remain capture-gated. Never
        // reinterpret a nearby request as a valuable utility mutation.
        PetManagerRejectedShapeDiagnostic.TryCapture(
            _session.IsSecure,
            _legacyAuthenticationAccess is not null,
            exactFrame,
            npcId,
            dialogIndex,
            subId,
            arguments);
        Console.WriteLine(
            "[pet-manager] capture-gated action preserved " +
            $"npc={npcId} dialog={dialogIndex} subId={subId} " +
            $"args={arguments.Count} secure={clientOperationId.HasValue}");
    }

    private static bool IsExactPetManagerFrame(
        GamePacket packet,
        uint npcId,
        int dialogIndex,
        int subId)
    {
        var bytes = packet.Buffer.AsSpan();
        return packet.Length == 92 &&
            bytes.Length == 92 &&
            packet.Opcode == Opcodes.NpcFunctionAction &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) ==
                npcId &&
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4)) ==
                dialogIndex &&
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12, 4)) ==
                dialogIndex &&
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16, 4)) ==
                subId;
    }
}
