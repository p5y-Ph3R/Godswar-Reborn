using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaFactionAnnouncementChecks
{
    public const string CheckName =
        "Medusa faction completion announcement";

    public static async Task RunAsync()
    {
        await using var winnerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var factionSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var otherFactionSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = new GameSessionRegistry();

        var winner = Character(
            characterId: 91_001,
            accountId: 9_101,
            "MedusaWinner",
            GameDefaults.SpartaCamp,
            GameDefaults.SpartaCapitalMap);
        var factionMember = Character(
            characterId: 91_002,
            accountId: 9_102,
            "SpartanObserver",
            GameDefaults.SpartaCamp,
            GameDefaults.SpartaCapitalMap);
        var otherFactionMember = Character(
            characterId: 91_003,
            accountId: 9_103,
            "AthenianObserver",
            GameDefaults.AthensCamp,
            GameDefaults.AthensCapitalMap);

        Join(registry, winnerSocket, winner);
        Join(registry, factionSocket, factionMember);
        Join(registry, otherFactionSocket, otherFactionMember);

        Check.True(
            MedusaCompletionRewardPolicy.TryResolve(
                MedusaEncounterDifficulty.Enhanced,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(10),
                out var award),
            "faction-announcement fixture resolves an authored title award");
        var receipt = new MedusaCompletionRewardReceipt(
            MedusaCompletionRewardStatus.Applied,
            WorldInstanceId.New(),
            award,
            [
                new MedusaCompletionRewardMember(
                    winner.Id,
                    winner.Camp,
                    HonorBefore: 0,
                    HonorAfter: award.HardPoints,
                    RewardRevision: 1,
                    AwardedTitleId: award.AwardedTitleId)
            ]);

        var delivered = await registry.PublishMedusaFactionNoticeAsync(
            receipt,
            winner.Name,
            CancellationToken.None);

        Check.Equal(
            2,
            delivered,
            "announcement reaches every online member of the rewarded faction");
        var expectedMessage =
            GameSessionRegistry.BuildMedusaFactionAnnouncement(
                winner.Name,
                award.NotificationText);
        Check.True(
            expectedMessage.StartsWith(
                "MedusaWinner's Team ",
                StringComparison.Ordinal),
            "announcement identifies the winning team by leader name");
        var crimsonHeirMessage =
            GameSessionRegistry.BuildMedusaFactionAnnouncement(
                winner.Name,
                "The team has defeated Medusa within 10 minutes and " +
                "earned the title of 'Heir of Perseus'.");
        Check.True(
            crimsonHeirMessage.Contains(
                "|cffDC143C'Heir of Perseus'|cFFFFFFFF",
                StringComparison.Ordinal),
            "Heir of Perseus is crimson in the faction announcement");
        Check.True(
            crimsonHeirMessage.Length <=
                PacketBuilder.CenteredAnnouncementMaximumTextLength,
            "the crimson title markup remains within the native packet");
        var boundedLongNameMessage =
            GameSessionRegistry.BuildMedusaFactionAnnouncement(
                new string('X', 32),
                "The team has defeated Medusa within 15 minutes and " +
                "earned the title of 'Bane of the Three Sisters'.");
        Check.True(
            boundedLongNameMessage.Length <=
                PacketBuilder.CenteredAnnouncementMaximumTextLength,
            "maximum-length player names retain a native bounded announcement");
        var expected = PacketBuilder.CenteredAnnouncement(expectedMessage);
        Check.Equal(
            137,
            expected.Length,
            "announcement uses the exact native MSG_PYTHON_NOTE frame size");
        Check.Equal(
            Opcodes.PythonNote,
            BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(2)),
            "announcement uses the native client writing opcode");
        Check.Equal(
            50,
            BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(4)),
            "announcement selects the client's direct-text formatter");
        Check.Equal(
            (byte)0,
            expected[8],
            "announcement selects the center-screen proclamation channel");
        Check.Equal(
            expectedMessage,
            PacketText.ReadFixedAscii(expected, 9, 64) +
            PacketText.ReadFixedAscii(expected, 73, 64),
            "the two native fields reconstruct the exact authored message");
        Check.True(
            (await winnerSocket.ReadPacketAsync()).SequenceEqual(expected),
            "the rewarded player receives the exact title announcement");
        Check.True(
            (await factionSocket.ReadPacketAsync()).SequenceEqual(expected),
            "another online faction member receives the exact title announcement");
        Check.Equal(
            0,
            otherFactionSocket.Available,
            "the opposite faction receives no announcement");
    }

    private static GameCharacter Character(
        int characterId,
        int accountId,
        string name,
        byte camp,
        byte mapId) => new()
    {
        Id = characterId,
        AccountId = accountId,
        Name = name,
        Camp = camp,
        CurrentMap = mapId,
        RealmId = RealmId.Tempest
    };

    private static void Join(
        GameSessionRegistry registry,
        RuntimePolicySessionSocket socket,
        GameCharacter character)
    {
        GameHandlerOwnershipTestFences.Bind(
            registry,
            socket.Session,
            character.AccountId,
            character);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            worldReady: true,
            joinedAt: DateTimeOffset.UtcNow);
    }
}
