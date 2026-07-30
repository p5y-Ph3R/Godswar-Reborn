namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Repository-text ratchet that prevents durable character-value executors
/// from bypassing the session-wide PostgreSQL ownership fence.
/// </summary>
internal static class PlayerOwnershipArchitectureChecks
{
    private const string LifecycleExecutorExemption =
        "src/Godswar.Server/Infrastructure/Characters/" +
        "PostgresCharacterLifecycleCommandExecutor.cs";

    private static readonly string[] ExecutorPaths =
    [
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresDeveloperBagClearCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresDeveloperItemGrantCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresEquipmentBagTransferCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresEquipmentForgeCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresGearEnhancementCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresGearMentorDecomposeCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresGearMentorMaterialConversionCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresHolyStoneCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresKitBagItemDeleteCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresKitBagItemMoveCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Inventory/" +
        "PostgresMakeAttributeStoneCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Pets/" +
        "PostgresPetDurableCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Progression/" +
        "PostgresProgressionIntervalSettlementCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Rewards/" +
        "PostgresMonsterDeathRewardCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Talents/" +
        "PostgresTalentUpgradeCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Zodiac/" +
        "PostgresZodiacSkillGridActivationCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Zodiac/" +
        "PostgresZodiacSkillGridSelectionCommandExecutor.cs",
        "src/Godswar.Server/Infrastructure/Zodiac/" +
        "PostgresZodiacSkillGridUpgradeCommandExecutor.cs"
    ];

    public static Task RunAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        Check.Equal(18, ExecutorPaths.Length,
            "reviewed durable executor fence inventory");
        AssertExactExecutorInventory(repositoryRoot);

        foreach (var relativePath in ExecutorPaths)
        {
            var source = ReadSource(repositoryRoot, relativePath);
            foreach (var requiredToken in new[]
                     {
                         "_ownershipGuard",
                         "LockCurrentAsync",
                         "ValidateCurrentAsync"
                     })
            {
                Check.True(
                    source.Contains(
                        requiredToken,
                        StringComparison.Ordinal),
                    $"{relativePath} retains {requiredToken}");
            }
        }

        var monsterPath =
            "src/Godswar.Server/Infrastructure/Rewards/" +
            "PostgresMonsterDeathRewardCommandExecutor.cs";
        var monster = ReadSource(repositoryRoot, monsterPath);
        var ownershipLock = monster.IndexOf(
            "LockCurrentAsync",
            StringComparison.Ordinal);
        var deathLock = monster.IndexOf(
            "AcquireDeathIdentityLockAsync",
            StringComparison.Ordinal);
        Check.True(
            ownershipLock >= 0 &&
            deathLock >= 0 &&
            ownershipLock < deathLock,
            "monster ownership lock precedes the death identity lock");

        AssertZodiacLevelStoreFence(repositoryRoot);
        AssertLegacyZodiacUpgradeFailsClosed(repositoryRoot);
        AssertLeaveBroadcastFence(repositoryRoot);
        AssertGameplayPacketFence(repositoryRoot);
        AssertGameplayEffectFences(repositoryRoot);

