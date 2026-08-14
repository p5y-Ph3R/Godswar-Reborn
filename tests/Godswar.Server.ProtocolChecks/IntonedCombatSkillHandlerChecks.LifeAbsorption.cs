using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static async Task CheckLifeAbsorptionCompletionAsync(
        SkillCombatDefinition combat)
    {
        foreach (var (name, mode) in new[]
                 {
                     ("Legacy", PlayerRuntimeMode.Legacy),
                     ("Ecs", PlayerRuntimeMode.Ecs)
                 })
        {
            await using var fixture = await Fixture.CreateAsync(
                $"{name}LifeAbsorptionThunder",
                currentHp: 400,
                lifeAbsorption: 5_000,
                playerRuntimeMode: mode);
            await fixture.BeginCastAsync();

            var damage = await fixture.Socket.ReadPacketAsync(32);
            var impact = await fixture.Socket.ReadPacketAsync(24);
            var mana = await fixture.Socket.ReadPacketAsync(12);
            var vitals = await fixture.Socket.ReadPacketAsync(16);
            Check.True(
                ReadOpcode(damage) == 10045 &&
                ReadOpcode(impact) == 10046 &&
                ReadOpcode(mana) == 10135 &&
                ReadOpcode(vitals) == 0x2771,
                $"{name} skill publishes damage, impact, mana, then life absorption");
            Check.Equal(
                500,
                BinaryPrimitives.ReadInt32LittleEndian(
                    vitals.AsSpan(8, 4)),
                $"{name} skill caps life absorption at missing HP");
            Check.Equal(
                500,
                fixture.Character.CurrentHp,
                $"{name} skill commits life absorption to authoritative HP");
            Check.Equal(
                InitialMana - combat.Mp,
                BinaryPrimitives.ReadInt32LittleEndian(
                    vitals.AsSpan(12, 4)),
                $"{name} life-absorption packet retains committed mana");

            await fixture.Store.WaitForVitalsWriteAsync();
            await Task.Delay(50);
            Check.Equal(
                1,
                fixture.Store.VitalsWrites,
                $"{name} skill persists its combined mana and heal revision once");
            Check.Equal(
                0,
                fixture.Socket.Available,
                $"{name} skill emits no duplicate life-absorption packet");
        }
    }
}
