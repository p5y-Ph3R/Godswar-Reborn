using System.Buffers.Binary;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummyHostileStatusSkillChecks
{
    private sealed class HandlerFixture : IAsyncDisposable
    {
        private HandlerFixture(
            RuntimePolicySessionSocket attackerSocket,
            RuntimePolicySessionSocket targetSocket,
            GameSessionRegistry registry,
            GameCharacter attacker,
            GameCharacter target,
            GameClientHandler attackerHandler,
            GameClientHandler targetHandler,
            HostileStatusEffectDefinition definition)
        {
            AttackerSocket = attackerSocket;
            TargetSocket = targetSocket;
            Registry = registry;
            Attacker = attacker;
            Target = target;
            AttackerHandler = attackerHandler;
            TargetHandler = targetHandler;
            Definition = definition;
        }

        public RuntimePolicySessionSocket AttackerSocket { get; }
        public RuntimePolicySessionSocket TargetSocket { get; }
        public GameSessionRegistry Registry { get; }
        public GameCharacter Attacker { get; }
        public GameCharacter Target { get; }
        public GameClientHandler AttackerHandler { get; }
        public GameClientHandler TargetHandler { get; }
        public HostileStatusEffectDefinition Definition { get; }
        public bool TargetPendingCompleted { get; private set; }
        public bool TargetInterruptionClaimed
        {
            get
            {
                var pending = PendingCastField.GetValue(TargetHandler)
                    ?? throw new InvalidOperationException(
                        "The target pending generation was absent.");
                return (bool)(pending.GetType().GetProperty(
                        "InterruptionClaimed")?.GetValue(pending)
                    ?? throw new InvalidOperationException(
                        "PendingSkillCast.InterruptionClaimed was absent."));
            }
        }
        public uint AttackerObjectId =>
            Registry.GetRequiredPlayerObjectId(AttackerSocket.Session);
        public uint TargetObjectId =>
            Registry.GetRequiredPlayerObjectId(TargetSocket.Session);

        public static async Task<HandlerFixture> CreateAsync(
            int skillId,
            bool shouldApply)
        {
            Check.True(
                TrainingDummyHostileStatusSkillCatalog.TryGet(
                    skillId,
                    out var definition),
                $"handler fixture resolves status skill {skillId}");
            var target = TrainingDummyHostileStatusTestFixture.CreateDummy();
            var attacker = FindFirstProcAttacker(
                definition,
                target,
                shouldApply);
            var attackerSocket =
                await RuntimePolicySessionSocket.CreateAsync();
            try
            {
                var targetSocket =
                    await RuntimePolicySessionSocket.CreateAsync();
                var registry = TrainingDummyHostileStatusTestFixture
                    .CreateRegistry();
                registry.JoinPlayerMap(
                    attackerSocket.Session,
                    attacker.AccountId,
                    attacker);
                registry.JoinPlayerMap(
                    targetSocket.Session,
                    target.AccountId,
                    target);
                var store = new LearnedSkillStore(skillId);
                var attackerHandler = CreateHandler(
                    attackerSocket,
                    store,
                    registry,
                    attacker);
                var targetHandler = CreateHandler(
                    targetSocket,
                    new LearnedSkillStore(),
                    registry,
                    target);
                var fixture = new HandlerFixture(
                    attackerSocket,
                    targetSocket,
                    registry,
                    attacker,
                    target,
                    attackerHandler,
                    targetHandler,
                    definition);
                fixture.RegisterTargetInterruptionSink();
                return fixture;
            }
            catch
            {
                await attackerSocket.DisposeAsync();
                throw;
            }
        }

        public Task InvokeCastAsync() =>
            InvokePacketAsync(
                AttackerHandler,
                CreateSkillCastPacket(
                    checked((uint)Definition.SkillId),
                    TargetObjectId,
                    Attacker.PositionX,
                    Attacker.PositionZ));

        public async Task BeginTargetPendingCastAsync()
        {
            Func<CancellationToken, Task> publishStart =
                static _ => Task.CompletedTask;
            Func<CancellationToken, Task> complete = _ =>
            {
                TargetPendingCompleted = true;
                return Task.CompletedTask;
            };
            var task = BeginPendingCastMethod.Invoke(
                TargetHandler,
                [
                    9_999u,
                    TimeSpan.FromSeconds(30),
                    "hostile-status-victim",
                    publishStart,
                    complete,
                    CancellationToken.None,
                    null
                ]) as Task<bool>
                ?? throw new InvalidOperationException(
                    "TryBeginPendingSkillCastAsync returned no task.");
            Check.True(
                await task,
                "victim pending cast starts before hostile status");
        }

        private void RegisterTargetInterruptionSink()
        {
            var sink = (Func<
                SkillCastInterruptionReason,
                CancellationToken,
                Task?,
                Task>)InterruptPendingCastMethod.CreateDelegate(
                    typeof(Func<
                        SkillCastInterruptionReason,
                        CancellationToken,
                        Task?,
                        Task>),
                    TargetHandler);
            Registry.RegisterSkillCastInterruptionSink(
                TargetSocket.Session,
                sink);
        }

        public void UseFaultingInterruptionSink()
        {
            Registry.UnregisterSkillCastInterruptionSink(
                TargetSocket.Session);
            Registry.RegisterSkillCastInterruptionSink(
                TargetSocket.Session,
                static (_, _, _) => Task.FromException(
                    new IOException("injected interruption sink failure")));
        }

        public async ValueTask DisposeAsync()
        {
            Registry.UnregisterSkillCastInterruptionSink(
                TargetSocket.Session);
            await StopHandlerAsync(AttackerHandler);
            await StopHandlerAsync(TargetHandler);
            Registry.Remove(AttackerSocket.Session);
            Registry.Remove(TargetSocket.Session);
            await AttackerSocket.DisposeAsync();
            await TargetSocket.DisposeAsync();
        }

        private static GameClientHandler CreateHandler(
            RuntimePolicySessionSocket socket,
            LearnedSkillStore store,
            GameSessionRegistry registry,
            GameCharacter character)
        {
            var handler = new GameClientHandler(
                socket.Session,
                store,
                registry,
                CharacterSnapshotReaderTestFixtures.Unused,
                WorldContentReaderTestFixtures.Empty);
            SetField(
                handler,
                "_account",
                new AccountIdentity(
                    character.AccountId,
                    $"status-{character.Id}"));
            SetField(handler, "_character", character);
            SetField(handler, "_registered", true);
            SetField(handler, "_worldPresenceAnnounced", true);
            return handler;
        }

        private static async Task StopHandlerAsync(
            GameClientHandler handler)
        {
            var task = StopPendingCastsMethod.Invoke(
                handler,
                null) as Task
                ?? throw new InvalidOperationException(
                    "StopPendingSkillCastsAsync returned no task.");
            await task;
        }
    }

    private static GameCharacter FindFirstProcAttacker(
        in HostileStatusEffectDefinition definition,
        GameCharacter target,
        bool shouldApply)
    {
        for (var id = 8_801; id < 9_801; id++)
        {
            var attacker = TrainingDummyHostileStatusTestFixture
                .CreateAttacker(
                    definition.RequiredProfession,
                    id,
                    $"StatusTester{id}");
            if (TrainingDummyHostileStatusTestFixture.FindRevision(
                    attacker,
                    target,
                    definition,
                    shouldApply) == 1)
            {
                return attacker;
            }
        }

        throw new InvalidOperationException(
            $"No first-revision proc outcome applied={shouldApply} " +
            $"was found for skill {definition.SkillId}.");
    }

    private static GamePacket CreateSkillCastPacket(
        uint skillId,
        uint targetObjectId,
        float casterX,
        float casterZ)
    {
        var packet = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.SkillCast);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            TrainingDummyHostileStatusTestFixture.LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            skillId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16),
            targetObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(24),
            casterX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28),
            casterZ);
        return new GamePacket(packet);
    }

    private sealed class LearnedSkillStore(params int[] skillIds)
        : GameStoreTestStub
    {
        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillState>>(
                skillIds.Select(static id => new SkillState
                {
                    SkillId = id,
                    Level = 1
                }).ToArray());
    }
}
