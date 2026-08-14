using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class PetHealingTalentLiveAdapterChecks
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 11, 2, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        var character = Character();
        var objectId = WorldObjectIds.ForPlayer(character.Id);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId,
            joinedAt: Start);
        Check.True(registry.UpdateActivePetHealingRuntime(
                socket.Session,
                [Pet(petId: 70, summoned: true)]),
            "joined session accepts active pet Healing projection");

        var applied = registry.ResolvePlayerVitalsDamageEcs(
            socket.Session,
            character,
            objectId,
            Request(
                eventId: 1,
                character,
                objectId,
                resolvedAt: Start,
                damage: 10));
        Check.True(applied.PetHealing is
                { AppliedHealing: 25, ResolvedHealing: 25 },
            "live adapter exposes quality-scaled pet Healing decision");
        Check.Equal(40, applied.AfterHealth,
            "damage decision retains post-hit HP");
        Check.Equal(65, applied.FinalHealth,
            "live decision exposes final healed HP");
        Check.Equal(65, character.CurrentHp,
            "live adapter commits final healed HP");
        Check.Equal(2L, character.VitalsRevision,
            "live adapter commits damage then Healing revisions");

        registry.Remove(socket.Session);
        Restore(character, currentHp: 50);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId,
            joinedAt: Start.AddSeconds(10));
        registry.UpdateActivePetHealingRuntime(
            socket.Session,
            [Pet(petId: 70, summoned: true)]);

        var beforeReady = registry.ResolvePlayerVitalsDamageEcs(
            socket.Session,
            character,
            objectId,
            Request(
                eventId: 1,
                character,
                objectId,
                resolvedAt: Start.AddSeconds(179),
                damage: 10));
        Check.True(beforeReady.PetHealing is null,
            "reconnect does not clear process Healing cooldown");
        Check.Equal(40, character.CurrentHp,
            "reconnected cooldown still permits authoritative damage");

        Restore(character, currentHp: 50);
        var atReady = registry.ResolvePlayerVitalsDamageEcs(
            socket.Session,
            character,
            objectId,
            Request(
                eventId: 2,
                character,
                objectId,
                resolvedAt: Start.AddSeconds(180),
                damage: 10));
        Check.True(atReady.PetHealing is not null,
            "reconnected owner can heal exactly when cooldown expires");

        Restore(character, currentHp: 50);
        registry.UpdateActivePetHealingRuntime(
            socket.Session,
            [Pet(petId: 70, summoned: false)]);
        var recalled = registry.ResolvePlayerVitalsDamageEcs(
            socket.Session,
            character,
            objectId,
            Request(
                eventId: 3,
                character,
                objectId,
                resolvedAt: Start.AddSeconds(400),
                damage: 10));
        Check.True(recalled.PetHealing is null,
            "refreshed recalled-pet projection cannot heal");

        registry.Remove(socket.Session);
    }

    private static PlayerMonsterDamageEcsRequest Request(
        ulong eventId,
        GameCharacter character,
        uint objectId,
        DateTimeOffset resolvedAt,
        uint damage) =>
        new(
            eventId,
            MonsterObjectId: 9_001,
            MonsterSpawnGeneration: 1,
            ExpectedCharacterId: character.Id,
            ExpectedPlayerObjectId: objectId,
            ExpectedLifeRevision: 0,
            ExpectedVitalsRevision: character.VitalsRevision,
            ResolvedDamage: damage,
            ResolvedAt: resolvedAt);

    private static PetBootstrapSnapshot Pet(
        long petId,
        bool summoned) =>
        new(
            petId,
            AccountId: 821,
            OwnerCharacterId: 9_771,
            SpeciesId: 1,
            Name: "HealingPet",
            Sex: 1,
            Level: 120,
            Experience: 0,
            Aptitude: PetAptitude.Transcendent,
            Rank: 1,
            CompletedRebirths: 0,
            RebirthsRemaining: 0,
            CompletedPetMerges: 0,
            HasSoulContract: false,
            HasOwnerMergeTalent: false,
            CurrentEnergy: 100,
            MaximumEnergy: 100,
            Amity: 100,
            Satiety: 100,
            RemainingLifetime: 100,
            AvailableStatPoints: 0,
            GrowthRevealed: true,
            IsBound: true,
            ActivityState: "idle",
            IsCarried: true,
            IsSummoned: summoned,
            ContributesToCharacter: false,
            Revision: 1,
            CreatedAt: Start,
            UpdatedAt: Start,
            StatValues: [],
            CharacterBonuses: [],
            Skills: [],
            TalentMask: 8);

    private static void Restore(
        GameCharacter character,
        int currentHp)
    {
        lock (character.VitalsSync)
        {
            character.CurrentHp = currentHp;
            character.MarkVitalsChanged();
        }
    }

    private static GameCharacter Character() =>
        new()
        {
            Id = 9_771,
            AccountId = 821,
            Name = "PetHealingOwner",
            CreatedUtc = Start.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = 0,
            PositionX = 100f,
            PositionZ = 100f,
            Level = 10,
            CurrentHp = 50,
            MaxHp = 100,
            CurrentMp = 40,
            MaxMp = 40,
            CalculatedStats = new CharacterStats()
        };
}
