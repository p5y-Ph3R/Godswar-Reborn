using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task
        CheckCompletionCountdownAndLeaderTerminateAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            level: 90,
            transitionReady: true);
        await OpenMedusaPageAsync(fixture);
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));
        await InvokeAsync(
            fixture.Handler,
            CreateControlPacket(Opcodes.ClientReady));
        await InvokeAsync(
            fixture.Handler,
            CreatePlayerDetailRequest());
        fixture.Registry.RegisterInstanceTransitionSink(
            fixture.Session,
            (command, cancellationToken) => InvokePartyTransitionAsync(
                fixture.Handler,
                command,
                cancellationToken));

        var completionAt = DateTimeOffset.UtcNow.AddSeconds(1);
        var stheno = DefeatCompletionBoss(
            fixture,
            "Stheno",
            CombatDamageChannel.Physical,
            eventId: 1,
            completionAt.AddMilliseconds(-1));
        var medusa = DefeatCompletionBoss(
            fixture,
            "Medusa",
            CombatDamageChannel.Magic,
            eventId: 2,
            completionAt);
        Check.True(
            stheno.Defeat?.Claim?.Outcome ==
                MedusaDefeatClaimOutcome.Applied &&
            medusa.Defeat?.Claim?.Outcome ==
                MedusaDefeatClaimOutcome.Completed,
            "Stheno then Medusa completes the active run while other hostiles may remain");

        var beforeCompletion = fixture.ReadPackets().Count;
        await fixture.Registry.AdvanceMonsterWorldOnceAsync(
            completionAt,
            CancellationToken.None);
        var completionPackets = fixture.ReadPackets()
            .Skip(beforeCompletion)
            .ToArray();
        var completionState = completionPackets.Single(packet =>
            ReadOpcode(packet) == Opcodes.RepetitionCompletionState);
        var countdown = completionPackets.Single(packet =>
            ReadOpcode(packet) == Opcodes.RepetitionReset);
        Check.True(
            BinaryPrimitives.ReadInt32LittleEndian(
                completionState.AsSpan(4)) == 209 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                completionState.AsSpan(8)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                countdown.AsSpan(4)) == 30 &&
            fixture.Character.CurrentMap == 200,
            "completion publishes captured state 10227 and a 30-second 10231 countdown without immediate egress");

        var sourceInstanceId = GetSourceInstanceId(fixture);
        var beforeTerminate = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateRepetitionLeave(
                repetitionId: 0,
                repetitionIndex: 0));
        var terminatePackets = fixture.ReadPackets()
            .Skip(beforeTerminate)
            .ToArray();
        Check.True(
            terminatePackets.Length == 1 &&
            terminatePackets[0].SequenceEqual(
                PacketBuilder.RepetitionReset()),
            "the registered completion leader closes its countdown despite stale client scene state");

        await fixture.Registry.AdvanceMonsterWorldOnceAsync(
            completionAt.AddMilliseconds(1),
            CancellationToken.None);
        var destinationMap = fixture.Character.Camp ==
                GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        Check.True(
            fixture.Character.CurrentMap == destinationMap &&
            GetSourceInstanceId(fixture) != sourceInstanceId &&
            fixture.ReadPackets().Skip(beforeTerminate).Any(packet =>
                ReadOpcode(packet) == Opcodes.SceneChange),
            "leader terminate requests immediate authoritative completion egress");
        fixture.Registry.UnregisterInstanceTransitionSink(fixture.Session);
    }

    private static MedusaPlayerMonsterDamageCommit DefeatCompletionBoss(
        InstanceCallerFixture fixture,
        string spawnId,
        CombatDamageChannel channel,
        ulong eventId,
        DateTimeOffset committedAt)
    {
        var index = MedusaIslandRosterPolicy.Spawns.IndexOf(
            MedusaIslandRosterPolicy.Spawns.Single(spawn =>
                spawn.SpawnId == spawnId));
        var objectId = checked(
            WorldObjectIds.FirstMedusaMonsterObjectId + (uint)index);
        Check.True(
            fixture.Registry.TryCapturePlayerMonsterTarget(
                fixture.Session,
                fixture.Character.CurrentMap,
                objectId,
                out var target,
                out var authority),
            $"completion fixture captures {spawnId}");
        var resolution = new CombatResolution(
            FormulaVersion: 1,
            eventId,
            TargetOrder: 0,
            channel,
            CombatHitOutcome.Normal,
            target.CurrentHealth,
            Rolls: default,
            Evidence: default);
        Check.True(
            fixture.Registry.TryCommitPlayerMonsterDamageGuarded(
                fixture.Session,
                fixture.Character.CurrentMap,
                target.ObjectId,
                target.RuntimeInstanceId,
                fixture.Character.Id,
                target.SpawnGeneration,
                target.HealthRevision,
                authority,
                committedAt,
                resolution,
                out var commit) &&
            commit.DamageResult is { Killed: true },
            $"completion fixture defeats {spawnId}");
        return commit;
    }
}