        return Task.CompletedTask;
    }

    private static void AssertZodiacLevelStoreFence(
        string repositoryRoot)
    {
        var path =
            "src/Godswar.Server/State/" +
            "PostgresGameStore.Progression.cs";
        var method = MethodSlice(
            ReadSource(repositoryRoot, path),
            "public async Task<ZodiacLevelUpgradeResult?> " +
            "UpgradeZodiacLevelAsync");
        AssertOrdered(
            method,
            path,
            "PlayerOwnershipFence ownership",
            "_playerOwnershipGuard.LockCurrentAsync",
            "ZodiacLevelUpgrade.Apply",
            "transaction.CommitAsync");
        Check.True(
            method.LastIndexOf(
                "_playerOwnershipGuard.ValidateCurrentAsync",
                StringComparison.Ordinal) >
            method.IndexOf(
                "transaction.CommitAsync",
                StringComparison.Ordinal),
            $"{path} revalidates ownership after commit");
    }

    private static void AssertLegacyZodiacUpgradeFailsClosed(
        string repositoryRoot)
    {
        var path =
            "src/Godswar.Server/Game/GameClientHandler.Zodiac.cs";
        var method = MethodSlice(
            ReadSource(repositoryRoot, path),
            "private async Task " +
            "HandleCompatibilityZodiacSkillGridUpgradeAsync");
        AssertOrdered(
            method,
            path,
            "AllowLegacyPlayerMutationFallback",
            "_registry.UpgradeZodiacSkillGridAsync");
    }

    private static void AssertLeaveBroadcastFence(
        string repositoryRoot)
    {
        var path =
            "src/Godswar.Server/Game/" +
            "GameClientHandler.WorldBroadcast.cs";
        var method = MethodSlice(
            ReadSource(repositoryRoot, path),
            "private async Task BroadcastPlayerLeaveAsync");
        AssertOrdered(
            method,
            path,
            "TryGetCharacterOwnership",
            "IsCurrentWorldOwnership",
            "BroadcastToMapAsync");
    }

    private static void AssertGameplayPacketFence(
        string repositoryRoot)
    {
        const string handlerPath =
            "src/Godswar.Server/Game/GameClientHandler.cs";
        var dispatch = MethodSlice(
            ReadSource(repositoryRoot, handlerPath),
            "private async Task HandlePacketAsync");
        AssertOrdered(
            dispatch,
            handlerPath,
            "AuthorizeAuthenticatedPacket",
            "switch (packet.Opcode)");

        const string ownershipPath =
            "src/Godswar.Server/Game/" +
            "GameClientHandler.PlayerOwnership.cs";
        var authorization = MethodSlice(
            ReadSource(repositoryRoot, ownershipPath),
            "private bool AuthorizeAuthenticatedPacket");
        foreach (var token in new[]
                 {
                     "IsCurrentAccountSession",
                     "IsCurrentWorldOwnership",
                     "RejectLostPlayerOwnership"
                 })
        {
            Check.True(
                authorization.Contains(
                    token,
                    StringComparison.Ordinal),
                $"{ownershipPath} packet gate retains {token}");
        }
    }

    private static void AssertGameplayEffectFences(
        string repositoryRoot)
    {
        var required = new Dictionary<string, string[]>(
            StringComparer.Ordinal)
        {
            ["src/Godswar.Server/Game/" +
             "GameClientHandler.Progression.cs"] =
            [
                "chat_broadcast",
                "RevalidateCurrentWorldEffectOwnership"
            ],
            ["src/Godswar.Server/Game/" +
             "GameClientHandler.Casting.cs"] =
            [
                "pending_skill_completion",
                "RevalidateCurrentWorldEffectOwnership"
            ],
            ["src/Godswar.Server/Game/" +
             "GameClientHandler.MovementCombat.cs"] =
            [
                "basic_attack_damage",
                "RevalidateCurrentWorldEffectOwnership"
            ],
            ["src/Godswar.Server/Game/" +
             "GameClientHandler.CombatSkill.cs"] =
            [
                "single_skill_damage",
                "RevalidateCurrentWorldEffectOwnership"
            ],
            ["src/Godswar.Server/Game/" +
             "GameClientHandler.CombatArea.cs"] =
            [
                "area_skill_damage",
                "RevalidateCurrentWorldEffectOwnership"
            ],
            ["src/Godswar.Server/Game/" +
             "GameClientHandler.CombatStunStatus.cs"] =
            [
                "stun_skill_effect",
                "RevalidateCurrentWorldEffectOwnership"
            ],
            ["src/Godswar.Server/Game/" +
             "GameClientHandler.CombatEcsBasic.cs"] =
            [
                "ecs_basic_attack_damage",
                "RevalidateCurrentWorldEffectOwnership"
            ],
            ["src/Godswar.Server/Game/" +
             "GameClientHandler.CombatEcsSkill.cs"] =
            [
                "ecs_single_skill_damage",
                "RevalidateCurrentWorldEffectOwnership"
            ],
            ["src/Godswar.Server/Game/" +
             "GameClientHandler.CombatEcsArea.cs"] =
            [
                "ecs_area_skill_damage",
                "RevalidateCurrentWorldEffectOwnership"
            ]
        };

        foreach (var (path, tokens) in required)
        {
            var source = ReadSource(repositoryRoot, path);
            foreach (var token in tokens)
            {
                Check.True(
                    source.Contains(
                        token,
                        StringComparison.Ordinal),
                    $"{path} retains effect fence token {token}");
            }
        }
    }

    private static string MethodSlice(
        string source,
        string signature)
    {
        var start = source.IndexOf(
            signature,
            StringComparison.Ordinal);
        Check.True(start >= 0, $"{signature} remains present");
        var nextMethod = source.IndexOf(
            "\n    private ",
            start + signature.Length,
            StringComparison.Ordinal);
        return nextMethod < 0
            ? source[start..]
            : source[start..nextMethod];
    }

    private static void AssertOrdered(
        string source,
        string path,
        params string[] tokens)
    {
        var previous = -1;
        foreach (var token in tokens)
        {
            var current = source.IndexOf(
                token,
                StringComparison.Ordinal);
            Check.True(
                current > previous,
                $"{path} retains ordered ownership token {token}");
            previous = current;
        }
    }

    private static void AssertExactExecutorInventory(
        string repositoryRoot)
    {
        var infrastructureRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Godswar.Server",
            "Infrastructure");
        var discovered = Directory
            .EnumerateFiles(
                infrastructureRoot,
                "Postgres*CommandExecutor.cs",
                SearchOption.AllDirectories)
            .Select(path => Path
                .GetRelativePath(repositoryRoot, path)
                .Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var reviewed = ExecutorPaths
            .Append(LifecycleExecutorExemption)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (discovered.SequenceEqual(
                reviewed,
                StringComparer.Ordinal))
        {
            return;
        }

        var unreviewed = discovered.Except(
            reviewed,
            StringComparer.Ordinal);
        var missing = reviewed.Except(
            discovered,
            StringComparer.Ordinal);
        throw new InvalidOperationException(
            "Durable executor ownership inventory changed. " +
            "New executors must be fenced or explicitly reviewed. " +
            $"Unreviewed=[{string.Join(", ", unreviewed)}]; " +
            $"Missing=[{string.Join(", ", missing)}].");
    }

    private static string ReadSource(
        string repositoryRoot,
        string relativePath)
    {
        var path = Path.GetFullPath(
            Path.Combine(
                repositoryRoot,
                relativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required ownership-fenced source is missing: " +
                relativePath,
                path);
        }
        return File.ReadAllText(path);
    }

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
            "Could not locate the repository root containing " +
            "AGENTS.md and GodswarServer.sln.");
    }

    private static bool IsRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, "AGENTS.md")) &&
        File.Exists(Path.Combine(path, "GodswarServer.sln"));
}
