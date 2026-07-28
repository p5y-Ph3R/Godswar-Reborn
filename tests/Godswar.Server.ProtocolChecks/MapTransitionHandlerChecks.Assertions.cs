using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MapTransitionHandlerChecks
{
    private static async Task AssertDetailAndCompletedTransitionAsync(
        RuntimePolicySessionSocket socket,
        GameCharacter character,
        string description)
    {
        await AssertNextPacketAsync(
            socket,
            PacketBuilder.PlayerDetail(character),
            $"{description} player detail");
        await AssertNextPacketAsync(
            socket,
            PacketBuilder.PlayerStatusUpdate(character, 1f),
            $"{description} detail status");
        await AssertNextPacketAsync(
            socket,
            PacketBuilder.PetWorldPresence(
                1,
                LocalPlayerObjectId),
            $"{description} summoned-pet restore");
        await AssertNextPacketAsync(
            socket,
            PacketBuilder.PlayerStatusUpdate(character, 1f),
            $"{description} completed status");
        await AssertNextPacketAsync(
            socket,
            PacketBuilder.PlayerStatusEffects(
                character,
                [],
                ClientStatusAggregate.Empty),
            $"{description} completed status effects");
    }

    private static async Task AssertNextPacketAsync(
        RuntimePolicySessionSocket socket,
        byte[] expected,
        string description)
    {
        var actual = await socket.ReadPacketAsync(expected.Length);
        Check.True(
            actual.SequenceEqual(expected),
            $"{description} matches byte-for-byte");
    }

    private static void AssertPersistedPosition(
        MapTransitionStore store,
        int index,
        byte expectedMapId,
        MapTraversalPosition expected,
        string description)
    {
        Check.Equal(
            index + 1,
            store.PositionWrites.Count,
            $"{description} persists exactly once");
        var write = store.PositionWrites[index];
        Check.True(
            write.AccountId == AccountId &&
            write.CharacterId == CharacterId,
            $"{description} persists the active identity");
        Check.Equal(
            expectedMapId,
            write.MapId,
            $"{description} persisted map");
        Check.Equal(
            expected.X,
            write.X,
            $"{description} persisted X");
        Check.Equal(
            expected.Z,
            write.Z,
            $"{description} persisted Z");
    }

    private static void AssertHiddenDestination(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character,
        byte sourceMapId,
        byte targetMapId,
        string description)
    {
        Check.Equal(
            targetMapId,
            character.CurrentMap,
            $"{description} updates authoritative map");
        Check.True(
            !registry.GetMapSessions(
                    sourceMapId)
                .Any(context =>
                    ReferenceEquals(context.Session, session)),
            $"{description} removes source membership");
        Check.Equal(
            1,
            registry.GetMapPopulation(targetMapId),
            $"{description} destination ECS owns hidden player");
        Check.True(
            !registry.GetMapSessions(
                    targetMapId)
                .Any(context =>
                    ReferenceEquals(context.Session, session)),
            $"{description} hides destination from world readers");
    }

    private static void AssertActiveDestination(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character,
        byte mapId,
        string description)
    {
        var active = registry.GetMapSessions(mapId)
            .Single(context =>
                ReferenceEquals(context.Session, session));
        Check.True(
            active.WorldReady &&
            ReferenceEquals(active.Character, character),
            $"{description} activates the authoritative character");
    }

    private static void AssertNoFullBootstrapReplay(
        GameClientHandler handler,
        MapTransitionStore store,
        int expectedPetPresenceReads,
        string description)
    {
        Check.True(
            !GetBooleanField(handler, "_postEnterBootstrapSent"),
            $"{description} does not mark full bootstrap sent");
        Check.Equal(
            0,
            store.EnterSyncRequests,
            $"{description} does not replay captured enter sync");
        Check.Equal(
            0,
            store.SkillStateRequests,
            $"{description} does not replay skill bootstrap");
        Check.Equal(
            0,
            store.TalentStateRequests,
            $"{description} does not replay talent bootstrap");
        Check.Equal(
            expectedPetPresenceReads,
            store.PetPresenceReads,
            $"{description} restores pet presence once at world readiness");
    }

    private static GameCharacter CreateCharacter(
        int characterId,
        int accountId,
        string name,
        byte mapId,
        float x,
        float z) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = name,
            CreatedUtc = TestTime.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = mapId,
            PositionX = x,
            PositionZ = z,
            Level = 20,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private static PetBootstrapSnapshot CreateSummonedPet(
        GameCharacter character) =>
        new(
            PetId: 1,
            AccountId: character.AccountId,
            OwnerCharacterId: character.Id,
            SpeciesId: 37,
            Name: "Transition Lion",
            Sex: 1,
            Level: 20,
            Experience: 0,
            Aptitude: PetAptitude.Godly,
            Rank: 1m,
            CompletedRebirths: 0,
            RebirthsRemaining: 0,
            CompletedPetMerges: 0,
            HasSoulContract: false,
            HasOwnerMergeTalent: false,
            CurrentEnergy: 100,
            MaximumEnergy: 100,
            Amity: 100,
            Satiety: 100,
            RemainingLifetime: 1_200,
            AvailableStatPoints: 0,
            GrowthRevealed: true,
            IsBound: false,
            ActivityState: "owned",
            IsCarried: true,
            IsSummoned: true,
            ContributesToCharacter: false,
            Revision: 1,
            CreatedAt: TestTime,
            UpdatedAt: TestTime,
            StatValues: [],
            CharacterBonuses: [],
            Skills: []);
}
