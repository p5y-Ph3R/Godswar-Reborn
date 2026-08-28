using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static readonly MethodInfo HandleEcsSingleSkillMethod =
        typeof(GameClientHandler).GetMethod(
            "HandleHostileMonsterSingleSkillCastEcsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The ECS single-skill handler was not found.");

    private static async Task CheckCooldownRejectedCompletionAsync(
        SkillCombatDefinition combat,
        PlayerRuntimeMode runtimeMode)
    {
        await using var fixture = await Fixture.CreateAsync(
            $"Cooldown{runtimeMode}",
            playerRuntimeMode: runtimeMode);
        await fixture.BeginCastAsync();
        await fixture.Socket.ReadPacketAsync(32);
        await fixture.Socket.ReadPacketAsync(24);
        await fixture.Socket.ReadPacketAsync(12);
        await fixture.Store.WaitForVitalsWriteAsync();

        var healthAfterAcceptedCast = fixture.CurrentMonsterHealth();
        var manaAfterAcceptedCast = fixture.Character.CurrentMp;
        var writesAfterAcceptedCast = fixture.Store.VitalsWrites;
        var acceptedEcsIntent = fixture.Registry
            .GetPlayerCombatEcsDiagnostics(fixture.Socket.Session)?
            .IntentId;
        var acceptedLegacyRevision = ReadLegacyCombatRevision(
            fixture.Handler);

        await fixture.BeginCastAsync();
        var notice = await fixture.Socket.ReadPacketAsync(12);
        var interruption = await fixture.Socket.ReadPacketAsync(8);

        Check.True(
            notice.SequenceEqual(PacketBuilder.LocalizedError(
                NativeErrorCodes.SkillNotReady)),
            $"{runtimeMode} cooldown rejection reaches the native left log");
        Check.True(
            interruption.SequenceEqual(
                PacketBuilder.SkillCastInterrupt(LocalObjectId)),
            $"{runtimeMode} cooldown rejection clears the published cast");
        Check.Equal(
            manaAfterAcceptedCast,
            fixture.Character.CurrentMp,
            $"{runtimeMode} cooldown rejection consumes no MP");
        Check.Equal(
            healthAfterAcceptedCast,
            fixture.CurrentMonsterHealth(),
            $"{runtimeMode} cooldown rejection mutates no monster health");
        Check.Equal(
            writesAfterAcceptedCast,
            fixture.Store.VitalsWrites,
            $"{runtimeMode} cooldown rejection persists no vitals");

        if (runtimeMode == PlayerRuntimeMode.Ecs)
        {
            var currentEcsIntent = fixture.Registry
                .GetPlayerCombatEcsDiagnostics(
                    fixture.Socket.Session)?
                .IntentId;
            Check.True(
                acceptedEcsIntent.HasValue && currentEcsIntent.HasValue,
                "ECS cooldown fixture retains combat diagnostics");
            Check.Equal(
                acceptedEcsIntent.GetValueOrDefault(),
                currentEcsIntent.GetValueOrDefault(),
                "ECS cooldown rejection queues no combat intent");
        }
        else
        {
            Check.Equal(
                acceptedLegacyRevision,
                ReadLegacyCombatRevision(fixture.Handler),
                "legacy cooldown rejection advances no combat revision");
        }
    }

    private static long ReadLegacyCombatRevision(
        GameClientHandler handler)
    {
        var field = typeof(GameClientHandler).GetField(
            "_legacyAdmittedCombatRevision",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "GameClientHandler legacy combat revision was not found.");
        return (long)(field.GetValue(handler) ?? 0L);
    }

    private static async Task
        CheckConcurrentManaRiseRetainsCooldownAsync(
            SkillCombatDefinition combat)
    {
        await using var fixture = await Fixture.CreateAsync(
            "CooldownConcurrentManaRise",
            playerRuntimeMode: PlayerRuntimeMode.Ecs);
        lock (fixture.Character.VitalsSync)
        {
            fixture.Character.CurrentMp = Math.Max(0, combat.Mp - 1);
            fixture.Character.MarkVitalsChanged();
        }

        var combatGate = GetPlayerCombatEcsGate(
            fixture.Registry,
            fixture.Socket.Session);
        using var releaseGate = new ManualResetEventSlim();
        var gateHeld = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gateOwner = Task.Run(() =>
        {
            lock (combatGate)
            {
                gateHeld.TrySetResult(true);
                if (!releaseGate.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "The ECS combat-gate fixture was not released.");
                }
            }
        });
        await gateHeld.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Check.True(
            SkillCastRequest.TryParse(
                CreateSkillCastPacket(
                    fixture.Character.PositionX,
                    fixture.Character.PositionZ).Buffer,
                out var cast),
            "concurrent mana-rise fixture parses its skill cast");
        var packet = CreateSkillCastPacket(
            fixture.Character.PositionX,
            fixture.Character.PositionZ);
        var admitted = Task.Run(() => InvokeEcsSingleSkillAsync(
            fixture.Handler,
            packet,
            cast,
            combat));

        try
        {
            await WaitUntilAsync(
                () => fixture.Registry
                    .HostileSkillCooldownOwnerCount == 1,
                TimeSpan.FromSeconds(1));
            lock (fixture.Character.VitalsSync)
            {
                fixture.Character.CurrentMp = InitialMana;
                fixture.Character.MarkVitalsChanged();
            }
        }
        finally
        {
            releaseGate.Set();
            await gateOwner;
        }

        await admitted;
        await fixture.Socket.ReadPacketAsync(32);
        await fixture.Socket.ReadPacketAsync(24);
        await fixture.Socket.ReadPacketAsync(12);
        var healthAfterAdmitted = fixture.CurrentMonsterHealth();
        var manaAfterAdmitted = fixture.Character.CurrentMp;
        var admittedIntent = fixture.Registry
            .GetPlayerCombatEcsDiagnostics(
                fixture.Socket.Session)?.IntentId;

        await InvokeEcsSingleSkillAsync(
            fixture.Handler,
            packet,
            cast,
            combat);
        var replayNotice = await fixture.Socket.ReadPacketAsync(12);
        var replayInterruption = await fixture.Socket.ReadPacketAsync(8);
        Check.True(
            replayNotice.SequenceEqual(PacketBuilder.LocalizedError(
                NativeErrorCodes.SkillNotReady)) &&
            replayInterruption.SequenceEqual(
                PacketBuilder.SkillCastInterrupt(LocalObjectId)),
            "mana-rise replay inside cooldown reports the rejection and clears client casting");
        Check.Equal(
            healthAfterAdmitted,
            fixture.CurrentMonsterHealth(),
            "mana-rise replay inside cooldown applies no damage");
        Check.Equal(
            manaAfterAdmitted,
            fixture.Character.CurrentMp,
            "mana-rise replay inside cooldown consumes no MP");
        Check.Equal(
            admittedIntent.GetValueOrDefault(),
            fixture.Registry.GetPlayerCombatEcsDiagnostics(
                fixture.Socket.Session)?.IntentId ?? 0,
            "mana-rise replay never queues a second ECS intent");
    }

    private static async Task InvokeEcsSingleSkillAsync(
        GameClientHandler handler,
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat)
    {
        var task = HandleEcsSingleSkillMethod.Invoke(
            handler,
            [
                packet,
                cast,
                combat,
                false,
                null,
                CancellationToken.None
            ]) as Task ?? throw new InvalidOperationException(
                "The ECS single-skill handler returned no task.");
        await task;
    }

    private static object GetPlayerCombatEcsGate(
        GameSessionRegistry registry,
        Networking.ClientSession session)
    {
        var getRuntime = typeof(GameSessionRegistry).GetMethod(
            "GetPlayerRuntimeEcs",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The player ECS runtime accessor was not found.");
        var adapters = getRuntime.Invoke(registry, [session])
            ?? throw new InvalidOperationException(
                "The player ECS runtime was not created.");
        var combat = adapters.GetType().GetProperty("Combat")?
            .GetValue(adapters)
            ?? throw new InvalidOperationException(
                "The player combat ECS adapter was not found.");
        return combat.GetType().GetField(
                   "_gate",
                   BindingFlags.Instance | BindingFlags.NonPublic)?
                   .GetValue(combat)
               ?? throw new InvalidOperationException(
                   "The player combat ECS synchronization gate was not found.");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The cooldown admission was not observed.");
            }

            await Task.Delay(10);
        }
    }
}
