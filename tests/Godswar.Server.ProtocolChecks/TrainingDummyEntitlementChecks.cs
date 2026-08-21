using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummyEntitlementChecks
{
    public const string CheckName =
        "Development-only identity-bound training-dummy basic attacks";

    public static async Task RunAsync()
    {
        Check.True(
            SkillCombatResolver.MustRejectHostilePlayerTarget(
                selectedTargetIsOtherPlayer: true),
            "training-dummy configuration does not admit hostile PvP skills");
        CheckConfigurationBoundary();
        CheckFixtureSourceContracts();
        CheckImmutableIdentityPolicy();
        CheckSpawnPkModeProjection();
        CheckEntryVitalsPolicy();
        await CheckDirectionalCapitalAdmissionAsync();
        await CheckBasicAttackCollisionAllowanceAsync();
        await CheckIdentityCollisionsDeniedAsync();
        await CheckOrdinaryAndMapBoundariesAsync();
    }

    private static void CheckConfigurationBoundary()
    {
        var parsed = TrainingDummyOptions.ParseIdentities(
            "7001:7001:AresBulwark:1:0:148:-154," +
            "7002:7002:AresMirage:1:0:148:-162");
        Check.True(
            parsed.Length == 2 &&
            parsed[0].CharacterId == 7001 &&
            parsed[0].AccountId == 7001 &&
            parsed[0].Name == "AresBulwark" &&
            parsed[0].Camp == 1 &&
            parsed[0].MapId == 0 &&
            parsed[0].PositionX == 148f &&
            parsed[0].PositionZ == -154f &&
            parsed[1].PositionZ == -162f,
            "environment contract parses immutable seven-field dummy tuples");
        Check.Throws<InvalidDataException>(
            () => TrainingDummyOptions.ParseIdentities(
                "7001:AresBulwark:0"),
            "partial name-only identity configuration is rejected");
        Check.Throws<InvalidDataException>(
            () => TrainingDummyOptions.ParseIdentities(
                "7001:7001:AresBulwark:1:0:NaN:-154"),
            "non-finite environment coordinates are rejected");
        var nonFinite = new TrainingDummyOptions
        {
            Enabled = true,
            Identities = [Identity(7001, "AresBulwark", 0)]
        };
        nonFinite.Identities[0].PositionZ = float.PositiveInfinity;
        Check.Throws<InvalidDataException>(
            nonFinite.Normalize,
            "non-finite appsettings coordinates are rejected");

        var sameCamp = new TrainingDummyOptions
        {
            Enabled = true,
            Identities = [Identity(7001, "AresBulwark", 0)]
        };
        sameCamp.Identities[0].Camp = 0;
        Check.Throws<InvalidDataException>(
            sameCamp.Normalize,
            "a dummy must use the camp opposing its capital map");

        var outsideCapital = new TrainingDummyOptions
        {
            Enabled = true,
            Identities = [Identity(7001, "AresBulwark", 0)]
        };
        outsideCapital.Identities[0].MapId = 7;
        Check.Throws<InvalidDataException>(
            outsideCapital.Normalize,
            "dummy identity configuration is limited to capital maps");

        var production = new ServerOptions
        {
            RuntimeProfile = "Production",
            Game = new GameEndpointOptions
            {
                TrainingDummies = Options()
            },
            Storage = new StorageOptions
            {
                Provider = "Postgres",
                PostgresConnectionString =
                    "Host=database;Database=godswar"
            },
            Secure = new() { Enabled = true }
        };
        Check.Throws<ServerStartupConfigurationException>(
            () => ServerRuntimeProfilePolicy.Validate(production),
            "production rejects training-dummy combat configuration");
        Check.Equal(
            "training_dummy_entitlement_forbidden",
            ServerRuntimeProfilePolicy.RejectionCode(
                ServerStartupRejectionReason.
                    TrainingDummyEntitlementForbidden),
            "training-dummy production rejection has an explicit code");
    }

    private static void CheckImmutableIdentityPolicy()
    {
        var policy = Policy();
        Check.True(
            policy.Contains(Dummy(7001)) &&
            policy.Contains(Dummy(7002)) &&
            policy.Contains(Dummy(7003)) &&
            policy.Contains(Dummy(7004)) &&
            !policy.Contains(Dummy(7001, name: "aresbulwark")) &&
            !policy.Contains(Dummy(7001, accountId: 7999)) &&
            !policy.Contains(Dummy(7999, accountId: 7001)) &&
            !policy.Contains(Dummy(7001, camp: 0)) &&
            !policy.Contains(Dummy(7001, map: 1)) &&
            !policy.Contains(Dummy(7001, positionX: 149f)) &&
            !policy.Contains(Dummy(7001, positionZ: -153f)) &&
            !policy.Contains(Dummy(7998, accountId: 7998)),
            "combat-ready identity requires exact core, map, and position");
    }

    private static void CheckSpawnPkModeProjection()
    {
        var registry = Registry(Policy());
        var exact = Dummy(7001);
        var ordinary = Player(90, 90, "Ordinary", 0, 1);
        var moved = Dummy(7001, positionX: 149f);
        Check.True(
            registry.TrainingDummySpawnPkMode(exact) == 1 &&
            registry.TrainingDummySpawnPkMode(ordinary) is null &&
            registry.TrainingDummySpawnPkMode(moved) is null,
            "only an exact dummy receives the spawn PK-mode override");

        var objectId = WorldObjectIds.ForPlayer(exact.Id);
        var dummySpawn = PacketBuilder.PlayerWorldSpawn(
            exact,
            objectId,
            pkMode: registry.TrainingDummySpawnPkMode(exact));
        var ordinarySpawn = PacketBuilder.PlayerWorldSpawn(
            ordinary,
            objectId,
            pkMode: registry.TrainingDummySpawnPkMode(ordinary));
        Check.True(
            dummySpawn[80] == 1 && ordinarySpawn[80] == 5,
            "dummy projection changes native byte 80 while ordinary spawn keeps its captured default");
    }

    private static void CheckEntryVitalsPolicy()
    {
        var registry = Registry(Policy());
        var exact = Dummy(
            7001,
            map: 7,
            positionX: 0f,
            positionZ: 0f);
        exact.CurrentHp = exact.MaxHp - 1;
        exact.CurrentMp = 17;
        var revision = exact.VitalsRevision;
        var positionRevision = exact.PositionRevision;
        Check.True(
            registry.TryRestoreTrainingDummyEntryState(exact) &&
            exact.CurrentMap == 0 &&
            exact.PositionX == 148f &&
            exact.PositionZ == -154f &&
            exact.CurrentHp == exact.MaxHp &&
            exact.CurrentMp == exact.MaxMp &&
            exact.VitalsRevision == revision + 1 &&
            exact.PositionRevision == positionRevision + 1,
            "trusted core identity repairs map, position, and full vitals");

        revision = exact.VitalsRevision;
        positionRevision = exact.PositionRevision;
        Check.True(
            registry.TryRestoreTrainingDummyEntryState(exact) &&
            exact.CurrentHp == exact.MaxHp &&
            exact.CurrentMp == exact.MaxMp &&
            exact.VitalsRevision == revision + 1 &&
            exact.PositionRevision == positionRevision + 1,
            "every exact dummy entry reasserts placement and full vitals");

        var ordinary = Player(
            80,
            80,
            "OrdinaryDeadPlayer",
            0,
            0);
        ordinary.CurrentHp = ordinary.MaxHp / 2;
        ordinary.CurrentMp = ordinary.MaxMp / 2;
        Check.True(
            !registry.TryRestoreTrainingDummyEntryState(ordinary) &&
            ordinary.CurrentHp == ordinary.MaxHp / 2 &&
            ordinary.CurrentMp == ordinary.MaxMp / 2,
            "ordinary injured players retain their entry vitals");

        ordinary.CurrentHp = 0;
        ordinary.CurrentMp = 0;
        Check.True(
            !registry.TryRestoreTrainingDummyEntryState(ordinary) &&
            ordinary.CurrentHp == 0 &&
            ordinary.CurrentMp == 0,
            "ordinary dead players bypass the full-restoration branch");

        var lowerCase = Dummy(7001, name: "aresbulwark");
        lowerCase.CurrentHp = 0;
        lowerCase.CurrentMp = 0;
        Check.True(
            !registry.TryRestoreTrainingDummyEntryState(lowerCase) &&
            lowerCase.CurrentHp == 0 &&
            lowerCase.CurrentMp == 0,
            "a case-collision identity cannot receive full restoration");
    }

    private static async Task CheckDirectionalCapitalAdmissionAsync()
    {
        var sparta = await ResolveAsync(
            Player(10, 10, "SpartanTester", 0, 0),
            Dummy(7001),
            Policy());
        Check.True(
            sparta.Accepted &&
            sparta.Eligibility.EntitlementKind ==
                PvpEntitlementKind.TrainingDummy &&
            sparta.Eligibility.Admits(10, 7001, 0),
            "Sparta admits an identity-bound opposing-camp dummy target");

        var athens = await ResolveAsync(
            Player(11, 11, "AthenianTester", 1, 1),
            Dummy(7003),
            Policy());
        Check.True(
            athens.Accepted &&
            athens.Eligibility.Admits(11, 7003, 1),
            "Athens admits an identity-bound opposing-camp dummy target");

        var reverse = await ResolveAsync(
            Dummy(7001),
            Player(12, 12, "OrdinaryTarget", 0, 1),
            Policy());
        Check.True(
            !reverse.Accepted &&
            reverse.Eligibility.Failure ==
                PvpEligibilityFailure.MissingEntitlement,
            "an exact configured dummy cannot attack another player");
    }

    private static async Task CheckIdentityCollisionsDeniedAsync()
    {
        (string Description, GameCharacter Target)[] collisions =
        [
            ("lower-case name collision",
                Dummy(7001, name: "aresbulwark")),
            ("wrong account",
                Dummy(7001, accountId: 7999)),
            ("wrong character ID",
                Dummy(7999, accountId: 7001)),
            ("wrong camp",
                Dummy(7001, camp: 0)),
            ("wrong current map",
                Dummy(7001, map: 1)),
            ("wrong X position",
                Dummy(7001, positionX: 149f)),
            ("wrong Z position",
                Dummy(7001, positionZ: -153f)),
            ("recreated same-name character",
                Dummy(7998, accountId: 7998))
        ];

        var attackerId = 100;
        foreach (var collision in collisions)
        {
            var target = collision.Target;
            var attacker = Player(
                attackerId,
                attackerId,
                $"Tester{attackerId}",
                target.CurrentMap,
                target.CurrentMap);
            var decision = await ResolveAsync(attacker, target, Policy());
            Check.True(
                !decision.Accepted ||
                decision.Eligibility.EntitlementKind !=
                    PvpEntitlementKind.TrainingDummy,
                $"{collision.Description} receives no training-dummy entitlement");
            attackerId++;
        }
    }

    private static async Task CheckOrdinaryAndMapBoundariesAsync()
    {
        var ordinary = await ResolveAsync(
            Player(200, 200, "SpartanTester", 0, 0),
            Player(201, 201, "OrdinaryTarget", 0, 1),
            Policy());
        Check.True(
            !ordinary.Accepted &&
            ordinary.Eligibility.Failure == PvpEligibilityFailure.SafeZone,
            "ordinary players remain protected in capitals");

        var outsideCapital = await ResolveAsync(
            Player(202, 202, "SpartanTester", 7, 0),
            Dummy(7001, map: 7),
            Policy());
        Check.True(
            outsideCapital.Accepted &&
            outsideCapital.Eligibility.EntitlementKind !=
                PvpEntitlementKind.TrainingDummy,
            "outside its pinned capital, the tuple falls back to ordinary PvP without dummy entitlement");

        var movedInSession = await ResolveAsync(
            Player(204, 204, "SpartanTester", 0, 0),
            Dummy(7001, positionX: 149f),
            Policy());
        Check.True(
            !movedInSession.Accepted &&
            movedInSession.Eligibility.EntitlementKind !=
                PvpEntitlementKind.TrainingDummy,
            "an in-session moved dummy loses target entitlement immediately");

        var disabled = await ResolveAsync(
            Player(203, 203, "SpartanTester", 0, 0),
            Dummy(7001),
            TrainingDummyPolicy.Disabled);
        Check.True(
            !disabled.Accepted,
            "disabled configuration leaves capital PvP default-deny");
    }

    private static async Task<PvpBasicAttackDecision> ResolveAsync(
        GameCharacter attacker,
        GameCharacter target,
        TrainingDummyPolicy policy)
    {
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = Registry(policy);
        registry.JoinPlayerMap(
            attackerSocket.Session,
            attacker.AccountId,
            attacker);
        var targetObjectId = registry.JoinPlayerMap(
            targetSocket.Session,
            target.AccountId,
            target);
        try
        {
            return await registry.ResolvePvpBasicAttackAsync(
                attackerSocket.Session,
                targetObjectId,
                attacker.PositionX,
                attacker.PositionZ,
                admittedCombatRevision: 1,
                DateTimeOffset.Parse("2026-08-16T00:00:00Z"),
                CancellationToken.None);
        }
        finally
        {
            registry.Remove(attackerSocket.Session);
            registry.Remove(targetSocket.Session);
        }
    }

    private static TrainingDummyOptions Options()
    {
        var options = new TrainingDummyOptions
        {
            Enabled = true,
            Identities =
            [
                Identity(7001, "AresBulwark", 0),
                Identity(7002, "AresMirage", 0),
                Identity(7003, "AthenaBulwark", 1),
                Identity(7004, "AthenaMirage", 1)
            ]
        };
        options.Normalize();
        return options;
    }

    private static TrainingDummyIdentityOptions Identity(
        int id,
        string name,
        byte mapId) =>
        new()
        {
            CharacterId = id,
            AccountId = id,
            Name = name,
            Camp = mapId == GameDefaults.SpartaCamp
                ? GameDefaults.AthensCamp
                : GameDefaults.SpartaCamp,
            MapId = mapId,
            PositionX = 148f,
            PositionZ = id is 7002 or 7004 ? -162f : -154f
        };

    private static TrainingDummyPolicy Policy() =>
        TrainingDummyPolicy.Create(Options(), LocalDevelopmentProfile());

    private static GameSessionRegistry Registry(
        TrainingDummyPolicy policy) =>
        new(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: PlayerRuntimeMode.Ecs,
            gameplayCatalogs: CapitalRuntimeCatalogs(),
            trainingDummies: policy);

    private static ValidatedServerRuntimeProfile LocalDevelopmentProfile() =>
        new(
            ServerRuntimeProfileKind.LocalDevelopment,
            GameStorageProviderKind.Postgres,
            ServerListenerTransport.RawTcp,
            AllowsLegacyAuthentication: true);

    private static GameplayRuntimeCatalogs CapitalRuntimeCatalogs()
    {
        var content = GameplayContentTestFixtures.Published with
        {
            Maps = GameplayContentTestFixtures.Published.Maps
                .Select(static map => map.MapId is 0 or 1
                    ? map with { MapMode = 5 }
                    : map)
                .ToArray()
        };
        return GameplayRuntimeCatalogs.Create(content);
    }

    private static GameCharacter Dummy(
        int id,
        int? accountId = null,
        string? name = null,
        byte? camp = null,
        byte? map = null,
        float? positionX = null,
        float? positionZ = null)
    {
        var template = id switch
        {
            7001 => ("AresBulwark", (byte)0, (byte)1, -154f),
            7002 => ("AresMirage", (byte)0, (byte)1, -162f),
            7003 => ("AthenaBulwark", (byte)1, (byte)0, -154f),
            7004 => ("AthenaMirage", (byte)1, (byte)0, -162f),
            _ => ("AresBulwark", (byte)0, (byte)1, -154f)
        };
        var character = Player(
            id,
            accountId ?? id,
            name ?? template.Item1,
            map ?? template.Item2,
            camp ?? template.Item3);
        character.PositionX = positionX ?? 148f;
        character.PositionZ = positionZ ?? template.Item4;
        return character;
    }

    private static GameCharacter Player(
        int id,
        int accountId,
        string name,
        byte map,
        byte camp)
    {
        var character = new GameCharacter
        {
            Id = id,
            AccountId = accountId,
            Name = name,
            CurrentMap = map,
            Camp = camp,
            Profession = 0,
            Level = 160,
            PositionX = 148f,
            PositionZ = -154f,
            CurrentHp = 100_000,
            MaxHp = 100_000,
            CurrentMp = 10_000,
            MaxMp = 10_000
        };
        character.CalculatedStats = new CharacterStats
        {
            CharacterId = id,
            AccountId = accountId,
            Name = name,
            Profession = 0,
            Level = 160,
            CurrentHp = character.CurrentHp,
            MaxHp = character.MaxHp,
            CurrentMp = character.CurrentMp,
            MaxMp = character.MaxMp,
            PhysicalAttack = 1_000,
            PhysicalDefense = 100,
            Hit = 5_000,
            Dodge = 0,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        return character;
    }
}
