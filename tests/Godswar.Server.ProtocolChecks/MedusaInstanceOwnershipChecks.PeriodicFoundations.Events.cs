using System.Reflection;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static readonly MethodInfo ResolveFoundationAttackEventId =
        typeof(GameSessionRegistry).GetMethod(
            "ResolveMonsterAttackEventId",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "The shared monster-attack event resolver is unavailable.");

    private static readonly MethodInfo TryAllocateFoundationAttackEventId =
        typeof(GameSessionRegistry).GetMethod(
            "TryAllocateMonsterAttackEventIdAbove",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "The shared monster-attack event allocator is unavailable.");

    private static async Task CheckSharedMonsterAttackEventFloorAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        const ulong explicitHigh = 9_400_000;
        var explicitAttack = fixture.CreateAttack(explicitHigh);
        var zeroAttack = fixture.CreateAttack(0);
        var secondZeroAttack = fixture.CreateAttack(0);

        var observedExplicit = ResolveFoundationEventId(
            fixture.Registry,
            explicitAttack);
        var firstGenerated = ResolveFoundationEventId(
            fixture.Registry,
            zeroAttack);
        var stableGenerated = ResolveFoundationEventId(
            fixture.Registry,
            zeroAttack);
        var secondGenerated = ResolveFoundationEventId(
            fixture.Registry,
            secondZeroAttack);
        Check.True(
            observedExplicit == explicitHigh &&
            firstGenerated == explicitHigh + 1 &&
            stableGenerated == firstGenerated &&
            secondGenerated == explicitHigh + 2,
            "an explicit high event raises the shared floor and each zero-ID update owns one stable CWT identity");

        var targetFloor = explicitHigh + 100;
        ulong aboveGlobalFloor = 0;
        Check.True(
            TryAllocateFoundationEventId(
                fixture.Registry,
                targetFloor,
                out var aboveTargetFloor) &&
            aboveTargetFloor == targetFloor + 1 &&
            TryAllocateFoundationEventId(
                fixture.Registry,
                floor: 1,
                out aboveGlobalFloor) &&
            aboveGlobalFloor == targetFloor + 2,
            "periodic allocation advances above both the supplied target ECS floor and the process-wide high-water mark");

        const int concurrentCount = 16;
        var concurrent = Enumerable.Range(0, concurrentCount)
            .Select(_ => Task.Run(() =>
            {
                Check.True(
                    TryAllocateFoundationEventId(
                        fixture.Registry,
                        aboveGlobalFloor,
                        out var allocated),
                    "a non-exhausted shared event allocation succeeds");
                return allocated;
            }))
            .ToArray();
        var allocatedIds = await Task.WhenAll(concurrent);
        Array.Sort(allocatedIds);
        Check.True(
            allocatedIds.Distinct().Count() == concurrentCount &&
            allocatedIds[0] == aboveGlobalFloor + 1 &&
            allocatedIds[^1] == aboveGlobalFloor + concurrentCount &&
            allocatedIds.Select(
                    (value, index) => value ==
                        aboveGlobalFloor + (ulong)index + 1)
                .All(static contiguous => contiguous),
            "concurrent consumers share one unique contiguous monotonic event sequence");

        Check.True(
            !TryAllocateFoundationEventId(
                fixture.Registry,
                ulong.MaxValue,
                out var exhausted) &&
            exhausted == 0 &&
            !TryAllocateFoundationEventId(
                fixture.Registry,
                floor: 0,
                out var stillExhausted) &&
            stillExhausted == 0,
            "observing ulong.MaxValue exhausts and permanently absorbs the shared event floor without wrapping");

        Exception? requiredFailure = null;
        try
        {
            _ = ResolveFoundationEventId(
                fixture.Registry,
                fixture.CreateAttack(0));
        }
        catch (TargetInvocationException error)
        {
            requiredFailure = error.InnerException;
        }
        Check.True(
            requiredFailure is InvalidOperationException,
            "required zero-ID resolution fails closed after event-space exhaustion instead of issuing a low replay ID");
    }

    private static ulong ResolveFoundationEventId(
        GameSessionRegistry registry,
        MonsterRuntimeUpdate attack) =>
        (ulong)(ResolveFoundationAttackEventId.Invoke(
            registry,
            [attack]) ?? throw new InvalidOperationException(
                "The shared event resolver returned no identity."));

    private static bool TryAllocateFoundationEventId(
        GameSessionRegistry registry,
        ulong floor,
        out ulong eventId)
    {
        object?[] arguments = [floor, 0UL];
        var allocated = (bool)(TryAllocateFoundationAttackEventId.Invoke(
            registry,
            arguments) ?? false);
        eventId = (ulong)(arguments[1] ?? 0UL);
        return allocated;
    }
}
