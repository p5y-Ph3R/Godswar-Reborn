using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private const ushort MedusaVisualOpcode = 0x2738;
    private const ushort MedusaImpactOpcode = 0x273E;
    private const ushort MedusaPhysicalDamageOpcode = 0x272A;
    private const ushort MedusaPlayerDeathOpcode = 0x2722;
    private const ushort MedusaStatusOpcode = 10167;

    private static async Task CheckMedusaStatusPublicationAsync()
    {
        await CheckAllMedusaNativeStatusPacketsAsync();
        await CheckMedusaFallbackImpactPacketsAsync();
        await CheckMedusaCommittedBleedPrefixAsync();
        await CheckMedusaObserverStatusRoutingAsync();
        await CheckMedusaApplicationTimeInterruptionAsync();
        await CheckMedusaRunTerminalClearsActiveAmplifierAsync();
        await CheckMedusaStatusPublicationRacesAsync();
    }

    private static async Task CheckAllMedusaNativeStatusPacketsAsync()
    {
        var cases = new (string SpawnId, uint SkillId, uint StatusId,
            uint Duration)[]
        {
            ("E1-Elite", 2002, 330, 2),
            ("E5-Elite", 2018, 402, 3),
            ("Euryale", 2017, 401, 3),
            ("Final-Pikeman-1", 2082, 236, 30),
            ("Final-Axeman-1", 2080, 235, 30)
        };
        var seed = 7_000_000UL;
        foreach (var item in cases)
        {
            await using var fixture =
                await MonsterPlayerHitFixture.CreateAsync(item.SpawnId);
            await DrainMedusaPacketsAsync(fixture.Socket);
            var eventId = fixture.FindEvent(
                seed,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            seed += 100_000;

            _ = await fixture.AttackAsync(
                fixture.CreateAttack(eventId));
            var impact = await fixture.Socket.ReadPacketAsync();
            var basicImpact = await fixture.Socket.ReadPacketAsync();
            var damage = await fixture.Socket.ReadPacketAsync();
            var status = await fixture.Socket.ReadPacketAsync();
            Check.True(
                MedusaPacketOpcode(impact) == MedusaImpactOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    impact.AsSpan(12)) == item.SkillId &&
                MedusaPacketOpcode(basicImpact) == MedusaImpactOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    basicImpact.AsSpan(12)) == 2000 &&
                MedusaPacketOpcode(damage) ==
                    MedusaPhysicalDamageOpcode &&
                MedusaPacketOpcode(status) == MedusaStatusOpcode &&
                MedusaStatusDuration(status, item.StatusId) ==
                    item.Duration,
                $"{item.SpawnId} publishes skill {item.SkillId} impact, basic impact, damage, then complete status {item.StatusId}");
        }
    }

    private static async Task CheckMedusaFallbackImpactPacketsAsync()
    {
        await using (var zero =
                     await MonsterPlayerHitFixture.CreateAsync("E5-Elite"))
        {
            await DrainMedusaPacketsAsync(zero.Socket);
            ulong eventId = 0;
            for (ulong candidate = 7_700_000;
                 candidate < 7_800_000;
                 candidate++)
            {
                var resolution = zero.Resolve(candidate);
                if (resolution.Hit &&
                    resolution.Damage > 0 &&
                    !zero.AuthoredEffectProcApplies(candidate))
                {
                    eventId = candidate;
                    break;
                }
            }
            Check.True(
                eventId != 0,
                "Gorgon Shaman has a deterministic normal-attack event");
            var attack = zero.CreateAttack(eventId) with
            {
                TargetX = zero.Source.X + 17f,
                TargetZ = zero.Source.Z - 19f
            };
            _ = await zero.AttackAsync(attack);
            var impact = await zero.Socket.ReadPacketAsync();
            var basicImpact = await zero.Socket.ReadPacketAsync();
            var damage = await zero.Socket.ReadPacketAsync();
            Check.True(
                MedusaPacketOpcode(impact) == MedusaImpactOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    impact.AsSpan(12)) == 2000 &&
                MedusaPacketOpcode(basicImpact) == MedusaImpactOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    basicImpact.AsSpan(12)) == 2000 &&
                BinaryPrimitives.ReadSingleLittleEndian(
                    impact.AsSpan(16)) == attack.TargetX &&
                BinaryPrimitives.ReadSingleLittleEndian(
                    impact.AsSpan(20)) == attack.TargetZ &&
                MedusaPacketOpcode(damage) ==
                    MedusaPhysicalDamageOpcode &&
                zero.Socket.Available == 0,
                "Gorgon Shaman normal attack emits the captured pair of skill-2000 impacts at the target, followed by damage and no unsupported cast visual");
        }

        await using (var lethal =
                     await MonsterPlayerHitFixture.CreateAsync("E1-Elite"))
        {
            await DrainMedusaPacketsAsync(lethal.Socket);
            lethal.SetHealth(1);
            var eventId = lethal.FindEvent(
                7_800_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await lethal.AttackAsync(lethal.CreateAttack(eventId));
            var impact = await lethal.Socket.ReadPacketAsync();
            var basicImpact = await lethal.Socket.ReadPacketAsync();
            var damage = await lethal.Socket.ReadPacketAsync();
            var death = await lethal.Socket.ReadPacketAsync();
            var cleanup = await lethal.Socket.ReadPacketAsync();
            Check.True(
                MedusaPacketOpcode(impact) == MedusaImpactOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    impact.AsSpan(12)) == 2000 &&
                MedusaPacketOpcode(basicImpact) == MedusaImpactOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    basicImpact.AsSpan(12)) == 2000 &&
                MedusaPacketOpcode(damage) ==
                    MedusaPhysicalDamageOpcode &&
                MedusaPacketOpcode(death) == MedusaPlayerDeathOpcode &&
                MedusaPacketOpcode(cleanup) == MedusaStatusOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    cleanup.AsSpan(8)) == 0,
                "lethal acceptance retains both captured skill-2000 impacts and sends an exact complete status clear");
        }
    }

    private static async Task CheckMedusaObserverStatusRoutingAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            observerSocket.Session,
            observer);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);

        try
        {
            var eventId = fixture.FindEvent(
                7_900_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await fixture.AttackAsync(
                fixture.CreateAttack(eventId));
            var self = await ReadMedusaAttackStatusSequenceAsync(
                fixture.Socket);
            var world = await ReadMedusaAttackStatusSequenceAsync(
                observerSocket);
            Check.True(
                self.SkillId == 2017 &&
                world.SkillId == 2017 &&
                self.StatusId == 401 &&
                world.StatusId == 401 &&
                self.TargetObjectId ==
                    MedusaHandlerLocalObjectId &&
                world.TargetObjectId == fixture.Context.ObjectId,
                "the exact target and same-instance observer receive impact, damage, then the same complete Shackle projection with local/world identities");
        }
        finally
        {
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task CheckMedusaApplicationTimeInterruptionAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            observerSocket.Session,
            observer);
        var store = new MedusaHandlerStore(fixture.Character);
        var handler = CreateMedusaHandler(
            fixture.Socket.Session,
            fixture.Registry,
            fixture.Character,
            store);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            fixture.Socket.Session,
            fixture.Character);
        RegisterMedusaCastInterruption(handler);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);

        try
        {
            await InvokeMedusaPacketAsync(
                handler,
                MedusaSkillPacket(fixture.Character, fixture.Source));
            var castStart = await fixture.Socket.ReadPacketAsync();
            var worldCastStart = await observerSocket.ReadPacketAsync();
            Check.True(
                MedusaPacketOpcode(castStart) == Opcodes.SkillCast &&
                MedusaPacketOpcode(worldCastStart) == Opcodes.SkillCast,
                "real handler publishes the pending cast start to self and observer");

            var eventId = fixture.FindEvent(
                8_000_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await fixture.AttackAsync(
                fixture.CreateAttack(eventId));
            var self = await ReadMedusaInterruptedSequenceAsync(
                fixture.Socket,
                localTarget: true,
                fixture.Source.ObjectId,
                MedusaHandlerLocalObjectId,
                expectedSkillId: 2002,
                expectedStatusId: 330,
                expectedDuration: 2,
                PacketBuilder.PlayerStatusUpdate(
                    fixture.Character,
                    ClientStatusAggregate.Empty));
            var world = await ReadMedusaInterruptedSequenceAsync(
                observerSocket,
                localTarget: false,
                fixture.Source.ObjectId,
                fixture.Context.ObjectId,
                expectedSkillId: 2002,
                expectedStatusId: 330,
                expectedDuration: 2,
                expectedLocalGameData: null);
            Check.True(
                self.StatusId == 330 &&
                world.StatusId == 330 &&
                self.InterruptObjectId ==
                    MedusaHandlerLocalObjectId &&
                world.InterruptObjectId == fixture.Context.ObjectId &&
                !MedusaHasPendingCast(handler),
                "application-time stun publishes impact and damage, then exact 10167, then one exact 10171 to self and observer before releasing the generation");
        }
        finally
        {
            UnregisterMedusaCastInterruption(handler);
            await StopMedusaPendingCastsAsync(handler);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task<MedusaAttackStatusSequence>
        ReadMedusaAttackStatusSequenceAsync(
            RuntimePolicySessionSocket socket)
    {
        var impact = await socket.ReadPacketAsync();
        var basicImpact = await socket.ReadPacketAsync();
        var damage = await socket.ReadPacketAsync();
        var status = await socket.ReadPacketAsync();
        Check.True(
            MedusaPacketOpcode(impact) == MedusaImpactOpcode &&
            MedusaPacketOpcode(basicImpact) == MedusaImpactOpcode &&
            MedusaPacketOpcode(damage) == MedusaPhysicalDamageOpcode &&
            MedusaPacketOpcode(status) == MedusaStatusOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                impact.AsSpan(4)) ==
            BinaryPrimitives.ReadUInt32LittleEndian(
                basicImpact.AsSpan(4)) &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                impact.AsSpan(8)) ==
            BinaryPrimitives.ReadUInt32LittleEndian(
                basicImpact.AsSpan(8)) &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                impact.AsSpan(4)) ==
            BinaryPrimitives.ReadUInt32LittleEndian(
                damage.AsSpan(4)) &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                impact.AsSpan(8)) ==
            BinaryPrimitives.ReadUInt32LittleEndian(
                damage.AsSpan(20)) &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                basicImpact.AsSpan(12)) == 2000 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                impact.AsSpan(8)) ==
            BinaryPrimitives.ReadUInt32LittleEndian(
                status.AsSpan(4)) &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                impact.AsSpan(4)) ==
            BinaryPrimitives.ReadUInt32LittleEndian(
                basicImpact.AsSpan(4)),
            "Medusa attack packet order is skill impact, basic impact, damage, complete status");
        var statusId = MedusaFirstStatusId(status);
        return new(
            BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(8)),
            statusId,
            MedusaStatusDuration(status, statusId),
            status);
    }

    private static async Task<MedusaInterruptedSequence>
        ReadMedusaInterruptedSequenceAsync(
            RuntimePolicySessionSocket socket,
            bool localTarget,
            uint expectedSourceObjectId,
            uint expectedTargetObjectId,
            uint expectedSkillId,
            uint expectedStatusId,
            uint expectedDuration,
        byte[]? expectedLocalGameData)
    {
        var attack = await ReadMedusaAttackStatusSequenceAsync(socket);
        var corrective = await socket.ReadPacketAsync();
        byte[]? gameData = null;
        if (localTarget)
        {
            gameData = await socket.ReadPacketAsync();
        }
        var interrupted = await socket.ReadPacketAsync();
        await Task.Delay(25);
        Check.True(
            attack.SourceObjectId == expectedSourceObjectId &&
            attack.TargetObjectId == expectedTargetObjectId &&
            attack.SkillId == expectedSkillId &&
            attack.StatusId == expectedStatusId &&
            attack.StatusDuration == expectedDuration &&
            MedusaPacketOpcode(corrective) == MedusaStatusOpcode &&
            corrective.AsSpan().SequenceEqual(
                attack.StatusPacket) &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                corrective.AsSpan(4)) == expectedTargetObjectId &&
            MedusaStatusDuration(
                corrective,
                expectedStatusId) == expectedDuration &&
            (!localTarget ||
             gameData is not null &&
             MedusaPacketOpcode(gameData) == 10166 &&
             expectedLocalGameData is not null &&
             gameData.AsSpan().SequenceEqual(expectedLocalGameData)) &&
            MedusaPacketOpcode(interrupted) ==
                Opcodes.SkillCastInterrupt &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                interrupted.AsSpan(4)) == expectedTargetObjectId &&
            socket.Available == 0,
            localTarget
                ? "self FIFO is skill impact, basic impact, damage, initial 10167, corrective 10167, 10166, one 10171"
                : "observer FIFO is skill impact, basic impact, damage, initial 10167, corrective 10167, one 10171");
        return new(
            attack.StatusId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                interrupted.AsSpan(4)));
    }

    private static async Task DrainMedusaPacketsAsync(
        RuntimePolicySessionSocket socket)
    {
        while (socket.Available > 0)
        {
            _ = await socket.ReadPacketAsync();
        }
    }

    private static ushort MedusaPacketOpcode(ReadOnlySpan<byte> packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);

    private static uint MedusaFirstStatusId(ReadOnlySpan<byte> packet) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]);

    private static uint MedusaStatusDuration(
        ReadOnlySpan<byte> packet,
        uint statusId)
    {
        var count = checked((int)BinaryPrimitives
            .ReadUInt32LittleEndian(packet[8..]));
        for (var index = 0; index < count; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(
                    packet[(12 + (index * 4))..]) == statusId)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(
                    packet[(92 + (index * 4))..]);
            }
        }

        return 0;
    }

    private readonly record struct MedusaAttackStatusSequence(
        uint SourceObjectId,
        uint SkillId,
        uint TargetObjectId,
        uint StatusId,
        uint StatusDuration,
        byte[] StatusPacket);

    private readonly record struct MedusaInterruptedSequence(
        uint StatusId,
        uint InterruptObjectId);
}
