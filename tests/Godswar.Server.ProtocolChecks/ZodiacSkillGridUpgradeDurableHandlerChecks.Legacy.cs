using System.Buffers.Binary;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    ZodiacSkillGridUpgradeDurableHandlerChecks
{
    private static async Task
        CheckRawUuidCannotCreateDurableCommandAsync()
    {
        var store = new ZodiacUpgradeCompatibilityStore();
        var executor = new CapturingExecutor(
            (_, _) => throw new InvalidOperationException(
                "raw UUID reached durable executor"));
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var registry = new GameSessionRegistry(store);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            zodiacSkillGridUpgradeCommands: executor);
        SetField(
            handler,
            "_account",
            new Godswar.Server.Application.Accounts.AccountIdentity(
                AccountId,
                "raw-zodiac-uuid-check"));
        SetField(handler, "_character", CreateCharacter());

        await InvokeAsync(
            handler,
            CreateUpgradePacket(OperationId));

        Check.Equal(
            0,
            executor.Count,
            "raw UUID cannot create a secure command envelope");
        Check.Equal(
            0,
            store.UpgradeCount,
            "raw UUID cannot fall back to compatibility mutation");
        Check.Equal(
            0,
            transport.WrittenBytes.Length,
            "raw UUID receives no unauthenticated terminal result");
    }

    private static async Task
        CheckSecureTokenlessRequestFailsClosedAsync()
    {
        await using var fixture = CreateFixture(
            ZodiacSkillGridUpgradeExecutionResult
                .PreconditionFailed());

        await InvokeAsync(
            fixture.Handler,
            CreateUpgradePacket());

        Check.Equal(
            0,
            fixture.Store.UpgradeCount,
            "secure tokenless Zodiac request cannot use compatibility");
        Check.Equal(
            0,
            fixture.Executor!.Count,
            "secure tokenless Zodiac request cannot execute durably");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "secure tokenless Zodiac request fails closed without response");
    }

    private static async Task
        CheckRawTokenlessRequestUsesCompatibilityAsync()
    {
        var store = new ZodiacUpgradeCompatibilityStore
        {
            Result = new ZodiacSkillGridUpgradeResult(
                ZodiacSkillGridUpgradeStatus.Succeeded,
                GridIndex,
                PreviousLevel: 1,
                CurrentLevel: 2,
                RequiredZodiacLevel: 1,
                EnergyCost: 5,
                TalentPointCost: 7,
                CurrentEnergy: 995,
                CurrentEnergyRemainderX100: 50,
                CurrentTalentPoints: 883,
                SelectedSkillId: 10_050)
        };
        var executor = new CapturingExecutor(
            (_, _) => throw new InvalidOperationException(
                "raw tokenless request reached durable executor"));
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var registry = new GameSessionRegistry(store);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            zodiacSkillGridUpgradeCommands: executor);
        SetField(
            handler,
            "_account",
            new Godswar.Server.Application.Accounts.AccountIdentity(
                AccountId,
                "raw-tokenless-zodiac-check"));
        SetField(handler, "_character", CreateCharacter());

        await InvokeAsync(
            handler,
            CreateUpgradePacket());

        Check.Equal(
            1,
            store.UpgradeCount,
            "raw tokenless Zodiac request uses compatibility store");
        Check.Equal(
            0,
            executor.Count,
            "raw tokenless Zodiac request skips durable executor");
        AssertLegacyPacketShape(
            ReadRawLegacyPackets(transport),
            expectedUpgradeAcknowledgements: 1,
            "raw tokenless compatibility Zodiac upgrade");
    }

    private static IReadOnlyList<byte[]> ReadRawLegacyPackets(
        ScriptedLegacyByteTransport transport)
    {
        var encrypted = transport.WrittenBytes;
        new PacketCipher().Transform(encrypted);
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < encrypted.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                encrypted.AsSpan(offset, sizeof(ushort)));
            if (length < 4 ||
                length > encrypted.Length - offset)
            {
                throw new InvalidDataException(
                    "Raw Zodiac response has an invalid frame.");
            }
            packets.Add(encrypted.AsSpan(offset, length).ToArray());
            offset += length;
        }
        return packets;
    }
}
