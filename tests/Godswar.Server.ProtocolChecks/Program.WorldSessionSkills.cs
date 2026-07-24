using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckWorldSessionOwnedBoostClockAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-world-boost-clock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int accountId;
            GameCharacter character;
            await using (var seedStore = new JsonGameStore(dataPath))
            {
                await seedStore.EnsureSeedDataAsync();
                var account = await seedStore.LoginOrCreateAccountAsync("world-boost-clock", "");
                character = await seedStore.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "WorldBoostHero",
                        Camp = GameDefaults.SpartaCamp
                    });
                accountId = account.Id;
            }

            var joinedAt = new DateTimeOffset(2026, 7, 20, 1, 0, 0, TimeSpan.Zero);
            var statePath = Path.Combine(dataPath, "state.json");
            var stateJson = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
                ?? throw new InvalidOperationException("JSON world boost-clock test could not parse state.json");
            stateJson["characterExperienceBoosts"] = new JsonArray
            {
                new JsonObject
                {
                    ["characterId"] = character.Id,
                    ["statusId"] = ExperienceStatusIds.MaxExperiencePotion,
                    ["kind"] = ExperienceBoostKinds.Consumable,
                    ["bonusBasisPoints"] = 30_000,
                    ["priority"] = 11,
                    ["activatedAt"] = joinedAt.AddDays(-1),
                    ["expiresAt"] = joinedAt.AddDays(-1).AddSeconds(1_000),
                    ["source"] = "world-clock"
                }
            };
            await File.WriteAllTextAsync(statePath, stateJson.ToJsonString(JsonDefaults.Indented));

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var firstOutbound = new TcpClient();
            var firstAccept = listener.AcceptTcpClientAsync();
            await firstOutbound.ConnectAsync(IPAddress.Loopback, port);
            using var firstInbound = await firstAccept;
            await using var firstSession =
                new ClientSession(new RawTcpLegacyTransport(firstOutbound));

            using var secondOutbound = new TcpClient();
            var secondAccept = listener.AcceptTcpClientAsync();
            await secondOutbound.ConnectAsync(IPAddress.Loopback, port);
            using var secondInbound = await secondAccept;
            await using var secondSession =
                new ClientSession(new RawTcpLegacyTransport(secondOutbound));

            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var registry = new GameSessionRegistry(store);

            var beforeWorld = await registry.GetExperienceBoostStateAsync(
                firstSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddHours(5),
                CancellationToken.None);
            Check.Equal(
                1_000u,
                beforeWorld.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddHours(5)),
                "account login and character selection do not start the boost clock");

            registry.ReplaceAccountSession(accountId, firstSession);
            registry.JoinMap(
                firstSession,
                accountId,
                character,
                WorldObjectIds.ForPlayer(character.Id),
                worldReady: false,
                joinedAt: joinedAt);
            var whileWorldLoads = await registry.GetExperienceBoostStateAsync(
                firstSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddSeconds(60),
                CancellationToken.None);
            Check.Equal(
                1_000u,
                whileWorldLoads.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddSeconds(60)),
                "world loading does not start the online countdown");
            Check.True(
                registry.TryMarkWorldReady(
                    firstSession,
                    new Dictionary<uint, long>(),
                    out var unseenPlayers,
                    joinedAt.AddSeconds(60)) &&
                unseenPlayers.Count == 0,
                "world-ready transition starts the authoritative boost session");
            var firstCheckpoint = await registry.GetExperienceBoostStateAsync(
                firstSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddSeconds(120),
                CancellationToken.None);
            Check.Equal(
                940u,
                firstCheckpoint.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddSeconds(120)),
                "world-ready play checkpoints the online countdown");

            var replaced = registry.ReplaceAccountSession(accountId, secondSession);
            Check.True(ReferenceEquals(firstSession, replaced), "second login identifies the prior account session");
            await registry.FinishProgressionBoostOnlineSessionAsync(
                firstSession,
                joinedAt.AddSeconds(150),
                CancellationToken.None);
            registry.Remove(firstSession);
            registry.JoinMap(
                secondSession,
                accountId,
                character,
                WorldObjectIds.ForPlayer(character.Id),
                joinedAt: joinedAt.AddSeconds(150));
            var replacementCheckpoint = await registry.GetExperienceBoostStateAsync(
                secondSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddSeconds(180),
                CancellationToken.None);
            Check.Equal(
                880u,
                replacementCheckpoint.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddSeconds(180)),
                "session replacement consumes one continuous interval without overlap");

            var staleSessionRead = await registry.GetExperienceBoostStateAsync(
                firstSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddSeconds(210),
                CancellationToken.None);
            Check.Equal(
                880u,
                staleSessionRead.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddSeconds(210)),
                "replaced session cannot consume the new owner's duration");

            await registry.FinishProgressionBoostOnlineSessionAsync(
                secondSession,
                joinedAt.AddSeconds(190),
                CancellationToken.None);
            registry.Remove(secondSession);
            var afterLogout = await store.GetExperienceBoostStateAsync(
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddDays(30));
            Check.Equal(
                870u,
                afterLogout.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddDays(30)),
                "logout saves the exact tail and the offline month consumes nothing");
        }
        finally
        {
            listener.Stop();
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static Task CheckWarriorStarterSkillPacketsAsync()
    {
        SkillState[] warriorSkills = [new() { SkillId = 0, Level = 1 }];
        var skillList = PacketBuilder.SkillList(warriorSkills);
        Check.Equal(24, skillList.Length, "single-skill list length");
        Check.Equal((ushort)10196, ReadUInt16(skillList, 2), "skill-list opcode");
        Check.Equal(1u, ReadUInt32(skillList, 8), "skill-list count");
        Check.Equal(0u, ReadUInt32(skillList, 12), "Light Chop skill ID zero remains valid");
        Check.Equal(0x101u, ReadUInt32(skillList, 16), "Light Chop level flag");

        var unlocks = PacketBuilder.TalentSkillUnlockList(warriorSkills);
        Check.Equal((ushort)10041, ReadUInt16(unlocks, 2), "skill-unlock opcode");
        Check.Equal(1u, ReadUInt32(unlocks, 8), "skill-unlock count");
        Check.Equal(0u, ReadUInt32(unlocks, 12), "skill unlock preserves ID zero");
        var listedIds = Enumerable.Range(0, (int)ReadUInt32(skillList, 8))
            .Select(index => ReadUInt32(skillList, 12 + (index * 12)))
            .ToArray();
        Check.True(
            !listedIds.Any(value => value is >= 250 and <= 354),
            "Warrior skill list does not contain Champion skills");
        return Task.CompletedTask;
    }

    private static async Task CheckJsonProviderStarterSkillAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-skill-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync("skill-check", "");
            var warrior = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "JsonWarrior",
                    Profession = 0,
                    Camp = GameDefaults.SpartaCamp
                });
            var skills = await store.GetSkillStatesAsync(account.Id, warrior.Id);
            Check.Equal(2, skills.Count, "JSON warrior receives starter combat and temporary Ride skills");
            Check.True(
                skills.Any(skill => skill.SkillId == 0 && skill.Level == 1),
                "JSON warrior learns Light Chop 1");
            Check.True(
                skills.Any(skill => skill.SkillId == MountCatalog.RideSkillId && skill.Level == 1),
                "JSON warrior receives the temporary Ride compatibility grant");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
