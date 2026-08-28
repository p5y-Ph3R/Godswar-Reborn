using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckCharacterEffectAuthorityAsync()
    {
        await CheckUnboundCharacterEffectAuthorityAsync();
        await CheckBoundCharacterEffectAuthorityAsync();
        await CheckEffectMembershipEpochIsolationAsync(
            "E1-Elite",
            MedusaEncounterEffectKind.Stun);
        await CheckEffectMembershipEpochIsolationAsync(
            "Final-Pikeman-1",
            MedusaEncounterEffectKind.OutgoingPhysicalAmplifier);
        await CheckCharacterEffectDepartureCleanupAsync();
    }

    private static async Task CheckUnboundCharacterEffectAuthorityAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = new GameSessionRegistry();
        var character = CreateRegistryDamageCharacter(901, mapId: 200);
        var created = await registry.CreateLocalWorldInstanceAsync(
            RealmId.Tempest,
            new MapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            CancellationToken.None);
        var runtime = created.Runtime ??
            throw new InvalidOperationException(
                "Explicit unbound Medusa test runtime was not created.");
        registry.JoinWorldInstance(
            socket.Session,
            character.AccountId,
            character,
            objectId: 0x79A1,
            runtime.InstanceId,
            worldReady: true,
            joinedAt: StartedAt);

        var resolved = registry.ResolveMedusaCharacterEffectAuthority(
            socket.Session,
            StartedAt);
        Check.True(
            resolved.Outcome ==
                MedusaCharacterEffectAuthorityOutcome.Unbound &&
            resolved.View is null &&
            registry.IsMedusaActionAllowed(
                socket.Session,
                MedusaEncounterControlRestriction.AllActions,
                StartedAt,
                out var actionAuthority) &&
            actionAuthority.Outcome ==
                MedusaCharacterEffectAuthorityOutcome.Unbound,
            "an ordinary map-200 runtime remains byte-compatible and unrestricted when no Medusa owner is bound");
        registry.Remove(socket.Session);
    }

    private static async Task CheckBoundCharacterEffectAuthorityAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var eventId = fixture.FindEvent(
            start: 5_000_000,
            static resolution => resolution.Hit &&
                resolution.Damage > 0);
        _ = await fixture.AttackAsync(
            fixture.CreateAttack(eventId));
        var effect = fixture.Mechanics().ActiveEffects.Single();
        var before = fixture.MechanicsSnapshot();

        fixture.Registry.UpdateCharacter(
            fixture.Socket.Session,
            fixture.Character,
            advanceWorldRevision: true);
        var currentContext = fixture.Map.Snapshot().Single(context =>
            ReferenceEquals(context.Session, fixture.Socket.Session));

        var resolved = fixture.Registry
            .ResolveMedusaCharacterEffectAuthority(
                fixture.Socket.Session,
                effect.AppliedAt);
        var actions = new[]
        {
            MedusaEncounterControlRestriction.Movement,
            MedusaEncounterControlRestriction.BasicAttack,
            MedusaEncounterControlRestriction.SkillCast,
            MedusaEncounterControlRestriction.ItemUse
        };
        Check.True(
            resolved is
            {
                Outcome:
                    MedusaCharacterEffectAuthorityOutcome.ResolvedActive,
                View: { } view
            } &&
            view.CharacterId == fixture.Character.Id &&
            view.EffectTarget == new MedusaEncounterEffectTarget(
                fixture.Ownership,
                LifeRevision: 0,
                currentContext.WorldMembershipEpoch) &&
            view.ActiveEffects.Single().Definition.Kind ==
                MedusaEncounterEffectKind.Stun &&
            currentContext.WorldRevision !=
                fixture.Map.Descriptor.Revision &&
            actions.All(action =>
                !fixture.Registry.IsMedusaActionAllowed(
                    fixture.Socket.Session,
                    action,
                    effect.AppliedAt,
                    out var authority) &&
                authority.Outcome ==
                    MedusaCharacterEffectAuthorityOutcome
                        .ResolvedActive),
            "a live exact-life stun blocks movement, attacks, skills, and item activation");

        var copiedContext = currentContext with
        {
            WorldReady = currentContext.WorldReady
        };
        var staleMembership = fixture.Map
            .ResolveMedusaCharacterEffectsForSessionGuarded(
                copiedContext,
                expectedLifeRevision: 0,
                registryAuthorityCurrent: true,
                effect.AppliedAt);
        Check.True(
            staleMembership.Outcome ==
                MedusaCharacterEffectAuthorityOutcome
                    .CurrentMembershipRequired &&
            staleMembership.ShouldFailClosed &&
            !staleMembership.Allows(
                MedusaEncounterControlRestriction.Movement),
            "a value-equal but non-current map membership fails closed");

        var expired = fixture.Registry
            .ResolveMedusaCharacterEffectAuthority(
                fixture.Socket.Session,
                effect.ExpiresAt);
        var afterPureQuery = fixture.MechanicsSnapshot();
        Check.True(
            expired is
            {
                Outcome:
                    MedusaCharacterEffectAuthorityOutcome.ResolvedActive,
                View: { ActiveEffects.IsEmpty: true }
            } &&
            expired.Allows(
                MedusaEncounterControlRestriction.AllActions) &&
            afterPureQuery.LastObservedAt == before.LastObservedAt &&
            fixture.Mechanics().ActiveEffects.Length == 1,
            "expiration is exclusive and command resolution does not mutate owner clocks");

        Check.Equal(
            1L,
            fixture.Registry.AdvancePlayerLifeRevision(
                fixture.Socket.Session,
                effect.AppliedAt.AddMilliseconds(1)),
            "life authority advances once");
        Check.True(
            fixture.Mechanics().ActiveEffects.IsEmpty &&
            fixture.Registry.IsMedusaActionAllowed(
                fixture.Socket.Session,
                MedusaEncounterControlRestriction.AllActions,
                effect.AppliedAt.AddMilliseconds(1),
                out var revived) &&
            revived.Outcome ==
                MedusaCharacterEffectAuthorityOutcome.ResolvedActive,
            "central life advance reclaims the exact expired-life effect");
    }

    private static async Task CheckCharacterEffectDepartureCleanupAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var eventId = fixture.FindEvent(
            start: 5_100_000,
            static resolution => resolution.Hit &&
                resolution.Damage > 0);
        _ = await fixture.AttackAsync(
            fixture.CreateAttack(eventId));
        Check.True(
            fixture.Mechanics().ActiveEffects.Length == 1 &&
            fixture.Registry.Remove(
                fixture.Socket.Session,
                fixture.Ownership) &&
            fixture.Mechanics().ActiveEffects.IsEmpty,
            "exact ownership departure clears its retained encounter effects before map removal");
    }

    private static async Task CheckEffectMembershipEpochIsolationAsync(
        string rosterSpawnId,
        MedusaEncounterEffectKind expectedKind)
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(rosterSpawnId);
        var eventId = fixture.FindEvent(
            start: 5_150_000,
            static resolution => resolution.Hit &&
                resolution.Damage > 0);
        _ = await fixture.AttackAsync(fixture.CreateAttack(eventId));
        var retained = fixture.Mechanics().ActiveEffects.Single();
        var original = fixture.Map.Snapshot().Single(context =>
            ReferenceEquals(context.Session, fixture.Socket.Session));
        Check.True(
            retained.Definition.Kind == expectedKind &&
            retained.TargetWorldMembershipEpoch ==
                original.WorldMembershipEpoch &&
            fixture.Map.Remove(fixture.Socket.Session, out _),
            $"{expectedKind} fixture retains its original exact epoch");

        var rejoined = original with
        {
            WorldMembershipEpoch = checked(
                original.WorldMembershipEpoch + 1)
        };
        fixture.Map.AddOrUpdate(rejoined);
        var resolved = fixture.Map
            .ResolveMedusaCharacterEffectsForSessionGuarded(
                rejoined,
                expectedLifeRevision: 0,
                registryAuthorityCurrent: true,
                retained.AppliedAt);

        Check.True(
            resolved is
            {
                Outcome:
                    MedusaCharacterEffectAuthorityOutcome.ResolvedActive,
                View:
                {
                    ActiveEffects.IsEmpty: true,
                    ControlRestriction:
                        MedusaEncounterControlRestriction.None,
                    PhysicalOutgoingDamageMultiplier: 1,
                    MagicalOutgoingDamageMultiplier: 1
                }
            } &&
            resolved.Allows(
                MedusaEncounterControlRestriction.AllActions) &&
            fixture.Mechanics().ActiveEffects.Single()
                .TargetWorldMembershipEpoch ==
                    original.WorldMembershipEpoch,
            $"old {expectedKind} authority cannot cross a same-session " +
            "remove/rejoin epoch even when ownership and life match");
        _ = fixture.Map.Remove(fixture.Socket.Session, out _);
    }
}
