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
    private const int ConcurrentPacketCount = 512;
    private const int ConcurrentPacketLength = 37;
    private const ushort ConcurrentPacketOpcode = 0x6F6F;

    public static async Task<int> Main(string[] args)
    {
        (string Name, Func<Task> Run)[] checks =
        [
            ("Strongly typed ECS kernel", EcsKernelChecks.RunAsync),
            ("Player runtime ECS shadow parity", PlayerRuntimeEcsShadowChecks.RunAsync),
            ("Reversible player runtime ECS cutover", PlayerRuntimeEcsCutoverChecks.RunAsync),
            ("Player and NPC ECS hydration parity", PlayerNpcEcsHydrationChecks.RunAsync),
            ("Per-map player and NPC ECS runtime cutover", MapEcsShadowChecks.RunAsync),
            ("Atomic map ECS publication and rollback", MapEcsRuntimeCutoverChecks.RunAsync),
            ("Online NPC revision and object-ID collision rollback", NpcCatalogRevisionChecks.RunAsync),
            ("Cross-map ECS transfer rollback state", MapEcsTransferRollbackChecks.RunAsync),
            ("Authoritative hidden live-map transfer", MapLiveTransferChecks.RunAsync),
            ("Native handler map-transition readiness", MapTransitionHandlerChecks.RunAsync),
            ("Native Sparta backhaul skill catalog", BackhaulSkillCatalogChecks.RunAsync),
            ("Authoritative Sparta backhaul casting", BackhaulSkillHandlerChecks.RunAsync),
            ("Authoritative map traversal catalog", MapTraversalCatalogChecks.RunAsync),
            ("Native local-player scene-change packet", MapSceneChangePacketChecks.RunAsync),
            ("Character position persistence epoch ordering", CharacterPositionPersistenceCoordinatorChecks.RunAsync),
            ("Player combat and committed-progression ECS parity", PlayerCombatEcsParityChecks.RunAsync),
            ("Live reversible player-combat ECS adapter", PlayerCombatEcsLiveAdapterChecks.RunAsync),
            ("Player movement ECS projection parity", PlayerMovementEcsParityChecks.RunAsync),
            ("Live reversible player-movement ECS adapter", PlayerMovementEcsLiveAdapterChecks.RunAsync),
            ("Monster-to-player damage ECS parity", MonsterPlayerDamageEcsParityChecks.RunAsync),
            ("Live reversible monster-to-player damage ECS adapter", MonsterPlayerDamageEcsLiveAdapterChecks.RunAsync),
            ("Data-boundary architecture ratchet", DataBoundaryArchitectureChecks.RunAsync),
            ("PostgreSQL migration safety foundation", PostgresMigrationFoundationChecks.RunAsync),
            ("PostgreSQL schema release migration paths", PostgresSchemaReleaseIntegrationChecks.RunAsync),
            ("PostgreSQL migration-prefix fixture", PostgresMigrationPrefixFixtureChecks.RunAsync),
            .. PetProtocolCheckCatalog.All,
            ("PostgreSQL forward-only database cleanup", PostgresDatabaseCleanupIntegrationChecks.RunAsync),
            ("PostgreSQL captured-monster ECS parity", PostgresMonsterEcsParityIntegrationChecks.RunAsync),
            ("Character camp starting location", CheckCharacterCampStartingLocationAsync),
            ("Saved character location persistence", CheckSavedCharacterLocationPersistenceAsync),
            ("Persistent monster-kill progression", CheckMonsterKillProgressionAsync),
            ("Additive fighter EXP boost stacking", CheckExperienceBoostStackingAsync),
            ("Online-only EXP and Talent boost duration", CheckOnlineProgressionBoostDurationAsync),
            ("World-session owned boost clock", CheckWorldSessionOwnedBoostClockAsync),
            ("Working-original login bootstrap manifest", CheckAfterLoginManifestAsync),
            ("Working-original character preview layout", CheckCharacterPreviewAsync),
            ("EnterMain character identity and saved location", CheckEnterMainCharacterIdentityAsync),
            ("Warrior talent ID-zero upgrade protocol", CheckWarriorTalentIdZeroUpgradeAsync),
            ("JSON warrior talent persistence", CheckJsonWarriorTalentPersistenceAsync),
            ("Warrior starter skill packets", CheckWarriorStarterSkillPacketsAsync),
            ("JSON provider starter skill", CheckJsonProviderStarterSkillAsync),
            ("Skill combat catalog", CheckSkillCombatCatalogAsync),
            ("Skill combat cast and cooldown timing", SkillCombatTimingCatalogChecks.RunAsync),
            ("Ordinary intoned combat skill lifecycle", IntonedCombatSkillHandlerChecks.RunAsync),
            ("Native mount Ride status and spawn protocol", CheckMountRideProtocolAsync),
            ("Mount and mount-gear Q20/G25 stat progression", MountEquipmentProgressionChecks.RunAsync),
            ("Immediate mount Ride dismount toggle", CheckImmediateMountRideDismountAsync),
            ("Atomic mount Ride activation commit", CheckAtomicMountRideActivationAsync),
            ("Sacred Zeal runtime-status composition", CheckSacredZealStatusCompositionAsync),
            ("Holy Ward runtime-status mitigation", CheckHolyWardStatusCompositionAsync),
            ("Skill cast target and impact layout", CheckSkillCastTargetAndImpactAsync),
            ("Native skill-cast interruption packet", SkillCastInterruptPacketChecks.RunAsync),
            (
                "Skill cast lifecycle cancellation races",
                BackhaulSkillHandlerChecks.RunCastingLifecycleRacesAsync),
            (
                "Skill cast authoritative interruption boundaries",
                BackhaulSkillHandlerChecks
                    .RunAuthoritativeInterruptionBoundariesAsync),
            ("Basic and monster attack packet layouts", CheckAttackPacketLayoutsAsync),
            ("Dynamic original-server time response", CheckServerTimePacketAsync),
            ("Zodiac full-sync and accumulation protocol", CheckZodiacProtocolAsync),
            ("Zodiac online-energy cadence and day policy", CheckZodiacOnlineEnergyPolicyAsync),
            ("JSON Zodiac creation persistence", CheckJsonZodiacPersistenceAsync),
            ("Zodiac level-up policy and protocol", CheckZodiacLevelUpgradeAsync),
            ("JSON Zodiac level-up persistence", CheckJsonZodiacLevelUpgradePersistenceAsync),
            ("Serialized Zodiac accrual and level-up", CheckZodiacLevelUpgradeSerializationAsync),
            ("PostgreSQL Zodiac level-up race", PostgresZodiacLevelUpgradeIntegrationChecks.RunAsync),
            ("Zodiac skill-grid activation and persistence", CheckZodiacSkillGridActivationAsync),
            ("Zodiac skill-grid upgrade and persistence", CheckZodiacSkillGridUpgradeAsync),
            ("PostgreSQL Zodiac skill-grid race", PostgresZodiacSkillGridIntegrationChecks.RunAsync),
            ("Player passive recovery protocol", CheckPlayerRecoveryProtocolAsync),
            ("PlayerWorldSpawn layout", CheckPlayerWorldSpawnAsync),
            ("PlayerWorldSpawn captured appearance", CheckPlayerWorldAppearanceAsync),
            ("PlayerWorldSpawn full quality/grade extension", CheckPlayerWorldExtendedAppearanceAsync),
            ("PlayerWorldSpawn mount overflow priority", CheckPlayerWorldMountOverflowPriorityAsync),
            ("Player auxiliary appearance packets", CheckPlayerAuxiliaryAppearanceAsync),
            ("PlayerInspectEquipment packed slots and details", CheckPlayerInspectExtendedSlotsAsync),
            ("PlayerDetail vitals and wallet layout", CheckPlayerDetailAsync),
            ("PlayerStatusUpdate layout", CheckPlayerStatusUpdateAsync),
            ("Native status-effect sync layout", CheckPlayerStatusEffectsAsync),
            ("Post-enter UI-ready bootstrap gate", CheckPostEnterBootstrapGateAsync),
            ("Captured accepted-quest replay exclusion", CheckCapturedAcceptedQuestReplayExclusionAsync),
            ("Guarded bag-to-equipment persistence and snapshot", CheckGuardedEquipmentMoveAsync),
            ("Rejected right-click equip authoritative slot", CheckRejectedEquipRefreshSlotAsync),
            ("Genuine equipment-kind persistence guard", EquipmentKindGuardChecks.RunAsync),
            ("Holy-stone targeted authoritative-item preservation", CheckHolyStoneAuthoritativePersistencePlanAsync),
            ("Occupied ghost-slot bag move parsing", CheckOccupiedGhostSlotBagMoveParsingAsync),
            ("Confirmed bag-item deletion protocol and persistence", CheckBagItemDeletionAsync),
            ("Developer material item command", CheckDeveloperForgingMaterialCommandAsync),
            ("Developer mount catalog, command, and JSON grant", DeveloperMountCommandChecks.RunAsync),
            ("PostgreSQL developer mount grant and audit", PostgresDeveloperMountIntegrationChecks.RunAsync),
            ("PostgreSQL developer clear-bag scope and audit", PostgresKitBagClearIntegrationChecks.RunAsync),
            ("Equipment forging packet protocol", ForgeProtocolChecks.RunAsync),
            ("Equipment forging rule catalog and calculator", EquipmentForgeCatalogChecks.RunAsync),
            ("Atomic equipment-forge persistence", ForgeTransactionChecks.RunAsync),
            ("PostgreSQL equipment-forge race and preservation", PostgresForgeIntegrationChecks.RunAsync),
            ("Gear-enhancement material catalog and planner", GearEnhancementStateChecks.RunAsync),
            ("Gear Mentor material, planner, and protocol", GearMentorStateChecks.RunAsync),
            ("PostgreSQL Gear Mentor race and preservation", PostgresGearMentorIntegrationChecks.RunAsync),
            ("Atomic gear-enhancement persistence", GearEnhancementTransactionChecks.RunAsync),
            ("PostgreSQL gear-enhancement race and preservation", PostgresGearEnhancementIntegrationChecks.RunAsync),
            ("Gear-enhancer initial NPC protocol", CheckGearEnhancerInitialProtocolAsync),
            ("Holy-suit design original NPC protocol", CheckHolySuitDesignProtocolAsync),
            ("NPC definitions and spawn layout", CheckNpcDefinitionsAndSpawnLayoutAsync),
            ("NPC multi-segment scene-key generation", NpcMultiSegmentSceneChecks.RunAsync),
            ("NPC movement-cell visibility", CheckNpcMovementCellVisibilityAsync),
            ("Monster movement-cell visibility and spawn layout", CheckMonsterMovementCellVisibilityAsync),
            ("World boss outdoor-area catalog", WorldBossCatalogChecks.RunAsync),
            ("Persisted world-boss respawn across restart", CheckPersistedWorldBossRespawnAsync),
            ("Monster ECS shadow parity", MonsterEcsParityChecks.RunAsync),
            ("Reversible monster runtime cutover", MonsterRuntimeCutoverChecks.RunAsync),
            ("Monster movement and lifecycle packet layouts", CheckMonsterMovementPacketLayoutsAsync),
            ("Monster runtime appearance patch", CheckMonsterRuntimeAppearancePatchAsync),
            ("Shared bounded monster runtime and lifecycle", CheckSharedBoundedMonsterRuntimeAsync),
            ("Warrior stun monster-control runtime", MonsterStunChecks.RunAsync),
            ("Passive monster retaliation state machine", CheckMonsterRetaliationRuntimeAsync),
            ("Monster smooth leash return and full-health replacement", CheckMonsterLeashReturnAsync),
            ("Monster return/replacement socket lifecycle", CheckMonsterReturnViewerPacketOrderAsync),
            ("Monster generation reconciliation across bootstrap", CheckMonsterGenerationReconciliationAsync),
            ("Monster old-generation event packet suppression", CheckMonsterOldGenerationEventSuppressionAsync),
            ("Monster same-generation activation refresh", CheckMonsterSameGenerationActivationRefreshAsync),
            ("Monster entering-viewer damage delivery lease", CheckMonsterEnteringViewerDamageLeaseAsync),
            ("Monster health-revision inverse and gap ordering", CheckMonsterHealthRevisionOrderingAsync),
            ("Monster self-viewer inverse damage ordering", CheckMonsterSelfViewerDamageOrderingAsync),
            ("Monster area-damage AOI revision delivery", CheckMonsterAreaDamageDeliveryAsync),
            ("Monster viewer registry AOI scoping", CheckMonsterViewerRegistryAsync),
            ("Map registry world-readiness gate", CheckMapRegistryWorldReadinessAsync),
            (
                "Password authentication primitives",
                PasswordAuthenticationPrimitiveChecks.RunAsync),
            (
                "Bounded password KDF scheduler",
                PasswordKdfSchedulerChecks.RunAsync),
            (
                "JSON account authentication and migration",
                AccountAuthenticationJsonChecks.RunAsync),
            (
                "Registration collision plaintext authentication",
                RegistrationCollisionAuthenticationChecks.RunAsync),
            ("Secure Phase 2 bounded protocol codecs", SecureProtocolCodecChecks.RunAsync),
            ("Secure Phase 2 legacy transport parity", LegacyByteTransportChecks.RunAsync),
            ("Secure Phase 2 bounded network lifecycle", NetworkRuntimeLifecycleChecks.RunAsync),
            ("Secure Phase 2 TLS mux transport", SecureTlsTransportChecks.RunAsync),
            (
                "Secure Phase 2 single-use game ticket authority",
                SecureGameTicketStoreChecks.RunAsync),
            (
                "Secure Phase 2 authenticated grant and principal flow",
                SecureLoginTicketFlowChecks.RunAsync),
            (
                "Secure Phase 2 authentication idle transition",
                ClientSessionRuntimeChecks.RunSecureAuthenticationIdleTransitionAsync),
            (
                "Mutually exclusive raw or secure listener profile",
                ServerListenerProfileChecks.RunAsync),
            (
                "Fail-closed server runtime and storage profiles",
                ServerRuntimeProfileChecks.RunAsync),
            (
                "Local-development-only legacy authentication",
                LegacyAuthenticationProfileChecks.RunAsync),
            (
                "Controlled-host exact Npgsql validation",
                ControlledHostValidationCommandChecks.RunAsync),
            (
                "Controlled-host exact TLS certificate policy",
                ControlledHostCertificateValidationChecks.RunAsync),
            (
                "Controlled-host exact acceptance options",
                ControlledHostAcceptancePolicyChecks.RunAsync),
            (
                "Controlled-host privacy-safe evidence",
                ControlledHostPrivacyEvidenceChecks.RunAsync),
            (
                "Controlled-host same-user graceful shutdown",
                ControlledHostShutdownControlChecks.RunAsync),
            (
                "Secure Phase 3 UDP Slice 9A/9B binding foundation",
                SecureUdpFoundationChecks.RunAsync),
            (
                "Secure Phase 3 UDP bounded loopback baseline",
                SecureUdpAdmissionBaselineChecks.RunAsync),
            (
                "Secure Phase 4 realtime movement protocol",
                SecureRealtimeMovementProtocolChecks.RunAsync),
            (
                "Secure Phase 4 realtime session authority",
                SecureRealtimeSessionAuthorityChecks.RunAsync),
            (
                "Secure Phase 4 authoritative movement policy",
                AuthoritativePlayerMovementSystemChecks.RunAsync),
            (
                "Secure Phase 4 deterministic network emulation and overload",
                SecurePhase4NetworkEmulationChecks.RunAsync),
            (
                "Secure Phase 4 controlled-host fault injection",
                SecurePhase4AcceptanceFaultChecks.RunAsync),
            (
                "Secure Phase 4 game-handler movement integration",
                SecureRealtimeHandlerIntegrationChecks.RunAsync),
            (
                "Secure Phase 5A deterministic movement replay",
                Phase5DeterministicMovementReplayChecks.RunAsync),
            (
                "Secure Phase 5A realtime decoder fuzz",
                Phase5RealtimeDecoderFuzzChecks.RunAsync),
            (
                "Secure Phase 5A simulation loop observability",
                SimulationLoopMetricsChecks.RunAsync),
            (
                "Secure Phase 5A operational state metrics",
                OperationalStateMetricsChecks.RunAsync),
            ("ClientSession concurrent send ordering", CheckConcurrentSendOrderingAsync)
        ];

        var filters = args
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (filters.Length > 0)
        {
            checks = checks
                .Where(check => filters.Any(filter =>
                    check.Name.Contains(
                        filter,
                        StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (checks.Length == 0)
            {
                Console.Error.WriteLine(
                    $"No protocol check matched: {string.Join(", ", filters)}");
                return 2;
            }
        }

        var failures = 0;
        foreach (var check in checks)
        {
            try
            {
                await check.Run();
                Console.WriteLine($"PASS {check.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {check.Name}: {ex}");
            }
        }

        Console.WriteLine($"Protocol checks: {checks.Length - failures} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}

internal static class Check
{
    public static void Equal<T>(T expected, T actual, string description)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}.");
        }
    }

    public static void True(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {description}.");
        }
    }

    public static void Throws<TException>(Action action, string description)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {typeof(TException).Name}.");
    }
}
