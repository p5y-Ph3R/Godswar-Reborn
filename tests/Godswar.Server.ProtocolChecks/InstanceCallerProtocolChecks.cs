using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class InstanceCallerProtocolChecks
{
    public const string CheckName =
        "Stock Instance Caller Medusa dialogue protocol";

    public static Task RunAsync()
    {
        Check.Equal(9, InstanceCallerProtocol.DialogIndex,
            "Instance Caller uses loaded NpcFunRepetition dialog 9");
        Check.Equal(92, InstanceCallerProtocol.ActionPacketBytes,
            "Instance Caller uses the canonical NPC action frame");
        Check.True(
            InstanceCallerProtocol.InitialMenuSubIds.SequenceEqual([11]) &&
            InstanceCallerProtocol.MedusaPageSubIds.SequenceEqual(
                [206, 204, 205, 207]) &&
            InstanceCallerProtocol.QueueUnavailableResultSubId == 1000,
            "Medusa root, difficulty page, and queue-failure result are finite");
        Check.True(
            InstanceCallerProtocol.IsEndpoint("Athens_060", 5199) &&
            InstanceCallerProtocol.IsEndpoint("Sparta_060", 5057) &&
            !InstanceCallerProtocol.IsEndpoint("Sparta_060", 5059) &&
            !InstanceCallerProtocol.IsEndpoint("Athens_061", 5199),
            "only published capital Instance Caller endpoints are accepted");

        CheckNavigationAndDifficultyPaths();
        CheckPublishedV9Routes();
        CheckLeaderInstancePanelPackets();
        CheckCompletionPackets();
        return Task.CompletedTask;
    }

    private static void CheckCompletionPackets()
    {
        var reward = PacketBuilder.RepetitionReward(2_250);
        const string message =
            "The team earned the title of 'Medusa Challengers'.";
        var notice = PacketBuilder.ServerNote(message);

        Check.True(
            reward.Length == 104 &&
            BinaryPrimitives.ReadUInt16LittleEndian(reward) == 104 &&
            BinaryPrimitives.ReadUInt16LittleEndian(reward.AsSpan(2)) ==
                Opcodes.RepetitionReward &&
            BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(4)) == 17 &&
            BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(92)) ==
                2_250 &&
            reward.AsSpan(8, 84).IndexOfAnyExcept((byte)0) < 0 &&
            reward.AsSpan(96).IndexOfAnyExcept((byte)0) < 0,
            "native repetition reward carries type 17 and Medusa Honor at offset 92");
        Check.True(
            notice.Length == 260 &&
            BinaryPrimitives.ReadUInt16LittleEndian(notice) == 260 &&
            BinaryPrimitives.ReadUInt16LittleEndian(notice.AsSpan(2)) ==
                Opcodes.ServerNote &&
            notice.AsSpan(4, message.Length).SequenceEqual(
                System.Text.Encoding.ASCII.GetBytes(message)) &&
            notice[4 + message.Length] == 0 &&
            Opcodes.Name(Opcodes.RepetitionReward) ==
                nameof(Opcodes.RepetitionReward) &&
            Opcodes.Name(Opcodes.ServerNote) == nameof(Opcodes.ServerNote),
            "native server-note packet carries the exact faction message");
    }

    private static void CheckLeaderInstancePanelPackets()
    {
        var sync = PacketBuilder.RepetitionSync(
            repetitionId: 209,
            repetitionIndex: 0,
            groupIndex: 0,
            state: 5,
            entryLimit: 1);
        var fight = PacketBuilder.RepetitionFightInfo(
            remainingSeconds: 2_399,
            teamScore: 17);
        Check.True(
            sync.Length == 14 &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync) == 14 &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(2)) ==
                Opcodes.RepetitionSync &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(4)) == 209 &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(6)) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(10)) == 5 &&
            fight.Length == 20 &&
            BinaryPrimitives.ReadUInt16LittleEndian(fight) == 20 &&
            BinaryPrimitives.ReadUInt16LittleEndian(fight.AsSpan(2)) ==
                Opcodes.RepetitionFightInfo &&
            BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(4)) ==
                2_399 &&
            BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(8)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(12)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(16)) == 17,
            "native repetition sync and fight-info frames carry the leader timer state");

        var completion = PacketBuilder.RepetitionCompletionState(
            repetitionId: 209,
            completed: true);
        var countdown = PacketBuilder.RepetitionCountdown(30);
        Check.True(
            completion.Length == 12 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                completion.AsSpan(2)) ==
                Opcodes.RepetitionCompletionState &&
            BinaryPrimitives.ReadInt32LittleEndian(
                completion.AsSpan(4)) == 209 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                completion.AsSpan(8)) == 1 &&
            countdown.Length == 8 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                countdown.AsSpan(2)) == Opcodes.RepetitionReset &&
            BinaryPrimitives.ReadInt32LittleEndian(
                countdown.AsSpan(4)) == 30,
            "captured completion state opens the 30-second terminate dialog");

        var roster = PacketBuilder.RepetitionInstanceMembers(
        [
            new(202, "-Alboz-", 139, true, 1),
            new(203, "Perseus", 90, false, 3)
        ]);
        Check.True(
            roster.Length == 96 &&
            BinaryPrimitives.ReadUInt16LittleEndian(roster.AsSpan(2)) ==
                Opcodes.RepetitionInstanceMembers &&
            BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(4)) == 2 &&
            BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(8)) == 202 &&
            BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(44)) == 139 &&
            roster[48] == 1 &&
            roster[49] == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(52)) == 203 &&
            roster[92] == 0 &&
            roster[93] == 3,
            "captured instance roster frame carries exact online and profession fields");
    }

    private static void CheckNavigationAndDifficultyPaths()
    {
        Check.True(
            InstanceCallerProtocol.TryGetMedusaPage(
                9,
                11,
                Arguments(),
                out var page) &&
            page.SequenceEqual([206, 204, 205, 207]),
            "root action 11 opens the Medusa difficulty page");
        Check.True(
            InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                Arguments(204),
                out var advanced) &&
            advanced == InstanceCallerDifficulty.Advanced &&
            InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                Arguments(205),
                out var normal) &&
            normal == InstanceCallerDifficulty.Normal &&
            InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                Arguments(207),
                out var mythic) &&
            mythic == InstanceCallerDifficulty.Mythic,
            "nested client paths select Advanced, Normal, and Mythic");

        var polluted = Arguments(204);
        polluted[1] = 0;
        Check.True(
            !InstanceCallerProtocol.TryGetMedusaPage(
                10,
                11,
                Arguments(),
                out _) &&
            !InstanceCallerProtocol.TryResolveDifficulty(
                9,
                204,
                Arguments(),
                out _) &&
            !InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                polluted,
                out _) &&
            !InstanceCallerProtocol.TryResolveDifficulty(
                9,
                11,
                Arguments()[..^1],
                out _),
            "wrong dialog, loose sub-id, polluted paths, and short frames fail");
    }

    private static void CheckPublishedV9Routes()
    {
        var routes = NpcDialogueBaselineV9.CreateRoutes();
        var npcs = NpcContentBaselineV1.LoadDefinitions()
            .Where(npc => npc.NpcKey is "Athens_060" or "Sparta_060")
            .OrderBy(npc => npc.NpcKey, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            NpcDialogueBaselineV9.ExpectedProfileCount == 11 &&
            NpcDialogueBaselineV9.ExpectedRouteCount == 22 &&
            NpcDialogueBaselineV9.ExpectedMenuEntryCount == 54 &&
            npcs.Length == 2,
            "V9 extends V8 with one profile and two published routes");
        foreach (var npc in npcs)
        {
            var route = routes.Single(candidate =>
                candidate.NpcKey == npc.NpcKey);
            Check.True(
                route.Behavior == NpcDialogueBehavior.InstanceCaller &&
                route.DialogIndex == InstanceCallerProtocol.DialogIndex &&
                route.InitialMenuSubIds.SequenceEqual([11]) &&
                NpcDialogueBehaviorRegistry.IsAllowed(npc, route),
                $"{npc.NpcKey} has the allowlisted Instance Caller route");
        }
    }

    private static int[] Arguments(params int[] path)
    {
        var arguments = Enumerable.Repeat(
            -1,
            InstanceCallerProtocol.FunctionArgumentCount).ToArray();
        path.CopyTo(arguments, 0);
        return arguments;
    }
}
