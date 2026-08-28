using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummyHostileStatusSkillChecks
{
    private static void CheckClientProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var expose = Required(84);
        var injury = Required(334);
        var snapshot = new TrainingDummyHostileStatusSnapshot(
            CharacterId: 7001,
            Revision: 2,
            ActiveStatuses:
            [
                Active(expose, now, revision: 1),
                Active(injury, now, revision: 2)
            ]);
        var overlay = TrainingDummyHostileStatusClientProjection.Create(
            snapshot,
            now);
        var baseline = new PlayerStatusSnapshot(
            [new ClientStatusEffect(204, 600)],
            ClientStatusAggregate.Empty with { Hit = 60 },
            "baseline");
        var merged = TrainingDummyHostileStatusClientProjection.Merge(
            baseline,
            overlay);
        Check.True(
            merged.Effects.Select(static effect => effect.StatusId)
                .SequenceEqual(new uint[] { 104, 133, 204 }) &&
            merged.Aggregate.Hit == 60 &&
            merged.Aggregate.PhysicalDefense == -400 &&
            merged.Aggregate.MagicDefense == -300 &&
            merged.Fingerprint.Contains(
                overlay.Fingerprint,
                StringComparison.Ordinal),
            "hostile overlay preserves baseline status data and adds exact stock effects");

        ClientStatusEffect[] fullBaselineEffects =
        [
            new(ElementalClientStatusProjection.BurnStatusId, 4),
            .. Enumerable.Range(200, 19).Select(static id =>
                new ClientStatusEffect(checked((uint)id), 60))
        ];
        var fullBaseline = new PlayerStatusSnapshot(
            fullBaselineEffects,
            ClientStatusAggregate.Empty with
            {
                PhysicalDefense = 23,
                MagicDefense = 31
            },
            "full-with-elemental")
        {
            Presentations = fullBaselineEffects.Select(effect =>
                new ClientStatusPresentation(
                    effect,
                    Beneficial: false,
                    Priority: 0,
                    effect.StatusId ==
                        ElementalClientStatusProjection.BurnStatusId
                            ? ClientStatusPresentationClass.DisplayOnly
                            : ClientStatusPresentationClass
                                .AuthoritativeBaseline))
                .ToArray()
        };
        var fullMerged = TrainingDummyHostileStatusClientProjection.Merge(
            fullBaseline,
            overlay);
        Check.True(
            fullMerged.Effects.Count ==
                PlayerStatusComposer.MaximumTotalStatuses &&
            fullMerged.Effects.Any(static effect =>
                effect.StatusId == 104) &&
            fullMerged.Effects.Any(static effect =>
                effect.StatusId == 133) &&
            fullMerged.Effects.All(static effect =>
                effect.StatusId !=
                    ElementalClientStatusProjection.BurnStatusId) &&
            fullMerged.Aggregate.PhysicalDefense == -377 &&
            fullMerged.Aggregate.MagicDefense == -269,
            "a full snapshot deterministically reserves hostile icons, evicts the lower-priority elemental presentation, and applies only admitted hostile aggregates");

        var character = TrainingDummyHostileStatusTestFixture.CreateDummy();
        var packet = PacketBuilder.PlayerStatusEffects(
            character,
            objectId: 77,
            merged.Effects,
            merged.Aggregate);
        Check.True(
            packet.Length == 340 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2, 2)) == 0x27B7 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(4, 4)) == 77 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(8, 4)) == 3 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(12, 4)) == 104 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(16, 4)) == 133 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.AsSpan(192, 4)) == 600 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.AsSpan(200, 4)) == 700,
            "hostile effects use the complete player 0x27B7 layout and derived defense fields");

        var stun = Required(74);
        var controlOverlay =
            TrainingDummyHostileStatusClientProjection.Create(
                new TrainingDummyHostileStatusSnapshot(
                    CharacterId: 7001,
                    Revision: 3,
                    ActiveStatuses:
                    [
                        Active(stun, now, revision: 3)
                    ]),
                now);
        var controlled = TrainingDummyHostileStatusClientProjection.Merge(
            new PlayerStatusSnapshot(
                [],
                ClientStatusAggregate.Empty,
                "control-baseline"),
            controlOverlay);
        var controlledPacket = PacketBuilder.PlayerStatusEffects(
            character,
            objectId: 77,
            controlled.Effects,
            controlled.Aggregate);
        var fullControl =
            HostileStatusControlFlags.HaltIntonate |
            HostileStatusControlFlags.NonMoving |
            HostileStatusControlFlags.NonMagicUsing |
            HostileStatusControlFlags.NonTechniqueUsing |
            HostileStatusControlFlags.NonAttackUsing |
            HostileStatusControlFlags.NonItemUsing;
        Check.True(
            controlled.Aggregate.Control == fullControl &&
            BinaryPrimitives.ReadSingleLittleEndian(
                controlledPacket.AsSpan(248, 4)) == 1f &&
            BinaryPrimitives.ReadSingleLittleEndian(
                controlledPacket.AsSpan(252, 4)) == 0f &&
            BinaryPrimitives.ReadSingleLittleEndian(
                controlledPacket.AsSpan(256, 4)) == 1f &&
            BinaryPrimitives.ReadSingleLittleEndian(
                controlledPacket.AsSpan(260, 4)) == 1f &&
            BinaryPrimitives.ReadSingleLittleEndian(
                controlledPacket.AsSpan(264, 4)) == 1f &&
            BinaryPrimitives.ReadSingleLittleEndian(
                controlledPacket.AsSpan(268, 4)) == 1f &&
            BinaryPrimitives.ReadSingleLittleEndian(
                controlledPacket.AsSpan(272, 4)) == 1f,
            "native StatusData serializes all six action controls as floats at the original client offsets");
    }

    private static ActiveTrainingDummyHostileStatus Active(
        HostileStatusEffectDefinition definition,
        DateTimeOffset now,
        long revision) =>
        new(
            definition,
            now,
            now + definition.Duration,
            SourceEventId: checked((ulong)revision),
            SourceTargetOrder: 0,
            SourceCharacterId: 8801,
            revision);
}
