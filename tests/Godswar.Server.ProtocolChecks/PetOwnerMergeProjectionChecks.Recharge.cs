using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetOwnerMergeProjectionChecks
{
    private static async Task CheckUnmergedPetEnergyRechargeAsync()
    {
        await CheckPresenceRestoreStartsEnergyRechargeAsync();
        await CheckEnergyRechargeHeartbeatAndCancellationAsync();
        await CheckShutdownSettlesDrainBeforeRechargeAsync();
    }

    private static async Task CheckPresenceRestoreStartsEnergyRechargeAsync()
    {
        var pet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 12) with
        {
            CurrentEnergy = 79,
            MaximumEnergy = 100,
            ContributesToCharacter = false
        };
        var executor = new OwnerMergeLifecycleTestExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [pet],
            executor,
            petOwnerMergeRechargeInterval:
                TimeSpan.FromMilliseconds(200));
        var restorePresence = typeof(GameClientHandler).GetMethod(
            "RestorePetPresenceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Pet presence restore method was not found.");
        var cancel = RequireLifecycleMethod(
            "CancelPetOwnerMergeEnergyRecharge");

        var restoreTask = restorePresence.Invoke(
            fixture.Handler,
            new object[]
            {
                new[] { pet },
                false,
                CancellationToken.None
            }) as Task ?? throw new InvalidOperationException(
                "Pet presence restore returned no task.");
        await restoreTask;
        await Task.Delay(300);
        Check.Equal(
            1,
            executor.RestoreCount,
            "post-enter carried-pet presence starts online energy recovery");
        Check.True(
            executor.RestoreEnergyPointRequests.SequenceEqual([5]),
            "online recovery requests five normalized energy points per tick");
        cancel.Invoke(fixture.Handler, null);
    }

    private static async Task CheckEnergyRechargeHeartbeatAndCancellationAsync()
    {
        var executor = new OwnerMergeLifecycleTestExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [],
            executor,
            petOwnerMergeRechargeInterval:
                TimeSpan.FromMilliseconds(200));
        var start = RequireLifecycleMethod(
            "StartPetOwnerMergeEnergyRecharge");
        var cancel = RequireLifecycleMethod(
            "CancelPetOwnerMergeEnergyRecharge");

        start.Invoke(fixture.Handler, null);
        await Task.Delay(500);
        Check.Equal(
            2,
            executor.RestoreCount,
            "unmerged carried pet receives authoritative recovery and heartbeat ticks");
        Check.True(
            executor.RestoreEnergyPointRequests.SequenceEqual([5, 5]),
            "each heartbeat requests the five-times recovery delta");
        var energyPackets = fixture.Transport.ReadLegacyPackets()
            .Where(packet =>
                BinaryPrimitives.ReadUInt16LittleEndian(
                    packet.AsSpan(2)) == Opcodes.PetEnergy)
            .ToArray();
        Check.Equal(
            2,
            energyPackets.Length,
            "recovery lifecycle projects each six-second cadence boundary");
        Check.Equal(
            1_440u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                energyPackets[0].AsSpan(4)),
            "recharge tick projects authoritative 80/100 energy");
        Check.Equal(
            1_800u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                energyPackets[1].AsSpan(4)),
            "full carried pet retains the captured energy heartbeat");

        cancel.Invoke(fixture.Handler, null);
        await Task.Delay(250);
        Check.Equal(
            2,
            executor.RestoreCount,
            "cancelled recharge generation cannot mutate pet energy");
    }

    private static MethodInfo RequireLifecycleMethod(string name) =>
        typeof(GameClientHandler).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"Owner-Merge lifecycle method '{name}' was not found.");

    private static async Task CheckShutdownSettlesDrainBeforeRechargeAsync()
    {
        var executor = new OwnerMergeLifecycleTestExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [],
            executor,
            petOwnerMergeRechargeInterval:
                TimeSpan.FromMilliseconds(500));
        var startRecharge = RequireLifecycleMethod(
            "StartPetOwnerMergeEnergyRecharge");
        var stopLifecycle = RequireLifecycleMethod(
            "StopPetOwnerMergeEnergyLifecycleAsync");
        var drainCancellation = new CancellationTokenSource();
        var rechargeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var simulatedExpiringDrain = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    drainCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }

            startRecharge.Invoke(fixture.Handler, null);
            rechargeStarted.TrySetResult();
        });
        PetDurableHandlerFixture.SetField(
            fixture.Handler,
            "_petOwnerMergeLifecycleCancellation",
            drainCancellation);
        PetDurableHandlerFixture.SetField(
            fixture.Handler,
            "_petOwnerMergeLifecycleTask",
            simulatedExpiringDrain);

        var stopTask = stopLifecycle.Invoke(
            fixture.Handler,
            null) as Task ?? throw new InvalidOperationException(
                "Owner-Merge energy lifecycle stop returned no task.");
        await stopTask;
        Check.True(
            rechargeStarted.Task.IsCompletedSuccessfully,
            "drain shutdown barrier exercised the expiry-to-recharge handoff");
        Check.True(
            ReadLifecycleField(
                fixture.Handler,
                "_petOwnerMergeRechargeTask") is null &&
            ReadLifecycleField(
                fixture.Handler,
                "_petOwnerMergeRechargeCancellation") is null,
            "shutdown clears recharge task and cancellation ownership after the drain handoff");
        await Task.Delay(600);
        Check.Equal(
            0,
            executor.RestoreCount,
            "shutdown cancels recharge created while the drain task unwinds");
    }

    private static object? ReadLifecycleField(
        GameClientHandler handler,
        string name)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"Owner-Merge lifecycle field '{name}' was not found.");
        return field.GetValue(handler);
    }
}
