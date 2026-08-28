using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static async Task CheckInsufficientManaRejectionsAsync(
        SkillCombatDefinition combat)
    {
        await CheckInsufficientManaBeforeIntonationAsync(combat);
        await CheckInsufficientManaAtCompletionAsync(combat);
    }

    private static async Task CheckInsufficientManaBeforeIntonationAsync(
        SkillCombatDefinition combat)
    {
        await using var fixture = await Fixture.CreateAsync(
            "InsufficientManaBeforeThunder");
        fixture.Character.CurrentMp = Math.Max(0, combat.Mp - 1);

        await InvokePacketAsync(
            fixture.Handler,
            CreateSkillCastPacket(
                fixture.Character.PositionX,
                fixture.Character.PositionZ));
        await AssertInsufficientManaSequenceAsync(
            fixture,
            fixture.Character.CurrentMp,
            "pre-intonation MP rejection");
    }

    private static async Task CheckInsufficientManaAtCompletionAsync(
        SkillCombatDefinition combat)
    {
        await using var fixture = await Fixture.CreateAsync(
            "InsufficientManaAtThunderCompletion");
        await fixture.BeginCastAsync();
        lock (fixture.Character.VitalsSync)
        {
            fixture.Character.CurrentMp = 0;
            fixture.Character.MarkVitalsChanged();
        }

        await AssertInsufficientManaSequenceAsync(
            fixture,
            expectedMana: 0,
            "completion-time MP rejection");
    }

    private static async Task AssertInsufficientManaSequenceAsync(
        Fixture fixture,
        int expectedMana,
        string description)
    {
        var notice = await fixture.Socket.ReadPacketAsync(12);
        var mana = await fixture.Socket.ReadPacketAsync(12);
        var interruption = await fixture.Socket.ReadPacketAsync(8);

        Check.True(
            notice.SequenceEqual(PacketBuilder.LocalizedError(
                NativeErrorCodes.InsufficientMana)),
            $"{description} reaches the native left log");
        Check.Equal(
            expectedMana,
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                mana.AsSpan(8, 4)),
            $"{description} publishes authoritative MP");
        Check.True(
            interruption.SequenceEqual(
                PacketBuilder.SkillCastInterrupt(LocalObjectId)),
            $"{description} clears the client casting state");
        Check.Equal(
            InitialMonsterHealth,
            fixture.CurrentMonsterHealth(),
            $"{description} applies no monster damage");
    }
}
