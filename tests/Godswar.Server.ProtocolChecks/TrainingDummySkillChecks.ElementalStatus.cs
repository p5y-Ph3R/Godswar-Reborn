using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private static async Task CheckElementalBurnStatusEgressAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await using var fixture = await Fixture.CreateAsync(
            bindElementalOwnership: true);
        var elemental = InitializeElementalState(
            fixture.Registry,
            fixture.TargetSocket.Session,
            fixture.Target);

        Check.True(
            SkillStatusEffectCatalog.TryGet(344, out var sacredZeal) &&
            await fixture.Registry.ApplyRuntimeStatusAndPublishAsync(
                fixture.TargetSocket.Session,
                sacredZeal,
                now,
                "training-burn-baseline",
                CancellationToken.None),
            "training Burn fixture installs an unrelated status baseline");
        var localBaseline =
            await fixture.TargetSocket.ReadPacketAsync(340);
        await fixture.TargetSocket.ReadPacketAsync(236);
        var remoteBaseline =
            await fixture.AttackerSocket.ReadPacketAsync(340);
        AssertStatusSnapshot(
            localBaseline,
            LocalPlayerObjectId,
            [204],
            "local unrelated baseline");
        AssertStatusSnapshot(
            remoteBaseline,
            fixture.TargetObjectId,
            [204],
            "remote unrelated baseline");

        Check.True(
            ApplyBurn(
                elemental,
                TrainingBurn(
                    fixture.Attacker,
                    fixture.Target,
                    eventId: 1,
                    now,
                    totalDamage: 80)),
            "authoritative training-dummy Burn applies");
        Check.Equal(
            1,
            await fixture.Registry
                .ReconcileTrainingDummyElementalStatusesOnceAsync(
                    now,
                    CancellationToken.None),
            "Burn application publishes one target snapshot");
        var (localBurn, remoteBurn) = await ReadStatusPairAsync(fixture);
        AssertStatusSnapshot(
            localBurn,
            LocalPlayerObjectId,
            [40, 204],
            "local Burn application");
        AssertStatusSnapshot(
            remoteBurn,
            fixture.TargetObjectId,
            [40, 204],
            "remote Burn application");
        Check.True(
            ReadStatusTimer(localBurn, 40) == 4 &&
            localBurn.AsSpan(172).SequenceEqual(
                localBaseline.AsSpan(172)),
            "Burn carries four real seconds and preserves aggregate status bytes");

        Check.True(
            !ApplyBurn(
                elemental,
                TrainingBurn(
                    fixture.Attacker,
                    fixture.Target,
                    eventId: 2,
                    now.AddMilliseconds(500),
                    totalDamage: 40)) &&
            await fixture.Registry
                .ReconcileTrainingDummyElementalStatusesOnceAsync(
                    now.AddMilliseconds(500),
                    CancellationToken.None) == 0 &&
            fixture.TargetSocket.Available == 0 &&
            fixture.AttackerSocket.Available == 0,
            "rejected weaker Burn does not refresh the icon");

        Check.True(
            ApplyBurn(
                elemental,
                TrainingBurn(
                    fixture.Attacker,
                    fixture.Target,
                    eventId: 3,
                    now.AddMilliseconds(500),
                    totalDamage: 120)) &&
            await fixture.Registry
                .ReconcileTrainingDummyElementalStatusesOnceAsync(
                    now.AddMilliseconds(500),
                    CancellationToken.None) == 1,
            "accepted stronger Burn refreshes the icon authority");
        var refreshed = await ReadStatusPairAsync(fixture);
        Check.Equal(
            4u,
            ReadStatusTimer(refreshed.Local, 40),
            "stronger Burn publishes its refreshed real timer");

        await using (var viewer =
                     await RuntimePolicySessionSocket.CreateAsync())
        {
            var observer = Player(8_099, 8_099, "BurnViewer", 0, 0);
            fixture.Registry.JoinPlayerMap(
                viewer.Session,
                observer.AccountId,
                observer);
            var target = fixture.Registry.GetMapSessions(0)
                .Single(context =>
                    context.CharacterId == fixture.Target.Id);
            await fixture.Registry.SendStatusSnapshotToViewerAsync(
                target,
                viewer.Session,
                CancellationToken.None);
            var late = await viewer.ReadPacketAsync(340);
            AssertStatusSnapshot(
                late,
                fixture.TargetObjectId,
                [40, 204],
                "late viewer Burn snapshot");
            fixture.Registry.Remove(viewer.Session);
        }

        Check.Equal(
            120L,
            ConsumeBurn(elemental, now.AddSeconds(1)),
            "detonation consumes the authoritative remaining Burn");
        Check.Equal(
            1,
            await fixture.Registry
                .ReconcileTrainingDummyElementalStatusesOnceAsync(
                    now.AddSeconds(1),
                    CancellationToken.None),
            "detonation publishes a Burn clear");
        var detonated = await ReadStatusPairAsync(fixture);
        AssertStatusSnapshot(
            detonated.Local,
            LocalPlayerObjectId,
            [204],
            "detonation clear");

        Check.True(
            ApplyBurn(
                elemental,
                TrainingBurn(
                    fixture.Attacker,
                    fixture.Target,
                    eventId: 4,
                    now.AddSeconds(2),
                    totalDamage: 80)),
            "final-tick Burn fixture applies");
        await fixture.Registry.ReconcileTrainingDummyElementalStatusesOnceAsync(
            now.AddSeconds(2),
            CancellationToken.None);
        await ReadStatusPairAsync(fixture);
        lock (elemental.Gate)
        {
            Check.Equal(
                4,
                elemental.Statuses.CollectDuePeriodicDamage(
                    now.AddSeconds(6).ToUnixTimeMilliseconds()).Count,
                "final deadline drains every Burn tick");
        }
        await fixture.Registry.ReconcileTrainingDummyElementalStatusesOnceAsync(
            now.AddSeconds(6),
            CancellationToken.None);
        var finalTick = await ReadStatusPairAsync(fixture);
        AssertStatusSnapshot(
            finalTick.Local,
            LocalPlayerObjectId,
            [204],
            "final-tick clear");

        Check.True(
            ApplyBurn(
                elemental,
                TrainingBurn(
                    fixture.Attacker,
                    fixture.Target,
                    eventId: 5,
                    now.AddSeconds(7),
                    totalDamage: 80)),
            "death-clear Burn fixture applies");
        await fixture.Registry.ReconcileTrainingDummyElementalStatusesOnceAsync(
            now.AddSeconds(7),
            CancellationToken.None);
        await ReadStatusPairAsync(fixture);
        fixture.Registry.AdvancePlayerLifeRevision(
            fixture.TargetSocket.Session,
            now.AddSeconds(7));
        await fixture.Registry.ReconcileTrainingDummyElementalStatusesOnceAsync(
            now.AddSeconds(7),
            CancellationToken.None);
        var death = await ReadStatusPairAsync(fixture);
        AssertStatusSnapshot(
            death.Local,
            LocalPlayerObjectId,
            [204],
            "death clear");

        Check.True(
            ApplyBurn(
                elemental,
                TrainingBurn(
                    fixture.Attacker,
                    fixture.Target,
                    eventId: 6,
                    now.AddSeconds(8),
                    totalDamage: 80)),
            "policy-loss Burn fixture applies");
        await fixture.Registry.ReconcileTrainingDummyElementalStatusesOnceAsync(
            now.AddSeconds(8),
            CancellationToken.None);
        await ReadStatusPairAsync(fixture);
        fixture.Target.PositionX = 149f;
        await fixture.Registry.ReconcileTrainingDummyElementalStatusesOnceAsync(
            now.AddSeconds(8),
            CancellationToken.None);
        var moved = await ReadStatusPairAsync(fixture);
        AssertStatusSnapshot(
            moved.Local,
            LocalPlayerObjectId,
            [204],
            "exact-policy loss clear");
        fixture.Target.PositionX = 148f;
        ConsumeBurn(elemental, now.AddSeconds(8));

        Check.True(
            ApplyBurn(
                elemental,
                TrainingBurn(
                    fixture.Attacker,
                    fixture.Target,
                    eventId: 7,
                    now.AddSeconds(9),
                    totalDamage: 80)),
            "reconnect-clear Burn fixture applies");
        await fixture.Registry.ReconcileTrainingDummyElementalStatusesOnceAsync(
            now.AddSeconds(9),
            CancellationToken.None);
        await ReadStatusPairAsync(fixture);
        fixture.Registry.Remove(
            fixture.TargetSocket.Session,
            preservePlayerStatus: true);
        fixture.Registry.JoinPlayerMap(
            fixture.TargetSocket.Session,
            fixture.Target.AccountId,
            fixture.Target);
        await fixture.Registry.ReconcileTrainingDummyElementalStatusesOnceAsync(
            now.AddSeconds(9),
            CancellationToken.None);
        var reconnect = await ReadStatusPairAsync(fixture);
        AssertStatusSnapshot(
            reconnect.Local,
            LocalPlayerObjectId,
            [204],
            "reconnect clear");

        await CheckOrdinaryPlayerBurnProjectionFenceAsync(now);
    }

    private static async Task CheckOrdinaryPlayerBurnProjectionFenceAsync(
        DateTimeOffset now)
    {
        await using var ordinary = await Fixture.CreateAsync(
            target: Player(8_098, 8_098, "OrdinaryBurn", 0, 1),
            bindElementalOwnership: true);
        var elemental = InitializeElementalState(
            ordinary.Registry,
            ordinary.TargetSocket.Session,
            ordinary.Target);
        Check.True(
            ApplyBurn(
                elemental,
                TrainingBurn(
                    ordinary.Attacker,
                    ordinary.Target,
                    eventId: 8,
                    now,
                    totalDamage: 80)) &&
            await ordinary.Registry
                .ReconcileTrainingDummyElementalStatusesOnceAsync(
                    now,
                    CancellationToken.None) == 0,
            "ordinary-player Burn remains outside dummy projection");
        var target = ordinary.Registry.GetMapSessions(0)
            .Single(context => context.CharacterId == ordinary.Target.Id);
        await ordinary.Registry.SendStatusSnapshotToViewerAsync(
            target,
            ordinary.AttackerSocket.Session,
            CancellationToken.None);
        var packet = await ordinary.AttackerSocket.ReadPacketAsync(340);
        AssertStatusSnapshot(
            packet,
            ordinary.TargetObjectId,
            [],
            "ordinary-player late-viewer fence");
    }

    private static GameSessionRegistry.ElementalCombatSessionState
        InitializeElementalState(
            GameSessionRegistry registry,
            ClientSession session,
            GameCharacter character)
    {
        var fence = new ElementalCombatSessionFence(
            character.Id,
            character.CurrentMap,
            new PlayerOwnershipFence(
                character.CheckpointOwnerId,
                character.CheckpointOwnerGeneration));
        Check.True(
            registry.TryGetElementalStatusAdjustment(
                session,
                fence,
                0,
                0,
                0,
                0,
                0,
                0,
                out _),
            "training fixture creates target-owned elemental authority");
        return ElementalState(registry, session);
    }

    private static ElementalEffectApplication TrainingBurn(
        GameCharacter source,
        GameCharacter target,
        ulong eventId,
        DateTimeOffset appliedAt,
        long totalDamage) =>
        new(
            ElementKind.Fire,
            ElementalEffectKind.Burn,
            source.Id,
            target.Id,
            eventId,
            appliedAt.ToUnixTimeMilliseconds(),
            appliedAt.AddSeconds(4).ToUnixTimeMilliseconds(),
            EffectivePotencyBasisPoints: 1_000,
            ApplicationChanceBasisPoints: 10_000,
            TargetResistanceBasisPoints: 0,
            totalDamage,
            PeriodicTickCount: 4,
            CombatEventProvenance.ElementalStatus);

    private static bool ApplyBurn(
        GameSessionRegistry.ElementalCombatSessionState elemental,
        ElementalEffectApplication application)
    {
        lock (elemental.Gate)
        {
            return elemental.Statuses.TryApply(application);
        }
    }

    private static long ConsumeBurn(
        GameSessionRegistry.ElementalCombatSessionState elemental,
        DateTimeOffset now)
    {
        lock (elemental.Gate)
        {
            return elemental.Statuses.ConsumeRemainingBurn(
                now.ToUnixTimeMilliseconds());
        }
    }

    private static async Task<(byte[] Local, byte[] Remote)>
        ReadStatusPairAsync(Fixture fixture) =>
        (
            await fixture.TargetSocket.ReadPacketAsync(340),
            await fixture.AttackerSocket.ReadPacketAsync(340));

    private static void AssertStatusSnapshot(
        byte[] packet,
        uint objectId,
        IReadOnlyList<uint> expectedIds,
        string description)
    {
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(8, 4)));
        var actual = Enumerable.Range(0, count)
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(12 + (index * sizeof(uint)), sizeof(uint))))
            .ToArray();
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2, 2)) == 0x27B7 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(4, 4)) == objectId &&
            actual.SequenceEqual(expectedIds),
            $"{description} carries the complete expected status map");
    }

    private static uint ReadStatusTimer(byte[] packet, uint statusId)
    {
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(8, 4)));
        for (var index = 0; index < count; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.AsSpan(
                        12 + (index * sizeof(uint)),
                        sizeof(uint))) == statusId)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.AsSpan(
                        92 + (index * sizeof(uint)),
                        sizeof(uint)));
            }
        }

        throw new InvalidOperationException(
            $"Status {statusId} was not present.");
    }
}
