using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Realms;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class RealmCalendarAuthorityChecks
{
    public const string CheckName =
        "Persisted per-realm game calendar authority";

    public static Task RunAsync()
    {
        CheckCalendarBoundaries();
        CheckCatalogFingerprint();
        CheckMigrationAndPersistenceSurface();
        CheckConsumerSourceContracts();
        return Task.CompletedTask;
    }

    private static void CheckCalendarBoundaries()
    {
        var manila = RealmCalendar.CreateForTesting(
            RealmId.Tempest,
            "Asia/Manila");
        var beforeMidnight =
            new DateTimeOffset(2026, 8, 20, 15, 59, 0, TimeSpan.Zero);
        var midnight =
            new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero);
        Check.Equal(
            new DateOnly(2026, 8, 20),
            manila.GetDay(beforeMidnight),
            "Manila realm day remains Thursday before local midnight");
        Check.Equal(
            new DateOnly(2026, 8, 21),
            manila.GetDay(midnight),
            "Manila realm day rotates at UTC+8 midnight");
        Check.Equal(
            TimeSpan.FromHours(8),
            manila.GetUtcOffset(midnight),
            "Manila server clock advertises UTC+8");
        Check.Equal(
            midnight,
            manila.GetStartOfDay(new DateOnly(2026, 8, 21)),
            "realm day start is returned as its UTC instant");
        Check.Equal(
            midnight,
            manila.GetNextDayBoundary(beforeMidnight),
            "next reset boundary uses the persisted realm calendar");
        Check.Equal(
            new DateOnly(2026, 8, 17),
            RealmCalendar.GetWeekStart(new DateOnly(2026, 8, 21)),
            "realm weeks begin on Monday");

        var newYork = RealmCalendar.CreateForTesting(
            RealmId.Dwargon,
            "America/New_York");
        Check.Equal(
            new DateTimeOffset(
                2026, 3, 9, 4, 0, 0, TimeSpan.Zero),
            newYork.GetNextDayBoundary(
                new DateTimeOffset(
                    2026, 3, 8, 12, 0, 0, TimeSpan.Zero)),
            "DST-short day resolves its real next UTC boundary");
        Check.Throws<ArgumentException>(
            () => RealmCalendar.CreateForTesting(
                RealmId.Tempest,
                "Asia//Manila"),
            "malformed IANA identifiers fail before persistence");
    }

    private static void CheckCatalogFingerprint()
    {
        var tempest = new RealmCalendar(
            RealmId.Tempest,
            "Asia/Manila",
            1,
            DateTimeOffset.UnixEpoch,
            "migration-103");
        var dwargon = new RealmCalendar(
            RealmId.Dwargon,
            "Asia/Manila",
            1,
            DateTimeOffset.UnixEpoch,
            "migration-103");
        var catalog = new RealmCalendarCatalog([dwargon, tempest]);
        Check.Equal(
            RealmId.Tempest,
            catalog.Entries[0].RealmId,
            "calendar fingerprint input is sorted by durable realm ID");
        Check.True(
            ReferenceEquals(tempest, catalog.Require(RealmId.Tempest)),
            "process realm resolves from the complete catalog");
        Check.Equal(
            64,
            catalog.CoordinationRevision.Length,
            "calendar catalog exposes an uppercase SHA-256 revision");
        Check.Equal(
            64,
            tempest.TimeZoneRulesFingerprint.Length,
            "calendar authority fingerprints the resolved host tzdata rules");

        var fixedUtc = TimeZoneInfo.CreateCustomTimeZone(
            "Test/SamePersistedZone",
            TimeSpan.Zero,
            "Fixed UTC",
            "Fixed UTC");
        var fixedPlusOne = TimeZoneInfo.CreateCustomTimeZone(
            "Test/SamePersistedZone",
            TimeSpan.FromHours(1),
            "Fixed +1",
            "Fixed +1");
        Check.True(
            !RealmCalendar.ComputeTimeZoneRulesFingerprint(fixedUtc).Equals(
                RealmCalendar.ComputeTimeZoneRulesFingerprint(fixedPlusOne),
                StringComparison.Ordinal),
            "different resolved zone rules fence mixed-tzdata workers even when persisted catalog fields otherwise agree");

        var transitionStart =
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(
                new DateTime(
                    1,
                    1,
                    1,
                    2,
                    0,
                    0,
                    DateTimeKind.Unspecified),
                3,
                1);
        var transitionEnd =
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(
                new DateTime(
                    1,
                    1,
                    1,
                    2,
                    0,
                    0,
                    DateTimeKind.Unspecified),
                11,
                1);
        var completeRuleFactory =
            typeof(TimeZoneInfo.AdjustmentRule)
                .GetMethods(
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic)
                .Single(static method =>
                    method.Name == "CreateAdjustmentRule" &&
                    method.GetParameters() is var parameters &&
                    parameters.Length == 7 &&
                    parameters[^1].ParameterType == typeof(bool));
        TimeZoneInfo.AdjustmentRule CompleteRule(
            bool noDaylightTransitions) =>
            (TimeZoneInfo.AdjustmentRule)completeRuleFactory.Invoke(
                obj: null,
                [
                    new DateTime(2020, 1, 1),
                    new DateTime(2030, 12, 31),
                    TimeSpan.FromHours(1),
                    transitionStart,
                    transitionEnd,
                    TimeSpan.Zero,
                    noDaylightTransitions
                ])!;
        TimeZoneInfo CompleteZone(bool noDaylightTransitions) =>
            TimeZoneInfo.CreateCustomTimeZone(
                "Test/OpaqueRule",
                TimeSpan.Zero,
                "Opaque rule",
                "Opaque rule",
                "Opaque daylight",
                [CompleteRule(noDaylightTransitions)]);
        var transitioning = CompleteZone(noDaylightTransitions: false);
        var noTransitions = CompleteZone(noDaylightTransitions: true);
        var winter = new DateTimeOffset(
            2025,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        Check.True(
            transitioning.GetUtcOffset(winter) !=
                noTransitions.GetUtcOffset(winter) &&
            !RealmCalendar.ComputeTimeZoneRulesFingerprint(transitioning)
                .Equals(
                    RealmCalendar.ComputeTimeZoneRulesFingerprint(
                        noTransitions),
                    StringComparison.Ordinal),
            "opaque no-transition semantics cannot collide under one calendar coordination revision");

        var changed = new RealmCalendarCatalog(
        [
            tempest,
            new RealmCalendar(
                RealmId.Dwargon,
                "Etc/UTC",
                2,
                DateTimeOffset.UnixEpoch,
                "management-test")
        ]);
        Check.True(
            !catalog.CoordinationRevision.Equals(
                changed.CoordinationRevision,
                StringComparison.Ordinal),
            "any realm calendar change fences mixed worker content");

        var digest = RuntimeContentFingerprint.Create(
            new string('A', 64),
            new string('B', 64),
            new string('C', 64),
            new string('D', 64),
            new string('E', 64),
            new string('F', 64),
            new string('1', 64),
            catalog.CoordinationRevision);
        Check.Equal(64, digest.Length, "runtime fingerprint includes calendars");
    }

    private static void CheckMigrationAndPersistenceSurface()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id ==
                "20260821_103_realm_calendar_authority");
        Check.Equal(
            "4050821E717127DC2F24C327FBF6BF9806CFECA8D99147C5EFEE9149A9BF1DD9",
            migration.Checksum,
            "realm calendar authority migration checksum is pinned");
        Check.True(
            migration.Sql.Contains(
                "ADD COLUMN time_zone_id varchar(64)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "SET time_zone_id = 'Asia/Manila'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "realm.id = 1",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "realm.name = 'Tempest'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'KAL3jcIzqGgKvOf1dbYZKC8cS'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "realm.id = 2",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "realm.name = 'Dwargon'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'DWG3jcIzqGgKvOf1dbYZKC8cS'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "seeded_rows <> 2",
                StringComparison.Ordinal),
            "exact Tempest and Dwargon identities are seeded to Philippine time");
        Check.True(
            migration.Sql.Contains(
                "CREATE TABLE public.server_time_zone_audit",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "audit evidence is append-only",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "must match the current realm row",
                StringComparison.Ordinal),
            "calendar changes retain guarded append-only evidence");
        Check.True(
            migration.Sql.Contains(
                "public.faction_crier_balance_revisions.server_utc_offset_minutes",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "public.holy_suit_operation_policy_content_definitions.realm_day_time_zone",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "public.official_holy_suit_operation_policy_content.realm_day_time_zone",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "Historical balance provenance only",
                StringComparison.Ordinal) &&
            migration.Sql.Split(
                "Historical content provenance only",
                StringSplitOptions.None).Length == 3,
            "legacy calendar columns are explicitly provenance-only");
        Check.True(
            PostgresRealmCalendarCatalogReader.ReadSql.Contains(
                "ORDER BY id",
                StringComparison.Ordinal) &&
            !PostgresRealmCalendarCatalogReader.ReadSql.Contains(
                "WHERE id =",
                StringComparison.Ordinal),
            "startup fingerprints the complete sorted realm catalog");
        Check.True(
            PostgresRealmCalendarSettingsStore.UpdateSql.Contains(
                "time_zone_revision = realm.time_zone_revision + 1",
                StringComparison.Ordinal) &&
            PostgresRealmCalendarSettingsStore.UpdateSql.Contains(
                "current.time_zone_revision = @expectedRevision",
                StringComparison.Ordinal),
            "management updates use compare-and-swap revisions");
    }

    private static void CheckConsumerSourceContracts()
    {
        var root = FindRepositoryRoot();
        var server = Path.Combine(root, "src", "Godswar.Server");
        var staleConfigAuthority = new[]
        {
            Path.Combine(root, "appsettings.json"),
            Path.Combine(root, "appsettings.docker.json"),
            Path.Combine(root, ".env.example"),
            Path.Combine(root, "docker-compose.yml"),
            Path.Combine(
                root,
                "deploy",
                "local",
                "redis-coordinated-worker.json")
        }.Select(File.ReadAllText).ToArray();
        Check.True(
            staleConfigAuthority.All(static source =>
                !source.Contains(
                    "serverUtcOffsetMinutes",
                    StringComparison.Ordinal) &&
                !source.Contains(
                    "GODSWAR_ZODIAC_SERVER_UTC_OFFSET_MINUTES",
                    StringComparison.Ordinal)),
            "deployment configuration has no Zodiac-local clock authority");

        var zodiac = File.ReadAllText(Path.Combine(
            server,
            "State",
            "ZodiacEnergyAccrual.cs"));
        var options = File.ReadAllText(Path.Combine(
            server,
            "ServerOptions.cs"));
        Check.True(
            !zodiac.Contains("ServerUtcOffset", StringComparison.Ordinal) &&
            !zodiac.Contains(".ToOffset(", StringComparison.Ordinal) &&
            zodiac.Contains("RealmCalendar", StringComparison.Ordinal) &&
            !options.Contains(
                "GODSWAR_ZODIAC_SERVER_UTC_OFFSET_MINUTES",
                StringComparison.Ordinal),
            "Zodiac calendar derivation has one shared realm authority");

        var faction = string.Join('\n', Directory.EnumerateFiles(
                Path.Combine(server, "Application", "FactionCrier"),
                "*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText));
        Check.True(
            !faction.Contains("ServerUtcOffset", StringComparison.Ordinal) &&
            !faction.Contains(".ToOffset(", StringComparison.Ordinal) &&
            faction.Contains("RealmCalendar", StringComparison.Ordinal),
            "Faction Crier days and weeks have no balance-local clock");

        var holySuit = string.Join('\n', Directory.EnumerateFiles(
                Path.Combine(server, "Infrastructure", "Inventory"),
                "PostgresHolySuitCommandExecutor*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText));
        Check.True(
            !holySuit.Contains(".RealmDayTimeZone", StringComparison.Ordinal) &&
            !holySuit.Contains("AT TIME ZONE", StringComparison.Ordinal) &&
            !holySuit.Contains(
                "_realmCalendar.TimeZoneId",
                StringComparison.Ordinal) &&
            holySuit.Contains(
                "_realmCalendar.GetDay(envelope.ReceivedAt)",
                StringComparison.Ordinal) &&
            holySuit.Contains(
                "AND cb.server_id = @realmId",
                StringComparison.Ordinal) &&
            holySuit.Contains(
                "AND server_id = @realmId",
                StringComparison.Ordinal),
            "Holy Suit uses an exact .NET realm day and realm-scoped SQL");

        var progressionState = File.ReadAllText(Path.Combine(
            server,
            "Infrastructure",
            "Progression",
            "PostgresProgressionIntervalSettlementCommandExecutor.State.cs"));
        Check.True(
            progressionState.Split(
                "AND server_id = @realmId",
                StringSplitOptions.None).Length == 3 &&
            progressionState.Contains(
                "AND character.server_id = @realmId",
                StringComparison.Ordinal),
            "durable Zodiac progression SQL is fenced to the selected realm");

        var factionExecutor = File.ReadAllText(Path.Combine(
            server,
            "Infrastructure",
            "FactionCrier",
            "PostgresFactionCrierCommandExecutor.cs"));
        var factionReplay = File.ReadAllText(Path.Combine(
            server,
            "Infrastructure",
            "FactionCrier",
            "PostgresFactionCrierCommandExecutor.Replay.cs"));
        var factionState = File.ReadAllText(Path.Combine(
            server,
            "Infrastructure",
            "FactionCrier",
            "PostgresFactionCrierCommandExecutor.State.cs"));
        var executionRealmLock = factionExecutor.IndexOf(
            "LockCharacterAsync(",
            StringComparison.Ordinal);
        var executionInboxRead = factionExecutor.IndexOf(
            "ReadInboxAsync(",
            StringComparison.Ordinal);
        var replayRealmLock = factionReplay.IndexOf(
            "LockCharacterAsync(",
            StringComparison.Ordinal);
        var replayInboxRead = factionReplay.IndexOf(
            "ReadInboxAsync(",
            StringComparison.Ordinal);
        Check.True(
            factionExecutor.Contains(
                "envelope.Command.RealmId !=",
                StringComparison.Ordinal) &&
            factionExecutor.Contains(
                "_realmCalendar.RealmId.Value",
                StringComparison.Ordinal) &&
            factionReplay.Contains(
                "replayIntent.RealmId != _realmCalendar.RealmId.Value",
                StringComparison.Ordinal) &&
            factionState.Contains(
                "AND server_id = @realmId",
                StringComparison.Ordinal) &&
            executionRealmLock >= 0 &&
            executionInboxRead > executionRealmLock &&
            replayRealmLock >= 0 &&
            replayInboxRead > replayRealmLock,
            "Faction Crier validates realm and locks character before replay");

        var serverTime = File.ReadAllText(Path.Combine(
            server,
            "Packets",
            "PacketBuilder.Login.cs"));
        Check.True(
            serverTime.Contains(
                "nativeUtcBiasSeconds = checked(-realmUtcOffsetSeconds)",
                StringComparison.Ordinal) &&
            !serverTime.Contains(
                "OriginalServerUtcOffset",
                StringComparison.Ordinal),
            "client server-time emits the inverse native bias");
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "Godswar.Server",
                    "Godswar.Server.csproj")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Godswar repository root.");
    }
}
