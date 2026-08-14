using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetManagerSkillUnlearnHandlerChecks
{
    private static async Task CheckExactNavigationAsync()
    {
        var oneLearnedSkill = CreatePet(
            [
                new PetSkillSnapshot(
                    SkillId: 405,
                    SlotIndex: 0,
                    SkillRank: 1,
                    SkillExperience: 0,
                    IsActive: true,
                    Revision: 7),
                new PetSkillSnapshot(
                    SkillId: 406,
                    SlotIndex: 1,
                    SkillRank: 1,
                    SkillExperience: 0,
                    IsActive: false,
                    Revision: 7)
            ],
            revision: 7);
        await AssertNavigationAsync(
            [oneLearnedSkill],
            [
                PetManagerProtocol.SkillUnlearnPageTitleSubId,
                106
            ],
            "a pet with twelve open cells but one learned skill exposes only that learned skill");

        var sparseSkills = CreatePet([11, 0, 6]);
        await AssertNavigationAsync(
            [sparseSkills],
            [
                PetManagerProtocol.SkillUnlearnPageTitleSubId,
                106,
                114,
                119
            ],
            "sparse learned skills are sorted and mapped across both native sub-ID ranges");

        var noLearnedSkills = CreatePet([]);
        await AssertNavigationAsync(
            [noLearnedSkills],
            [PetManagerProtocol.EmptySkillSlotResultSubId],
            "an active pet with no learned skill receives the bounded native empty result");

        var recalledPet = CreatePet([0]) with
        {
            IsSummoned = false
        };
        await AssertNavigationAsync(
            [recalledPet],
            [PetManagerProtocol.NoSummonedPetResultSubId],
            "a recalled pet receives the bounded native no-summoned-pet result");

        var skillOutsideOpenedBoundary = CreatePet([1]) with
        {
            OpenedSkillSlots = 1,
            AvailableSkillSlots = 1
        };
        await AssertNavigationAsync(
            [skillOutsideOpenedBoundary],
            expectedResponseSubIds: null,
            "a learned skill outside the authoritative opened-cell boundary fails closed");

        var duplicateSlotProjection = CreatePet(
            [
                new PetSkillSnapshot(405, 0, 1, 0, true, 7),
                new PetSkillSnapshot(406, 0, 1, 0, true, 7)
            ],
            revision: 7);
        await AssertNavigationAsync(
            [duplicateSlotProjection],
            expectedResponseSubIds: null,
            "duplicate authoritative learned-skill slots fail closed");
    }

    private static async Task AssertNavigationAsync(
        IReadOnlyList<PetBootstrapSnapshot> pets,
        int[]? expectedResponseSubIds,
        string scope)
    {
        var character = CharacterWithPotion(stack: 3);
        var executor = new DelegatingPetDurableCommandExecutor();
        await using var fixture = PetDurableRawHandlerFixture.Create(
            character,
            character,
            pets,
            executor,
            hasLocalDevelopmentCapability: true);

        await InvokeAsync(
            fixture.Handler,
            DecodeExactRequest(CreateActionPacket(
                PetManagerProtocol.SkillUnlearnMenuSubId)));

        var actual = fixture.ReadLegacyPackets();
        Check.Equal(
            0,
            executor.UnlearnSkillCount,
            $"{scope}: navigation never executes a mutation");
        if (expectedResponseSubIds is null)
        {
            Check.Equal(
                0,
                actual.Count,
                $"{scope}: invalid projection emits no misleading page");
            return;
        }

        Check.True(
            actual is [var page] &&
            page.SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                expectedResponseSubIds)),
            $"{scope}: exact 92-byte choice 6 emits one authoritative page");
    }
}
