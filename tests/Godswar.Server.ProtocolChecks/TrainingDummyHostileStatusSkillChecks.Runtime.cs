using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummyHostileStatusSkillChecks
{
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static readonly MethodInfo BeginPendingCastMethod =
        typeof(GameClientHandler).GetMethod(
            "TryBeginPendingSkillCastAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "TryBeginPendingSkillCastAsync was not found.");

    private static readonly MethodInfo InterruptPendingCastMethod =
        typeof(GameClientHandler).GetMethod(
            "InterruptPendingSkillCastAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "InterruptPendingSkillCastAsync was not found.");

    private static readonly MethodInfo StopPendingCastsMethod =
        typeof(GameClientHandler).GetMethod(
            "StopPendingSkillCastsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "StopPendingSkillCastsAsync was not found.");

    private static readonly FieldInfo PendingCastField =
        typeof(GameClientHandler).GetField(
            "_pendingSkillCast",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler._pendingSkillCast was not found.");

    private static readonly FieldInfo RegistryGateField =
        typeof(GameSessionRegistry).GetField(
            "_gate",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameSessionRegistry._gate was not found.");

    private static async Task CheckStatusOnlyHandlerRuntimeAsync()
    {
        await CheckInterruptionClaimLinearizationAsync();
        foreach (var skillId in new[] { 74, 354, 604, 794 })
        {
            await CheckAppliedControlHandlerAsync(skillId);
        }

        await CheckInterruptionFailureIsolationAsync();
        await CheckResistedControlHandlerAsync(skillId: 74);
        await CheckExposeArmorHandlerAsync();
    }

    private static async Task CheckInterruptionClaimLinearizationAsync()
    {
        await using var fixture = await HandlerFixture.CreateAsync(
            skillId: 74,
            shouldApply: true);
        await fixture.BeginTargetPendingCastAsync();
        if (!GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                fixture.Definition.SkillId,
                out var skill))
        {
            throw new InvalidOperationException(
                "Stun combat content was not found.");
        }

        var notificationBarrier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? interruption = null;
        var callbackObservedUnderGate = false;
        var callbackObservedBeforeMutation = false;
        var callbackClaimedPending = false;
        var registryGate = RegistryGateField.GetValue(fixture.Registry)
            ?? throw new InvalidOperationException(
                "The registry gate was null.");
        var decision = await fixture.Registry
            .ResolveTrainingDummyHostileStatusCastAsync(
                fixture.AttackerSocket.Session,
                TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
                fixture.TargetObjectId,
                skill,
                fixture.Definition,
                static () => 1,
                DateTimeOffset.UtcNow,
                CancellationToken.None,
                (target, definition) =>
                {
                    callbackObservedUnderGate =
                        Monitor.IsEntered(registryGate);
                    callbackObservedBeforeMutation = fixture.Registry
                        .CaptureTrainingDummyHostileStatusSnapshot(
                            target.Session,
                            DateTimeOffset.UtcNow)
                        .ActiveStatuses.Count == 0;
                    var reason = PlayerSkillCastControlCatalog
                        .ResolveAppliedInterruption(definition.StatusId)
                        ?? throw new InvalidOperationException(
                            "Applied Stun had no interruption mapping.");
                    interruption = fixture.Registry
                        .RequestSkillCastInterruptionAsync(
                            target.Session,
                            reason,
                            CancellationToken.None,
                            notificationBarrier.Task);
                    callbackClaimedPending =
                        fixture.TargetInterruptionClaimed;
                });

        Check.True(
            decision.Accepted &&
            decision.Targets is [{ Application.Applied: true }] &&
            callbackObservedUnderGate &&
            callbackObservedBeforeMutation &&
            callbackClaimedPending &&
            fixture.TargetInterruptionClaimed &&
            interruption is { IsCompleted: false },
            "an applied status synchronously claims the victim generation under the registry gate before the active-status mutation and transaction return");
        notificationBarrier.TrySetResult();
        await interruption!;
        var attackerInterrupt =
            await fixture.AttackerSocket.ReadPacketAsync(8);
        var targetInterrupt =
            await fixture.TargetSocket.ReadPacketAsync(8);
        Check.True(
            ReadOpcode(attackerInterrupt) == Opcodes.SkillCastInterrupt &&
            ReadOpcode(targetInterrupt) == Opcodes.SkillCastInterrupt &&
            !fixture.TargetPendingCompleted,
            "the claimed generation emits its native interruption only after the presentation barrier opens");
    }

    private static async Task CheckInterruptionFailureIsolationAsync()
    {
        await using var fixture = await HandlerFixture.CreateAsync(
            skillId: 74,
            shouldApply: true);
        fixture.UseFaultingInterruptionSink();
        await fixture.InvokeCastAsync();

        await fixture.AttackerSocket.ReadPacketAsync(40);
        await fixture.TargetSocket.ReadPacketAsync(40);
        await fixture.AttackerSocket.ReadPacketAsync(340);
        await fixture.TargetSocket.ReadPacketAsync(340);
        var attackerImpact =
            await fixture.AttackerSocket.ReadPacketAsync(24);
        var targetImpact =
            await fixture.TargetSocket.ReadPacketAsync(24);
        var attackerMana =
            await fixture.AttackerSocket.ReadPacketAsync(12);
        var targetMana =
            await fixture.TargetSocket.ReadPacketAsync(12);
        Check.True(
            ReadOpcode(attackerImpact) == 0x273E &&
            ReadOpcode(targetImpact) == 0x273E &&
            ReadOpcode(attackerMana) == 0x2797 &&
            ReadOpcode(targetMana) == 0x2797 &&
            fixture.AttackerSocket.Available == 0 &&
            fixture.TargetSocket.Available == 0,
            "a post-commit interruption sink fault is isolated after status projection and cannot skip impact or authoritative mana");
    }

    private static async Task CheckAppliedControlHandlerAsync(int skillId)
    {
        await using var fixture = await HandlerFixture.CreateAsync(
            skillId,
            shouldApply: true);
        await fixture.BeginTargetPendingCastAsync();
        await fixture.InvokeCastAsync();

        var attackerVisual =
            await fixture.AttackerSocket.ReadPacketAsync(40);
        var targetVisual =
            await fixture.TargetSocket.ReadPacketAsync(40);
        var attackerStatus =
            await fixture.AttackerSocket.ReadPacketAsync(340);
        var targetStatus =
            await fixture.TargetSocket.ReadPacketAsync(340);
        var attackerInterrupt =
            await fixture.AttackerSocket.ReadPacketAsync(8);
        var targetInterrupt =
            await fixture.TargetSocket.ReadPacketAsync(8);
        var attackerImpact =
            await fixture.AttackerSocket.ReadPacketAsync(24);
        var targetImpact =
            await fixture.TargetSocket.ReadPacketAsync(24);
        var attackerMana =
            await fixture.AttackerSocket.ReadPacketAsync(12);
        var targetMana =
            await fixture.TargetSocket.ReadPacketAsync(12);

        AssertVisual(
            attackerVisual,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            fixture.TargetObjectId,
            skillId,
            $"skill {skillId} attacker visual");
        AssertVisual(
            targetVisual,
            fixture.AttackerObjectId,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            skillId,
            $"skill {skillId} target visual");
        AssertStatus(
            attackerStatus,
            fixture.TargetObjectId,
            fixture.Definition,
            $"skill {skillId} remote status");
        AssertStatus(
            targetStatus,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            fixture.Definition,
            $"skill {skillId} local status");
        Check.True(
            ReadOpcode(attackerInterrupt) ==
                Opcodes.SkillCastInterrupt &&
            ReadObjectId(attackerInterrupt) == fixture.TargetObjectId &&
            ReadOpcode(targetInterrupt) ==
                Opcodes.SkillCastInterrupt &&
            ReadObjectId(targetInterrupt) ==
                TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId &&
            !fixture.TargetPendingCompleted,
            $"skill {skillId} cancels the victim's actual pending generation after status publication");
        AssertImpact(
            attackerImpact,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            fixture.TargetObjectId,
            skillId,
            $"skill {skillId} attacker impact");
        AssertImpact(
            targetImpact,
            fixture.AttackerObjectId,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            skillId,
            $"skill {skillId} target impact");
        Check.True(
            ReadOpcode(attackerMana) == 0x2797 &&
            ReadObjectId(attackerMana) ==
                TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId &&
            ReadOpcode(targetMana) == 0x2797 &&
            ReadObjectId(targetMana) == fixture.AttackerObjectId &&
            BinaryPrimitives.ReadInt32LittleEndian(
                attackerMana.AsSpan(8, 4)) ==
                fixture.Attacker.MaxMp - fixture.Definition.ManaCost,
            $"skill {skillId} publishes one authoritative MP charge");
        Check.Equal(
            0,
            fixture.AttackerSocket.Available +
            fixture.TargetSocket.Available,
            $"skill {skillId} emits no duplicate status-only packets");
    }

    private static async Task CheckResistedControlHandlerAsync(int skillId)
    {
        await using var fixture = await HandlerFixture.CreateAsync(
            skillId,
            shouldApply: false);
        await fixture.InvokeCastAsync();

        var attackerVisual =
            await fixture.AttackerSocket.ReadPacketAsync(40);
        var targetVisual =
            await fixture.TargetSocket.ReadPacketAsync(40);
        var attackerImpact =
            await fixture.AttackerSocket.ReadPacketAsync(24);
        var targetImpact =
            await fixture.TargetSocket.ReadPacketAsync(24);
        var attackerMana =
            await fixture.AttackerSocket.ReadPacketAsync(12);
        var targetMana =
            await fixture.TargetSocket.ReadPacketAsync(12);
        AssertVisual(
            attackerVisual,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            fixture.TargetObjectId,
            skillId,
            "resisted attacker visual");
        AssertVisual(
            targetVisual,
            fixture.AttackerObjectId,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            skillId,
            "resisted target visual");
        AssertImpact(
            attackerImpact,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            fixture.TargetObjectId,
            skillId,
            "resisted attacker impact");
        AssertImpact(
            targetImpact,
            fixture.AttackerObjectId,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            skillId,
            "resisted target impact");
        Check.True(
            ReadOpcode(attackerMana) == 0x2797 &&
            ReadOpcode(targetMana) == 0x2797 &&
            fixture.Attacker.CurrentMp ==
                fixture.Attacker.MaxMp - fixture.Definition.ManaCost &&
            fixture.Registry.CaptureTrainingDummyHostileStatusSnapshot(
                fixture.TargetSocket.Session,
                DateTimeOffset.UtcNow).ActiveStatuses.Count == 0 &&
            fixture.AttackerSocket.Available == 0 &&
            fixture.TargetSocket.Available == 0,
            "a resisted status still shows cast/impact and charges MP without publishing 0x27B7");
    }

    private static async Task CheckExposeArmorHandlerAsync()
    {
        await using var fixture = await HandlerFixture.CreateAsync(
            skillId: 84,
            shouldApply: true);
        await fixture.InvokeCastAsync();
        var attackerVisual =
            await fixture.AttackerSocket.ReadPacketAsync(40);
        var targetVisual =
            await fixture.TargetSocket.ReadPacketAsync(40);
        var targetStatus =
            await fixture.TargetSocket.ReadPacketAsync(340);
        var targetGameData =
            await fixture.TargetSocket.ReadPacketAsync(236);
        var attackerStatus =
            await fixture.AttackerSocket.ReadPacketAsync(340);
        var attackerImpact =
            await fixture.AttackerSocket.ReadPacketAsync(24);
        var targetImpact =
            await fixture.TargetSocket.ReadPacketAsync(24);
        await fixture.AttackerSocket.ReadPacketAsync(12);
        await fixture.TargetSocket.ReadPacketAsync(12);

        AssertVisual(
            attackerVisual,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            84,
            "Expose Armor attacker self-area visual");
        AssertVisual(
            targetVisual,
            fixture.AttackerObjectId,
            fixture.AttackerObjectId,
            84,
            "Expose Armor target self-area visual");
        AssertStatus(
            attackerStatus,
            fixture.TargetObjectId,
            fixture.Definition,
            "Expose Armor remote status");
        AssertStatus(
            targetStatus,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            fixture.Definition,
            "Expose Armor local status");
        Check.True(
            ReadOpcode(targetGameData) == 0x27B6 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                targetStatus.AsSpan(192, 4)) == 600 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                targetStatus.AsSpan(200, 4)) == 700,
            "Expose Armor synchronizes local GameData and reduced defense fields");
        AssertImpact(
            attackerImpact,
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId,
            uint.MaxValue,
            84,
            "Expose Armor attacker self-area impact");
        AssertImpact(
            targetImpact,
            fixture.AttackerObjectId,
            uint.MaxValue,
            84,
            "Expose Armor target self-area impact");
    }

    private static void AssertVisual(
        byte[] packet,
        uint casterId,
        uint targetId,
        int skillId,
        string scope) =>
        Check.True(
            ReadOpcode(packet) == Opcodes.SkillCast &&
            ReadObjectId(packet) == casterId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(8, 4)) == checked((uint)skillId) &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(16, 4)) == targetId,
            $"{scope} translates caster and target IDs");

    private static void AssertStatus(
        byte[] packet,
        uint objectId,
        in HostileStatusEffectDefinition definition,
        string scope) =>
        Check.True(
            ReadOpcode(packet) == 0x27B7 &&
            ReadObjectId(packet) == objectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(8, 4)) == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(12, 4)) == definition.StatusId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(92, 4)) ==
                checked((uint)definition.Duration.TotalSeconds),
            $"{scope} publishes the fixed stock status and duration");

    private static void AssertImpact(
        byte[] packet,
        uint casterId,
        uint targetId,
        int skillId,
        string scope) =>
        Check.True(
            ReadOpcode(packet) == 0x273E &&
            ReadObjectId(packet) == casterId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(8, 4)) == targetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(12, 4)) == checked((uint)skillId),
            $"{scope} identifies the committed status-only cast");

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));

    private static uint ReadObjectId(byte[] packet) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));

    private static async Task InvokePacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "HandlePacketAsync returned no task.");
        await task;
    }

    private static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

}
