using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class DurableRegistryCompositionChecks
{
    private const string RetiredAccumulationMutation =
        "AddZodiacAccumulationAsync";

    public static async Task RunAsync()
    {
        CheckCompatibilityComposition();
        CheckDurableComposition();
        await CheckJoinMapOwnershipCompositionAsync();
        CheckSourceRatchets();
    }

    private static void CheckCompatibilityComposition()
    {
        _ = new GameSessionRegistry();
        _ = new GameSessionRegistry(
            progressionIntervalSettlementCommands:
                new ProgressionExecutorStub());
    }

    private static void CheckDurableComposition()
    {
        var progression = new ProgressionExecutorStub();
        var checkpoints = new GameHandlerCheckpointCoordinatorStub();
        var focusedStore = new FocusedStore();

        Check.Throws<InvalidOperationException>(
            () => _ = new GameSessionRegistry(
                store: focusedStore,
                progressionIntervalSettlementCommands: progression,
                requiresDurablePlayerPersistence: true),
            "durable registry rejects missing checkpoint coordinator");
        Check.Throws<InvalidOperationException>(
            () => _ = new GameSessionRegistry(
                store: focusedStore,
                checkpointCoordinator: checkpoints,
                requiresDurablePlayerPersistence: true),
            "durable registry rejects missing progression executor");
        Check.Throws<InvalidOperationException>(
            () => _ = new GameSessionRegistry(
                checkpointCoordinator: checkpoints,
                progressionIntervalSettlementCommands: progression,
                experienceBoosts: focusedStore,
                requiresDurablePlayerPersistence: true),
            "durable registry rejects missing Zodiac store");
        Check.Throws<InvalidOperationException>(
            () => _ = new GameSessionRegistry(
                checkpointCoordinator: checkpoints,
                progressionIntervalSettlementCommands: progression,
                zodiacLevelStore: focusedStore,
                requiresDurablePlayerPersistence: true),
            "durable registry rejects missing experience-boost reader");

        var complete = new GameSessionRegistry(
            store: focusedStore,
            checkpointCoordinator: checkpoints,
            progressionIntervalSettlementCommands: progression,
            requiresDurablePlayerPersistence: true);
        Check.Throws<InvalidOperationException>(
            () => complete.ConfigureProgressionIntervalSettlement(
                new ProgressionExecutorStub()),
            "durable registry cannot replace its progression executor");
    }

    private static async Task
        CheckJoinMapOwnershipCompositionAsync()
    {
        await using var compatibilityTransport =
            new ScriptedLegacyByteTransport();
        await using var compatibilitySession =
            new Networking.ClientSession(
                compatibilityTransport,
                endpointRole:
                    Networking.NetworkEndpointRole.Game);
        var compatibilityCharacter = new State.GameCharacter
        {
            Id = 1,
            AccountId = 1,
            Name = "CompatibilityJoin",
            CurrentMap = State.GameDefaults.SpartaCapitalMap
        };
        var compatibility = new GameSessionRegistry();
        compatibility.JoinMap(
            compatibilitySession,
            compatibilityCharacter.AccountId,
            compatibilityCharacter,
            WorldObjectIds.ForPlayer(
                compatibilityCharacter.Id));
        Check.Equal(
            1,
            compatibility.GetMapPopulation(
                compatibilityCharacter.CurrentMap),
            "non-durable registry retains default-fence compatibility");
        compatibility.Remove(compatibilitySession);

        await using var durableTransport =
            new ScriptedLegacyByteTransport();
        await using var durableSession =
            new Networking.ClientSession(
                durableTransport,
                endpointRole:
                    Networking.NetworkEndpointRole.Game);
        var durableCharacter = new State.GameCharacter
        {
            Id = 2,
            AccountId = 2,
            Name = "DurableJoin",
            CurrentMap = State.GameDefaults.SpartaCapitalMap
        };
        var durable = new GameSessionRegistry(
            store: new FocusedStore(),
            checkpointCoordinator:
                new GameHandlerCheckpointCoordinatorStub(),
            progressionIntervalSettlementCommands:
                new ProgressionExecutorStub(),
            requiresDurablePlayerPersistence: true);
        Check.Throws<InvalidOperationException>(
            () => durable.JoinMap(
                durableSession,
                durableCharacter.AccountId,
                durableCharacter,
                WorldObjectIds.ForPlayer(
                    durableCharacter.Id)),
            "durable registry rejects world join without ownership fence");
        Check.Equal(
            0,
            durable.GetMapPopulation(
                durableCharacter.CurrentMap),
            "rejected durable world join publishes no presence");

        var ownership = new PlayerOwnershipFence(
            Guid.NewGuid(),
            1);
        durableCharacter.CheckpointOwnerId = ownership.OwnerId;
        durableCharacter.CheckpointOwnerGeneration =
            ownership.Generation;
        durable.ReplaceAccountSession(
            durableCharacter.AccountId,
            durableSession);
        Check.True(
            durable.TryBindAccountSessionOwnership(
                durableCharacter.AccountId,
                durableSession,
                ownership),
            "durable world fixture binds exact ownership");
        durable.JoinMap(
            durableSession,
            durableCharacter.AccountId,
            durableCharacter,
            WorldObjectIds.ForPlayer(
                durableCharacter.Id));
        Check.Equal(
            1,
            durable.GetMapPopulation(
                durableCharacter.CurrentMap),
            "durable registry accepts exact owned world join");
        durable.Remove(durableSession, ownership);
    }

    private static void CheckSourceRatchets()
    {
        var root = FindRepositoryRoot();
        AssertOrdered(
            ReadSource(root, "src/Godswar.Server/Program.cs"),
            "production registry composition",
            "new GameSessionRegistry(",
            "characterCheckpoints",
            ".ProgressionIntervalSettlementCommands",
            "requiresDurablePlayerPersistence:");
        AssertOrdered(
            ReadSource(
                root,
                "src/Godswar.Server/Game/" +
                "GameSessionRegistry.WorldMembership.cs"),
            "durable world-join ownership",
            "var ownership = PlayerOwnership(character)",
            "ValidateWorldJoinOwnership(",
            "new GameSessionContext(");
        AssertOrdered(
            ReadSource(
                root,
                "src/Godswar.Server/Game/" +
                "GameSessionRegistry.WorldMembership.cs"),
            "durable world-join ownership guard",
            "private void ValidateWorldJoinOwnership",
            "_requiresDurablePlayerPersistence",
            "!ownership.IsValid",
            "IsCurrentAccountSession(");

        var composition = ReadSource(
            root,
            "src/Godswar.Server/Game/" +
            "GameSessionRegistry.DurableComposition.cs");
        foreach (var token in new[]
                 {
                     "_checkpointCoordinator is null",
                     "_progressionIntervalSettlementCommands is null",
                     "RequireLegacyRegistryMutationAllowed"
                 })
        {
            Check.True(
                composition.Contains(token, StringComparison.Ordinal),
                $"durable registry composition retains {token}");
        }

        AssertGuardPrecedes(
            ReadSource(
                root,
                "src/Godswar.Server/Game/" +
                "GameSessionRegistry.Progression.cs"),
            "consume_character_boost_online_time",
            "_store.ConsumeCharacterBoostOnlineTimeAsync");
        AssertGuardPrecedes(
            ReadSource(
                root,
                "src/Godswar.Server/Game/" +
                "GameSessionRegistry.Progression.cs"),
            "apply_zodiac_online_time",
            "_store.ApplyZodiacOnlineTimeAsync");
        AssertGuardPrecedes(
            ReadSource(
                root,
                "src/Godswar.Server/Game/" +
                "GameSessionRegistry.CharacterCheckpoints.cs"),
            "save_character_vitals",
            "_store.SaveCharacterVitalsAsync");
        AssertGuardPrecedes(
            ReadSource(
                root,
                "src/Godswar.Server/Game/" +
                "GameClientHandler.CharacterCheckpoints.cs"),
            "save_character_position",
            "_store.SaveCharacterPositionAsync");
        AssertGuardPrecedes(
            ReadSource(
                root,
                "src/Godswar.Server/Game/" +
                "GameClientHandler.CharacterCheckpoints.cs"),
            "save_character_vitals",
            "_store.SaveCharacterVitalsAsync");

        var construction = ReadSource(
            root,
            "src/Godswar.Server/Game/" +
            "GameClientHandler.Construction.cs");
        var providersStart = construction.IndexOf(
            "if (_requiresDurablePlayerCommands",
            StringComparison.Ordinal);
        var providersEnd = construction.IndexOf(
            "}.Any(static provider => provider is null)",
            providersStart,
            StringComparison.Ordinal);
        Check.True(
            providersStart >= 0 &&
            providersEnd > providersStart &&
            construction[providersStart..providersEnd].Contains(
                "_characterCheckpoints",
                StringComparison.Ordinal),
            "production handler provider validation retains checkpoints");

        var productionSources = Directory.EnumerateFiles(
            Path.Combine(root, "src", "Godswar.Server"),
            "*.cs",
            SearchOption.AllDirectories);
        var retiredMutationSources = productionSources
            .Where(path => File.ReadAllText(path).Contains(
                RetiredAccumulationMutation,
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();
        Check.Equal(
            0,
            retiredMutationSources.Length,
            "retired Zodiac accumulation mutation remains absent");
    }

    private static void AssertGuardPrecedes(
        string source,
        string guardToken,
        string mutationToken)
    {
        var mutation = source.IndexOf(
            mutationToken,
            StringComparison.Ordinal);
        var guard = mutation < 0
            ? -1
            : source.LastIndexOf(
                guardToken,
                mutation,
                StringComparison.Ordinal);
        Check.True(
            guard >= 0 && guard < mutation,
            $"{guardToken} precedes {mutationToken}");
    }

    private static void AssertOrdered(
        string source,
        string description,
        params string[] tokens)
    {
        var previous = -1;
        foreach (var token in tokens)
        {
            var current = source.IndexOf(
                token,
                previous + 1,
                StringComparison.Ordinal);
            Check.True(
                current > previous,
                $"{description} retains ordered token {token}");
            previous = current;
        }
    }

    private static string ReadSource(
        string root,
        string relativePath) =>
        File.ReadAllText(
            Path.Combine(
                root,
                relativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        var configured = Environment.GetEnvironmentVariable(
            "GODSWAR_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) &&
            IsRepositoryRoot(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            for (var candidate = new DirectoryInfo(seed);
                 candidate is not null;
                 candidate = candidate.Parent)
            {
                if (IsRepositoryRoot(candidate.FullName))
                {
                    return candidate.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }

    private static bool IsRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, "AGENTS.md")) &&
        File.Exists(Path.Combine(path, "GodswarServer.sln"));

    private sealed class ProgressionExecutorStub :
        IProgressionIntervalSettlementCommandExecutor
    {
        public Task<ProgressionIntervalSettlementExecutionResult>
            ExecuteAsync(
                CommandEnvelope<ProgressionIntervalSettlementCommand>
                    envelope,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "Composition checks do not execute progression.");
    }

    private sealed class FocusedStore : GameStoreTestStub;
}
