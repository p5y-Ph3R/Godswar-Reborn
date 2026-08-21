using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static async Task CheckZodiacProjectedIntonationAsync(
        SkillCombatDefinition authored)
    {
        await CheckProjectedManaPrecheckAsync(authored);
        await CheckProjectedCompletionAsync(
            authored,
            PlayerRuntimeMode.Ecs);
        await CheckProjectedCompletionAsync(
            authored,
            PlayerRuntimeMode.Legacy);
        await CheckPinnedProjectedPriestAreaHealAsync();
    }

    private static async Task CheckProjectedManaPrecheckAsync(
        SkillCombatDefinition authored)
    {
        await using var fixture =
            await Fixture.CreateAsync("ZodiacThunderPrecheck");
        SelectThunderImprovement(fixture.Character);
        var projected = ZodiacOffensiveSkillProjection.Resolve(
            fixture.Character,
            authored);
        fixture.Character.CurrentMp = authored.Mp;

        await InvokePacketAsync(
            fixture.Handler,
            CreateSkillCastPacket(
                fixture.Character.PositionX,
                fixture.Character.PositionZ));
        var mana = await fixture.Socket.ReadPacketAsync(12);
        Check.True(
            projected.Applied &&
            projected.Skill.Mp > authored.Mp &&
            ReadOpcode(mana) == 10135 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                mana.AsSpan(8, 4)) == authored.Mp &&
            fixture.Character.CurrentMp == authored.Mp &&
            fixture.CurrentMonsterHealth() == InitialMonsterHealth,
            "intoned precheck requires projected Zodiac MP without spending base MP");
        await Task.Delay(50);
        Check.Equal(
            0,
            fixture.Socket.Available,
            "rejected projected intonation starts no pending cast");
    }

    private static async Task CheckProjectedCompletionAsync(
        SkillCombatDefinition authored,
        PlayerRuntimeMode runtimeMode)
    {
        await using var fixture = await Fixture.CreateAsync(
            $"ZodiacThunder{runtimeMode}",
            playerRuntimeMode: runtimeMode);
        SelectThunderImprovement(fixture.Character);
        var projected = ZodiacOffensiveSkillProjection.Resolve(
            fixture.Character,
            authored);

        await fixture.BeginCastAsync();
        await fixture.AssertStartOnlyAsync(
            projected.Skill,
            "Zodiac-improved Thunder");
        lock (fixture.Character.ZodiacSync)
        {
            fixture.Character.ZodiacSkillGridSkillIds[4] =
                ZodiacSkillGridCatalog.NoSelectedSkill;
        }
        _ = await fixture.Socket.ReadPacketAsync(32);
        _ = await fixture.Socket.ReadPacketAsync(24);
        var mana = await fixture.Socket.ReadPacketAsync(12);
        Check.True(
            projected.Applied &&
            projected.Skill.Power1 == authored.Power1 + 0.02m &&
            projected.Skill.Mp == authored.Mp +
                ZodiacOffensiveSkillProjection
                    .ResolveRoundedUpAdditionalMana(authored.Mp, 5) &&
            BinaryPrimitives.ReadInt32LittleEndian(
                mana.AsSpan(8, 4)) ==
                InitialMana - projected.Skill.Mp &&
            fixture.Character.CurrentMp ==
                InitialMana - projected.Skill.Mp &&
            fixture.Character.ZodiacSkillGridSkillIds[4] ==
                ZodiacSkillGridCatalog.NoSelectedSkill &&
            fixture.CurrentMonsterHealth() < InitialMonsterHealth,
            $"{runtimeMode} intoned completion pins its projected Zodiac power and MP despite a mid-cast deselection");
        await fixture.Store.WaitForVitalsWriteAsync();
    }

    private static void SelectThunderImprovement(GameCharacter character)
    {
        character.ZodiacSkillGridLevels[4] = 1;
        character.ZodiacSkillGridSkillIds[4] = 20_053;
    }
}
