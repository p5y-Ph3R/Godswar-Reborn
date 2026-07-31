using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
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

            var joinedAt = new DateTimeOffset(
                2026,
                7,
                20,
                1,
                0,
                0,
                TimeSpan.Zero);

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

            var persistence = new WorldBoostClockPersistence(
                accountId,
                character.Id,
                TimeSpan.FromSeconds(1_000));
            var registry = new GameSessionRegistry(
                progressionIntervalSettlementCommands: persistence,
                experienceBoosts: persistence);

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

            GameHandlerOwnershipTestFences.Bind(
                registry,
                firstSession,
                accountId,
                character);
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

            await registry.FinishProgressionBoostOnlineSessionAsync(
                firstSession,
                joinedAt.AddSeconds(150),
                CancellationToken.None);
            var replaced = registry.ReplaceAccountSession(
                accountId,
                secondSession);
            Check.True(
                ReferenceEquals(firstSession, replaced),
                "second login identifies the prior account session");
            registry.Remove(firstSession);
            GameHandlerOwnershipTestFences.Bind(
                registry,
                secondSession,
                accountId,
                character);
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
            var afterLogout = await registry.GetExperienceBoostStateAsync(
                secondSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddDays(30),
                CancellationToken.None);
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

    private sealed class WorldBoostClockPersistence(
        int accountId,
        int characterId,
        TimeSpan initialDuration) :
        IProgressionIntervalSettlementCommandExecutor,
        IExperienceBoostStateReader
    {
        private readonly object _gate = new();
        private readonly Dictionary<
            string,
            ProgressionIntervalSettlementReceipt> _committed =
                new(StringComparer.Ordinal);
        private long _remainingTicks = initialDuration.Ticks;

        public Task<ExperienceBoostSnapshot> ReadAsync(
            ExperienceBoostReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExperienceBoostContract.ValidateRequest(request);
            lock (_gate)
            {
                if (request.AccountId != accountId ||
                    request.CharacterId != characterId ||
                    _remainingTicks <= 0)
                {
                    return Task.FromResult(
                        ExperienceBoostSnapshot.Empty);
                }

                return Task.FromResult(
                    new ExperienceBoostSnapshot(
                        ImmutableArray.Create(
                            new ExperienceBoostEntry(
                                Godswar.Server.Application.Progression
                                    .ExperienceStatusIds
                                    .MaxExperiencePotion,
                                Godswar.Server.Application.Progression
                                    .ExperienceBoostKinds.Consumable,
                                BonusBasisPoints: 30_000,
                                Priority: 11,
                                request.ReadAtUtc +
                                    TimeSpan.FromTicks(
                                        _remainingTicks),
                                Source: "world-clock"))));
            }
        }

        public Task<ProgressionIntervalSettlementExecutionResult>
            ExecuteAsync(
                CommandEnvelope<ProgressionIntervalSettlementCommand>
                    envelope,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_committed.TryGetValue(
                        envelope.OperationId,
                        out var committed))
                {
                    return Task.FromResult(
                        ProgressionIntervalSettlementExecutionResult
                            .Duplicate(
                                committed,
                                committed.Projection));
                }

                var command = envelope.Command;
                _remainingTicks = Math.Max(
                    0,
                    _remainingTicks -
                        (command.OnlineUntilUtc -
                         command.OnlineFromUtc).Ticks);
                var projection = new ProgressionIntervalProjection(
                    command.OnlineSessionId,
                    command.IntervalSequence,
                    command.OnlineUntilUtc,
                    command.IntervalSequence,
                    ZodiacEnergy: 0,
                    ZodiacEnergyRemainderX100: 0,
                    DateOnly.FromDateTime(
                        command.OnlineUntilUtc.UtcDateTime),
                    command.OnlineUntilUtc.UtcTicks -
                        command.OnlineFromUtc.UtcTicks,
                    ZodiacLastCompensationDay: null);
                var receipt =
                    new ProgressionIntervalSettlementReceipt(
                        envelope.Subject.CharacterId,
                        command.OnlineSessionId,
                        command.IntervalSequence,
                        command.OnlineFromUtc,
                        command.OnlineUntilUtc,
                        GainedZodiacEnergyX100: 0,
                        ZodiacCompensationApplied: false,
                        UpdatedBoostCount: 1,
                        projection,
                        AuditReference: "world-clock",
                        Guid.NewGuid());
                _committed.Add(envelope.OperationId, receipt);
                return Task.FromResult(
                    ProgressionIntervalSettlementExecutionResult
                        .Committed(receipt));
            }
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
