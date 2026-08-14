using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal sealed partial class PetDurableHandlerFixture : IAsyncDisposable
{
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private PetDurableHandlerFixture(
        ClientSession session,
        PetDurableCaptureTransport transport,
        GameClientHandler handler,
        GameSessionRegistry registry,
        PetHandlerStore store)
    {
        Session = session;
        Transport = transport;
        Handler = handler;
        Registry = registry;
        _store = store;
    }

    public ClientSession Session { get; }

    public PetDurableCaptureTransport Transport { get; }

    public GameClientHandler Handler { get; }

    public GameSessionRegistry Registry { get; }

    public static PetDurableHandlerFixture Create(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter,
        IReadOnlyList<PetBootstrapSnapshot> persistedPets,
        IPetDurableCommandExecutor executor,
        short openedPetShedCells =
            PetShedCapacityPolicy.DefaultOpenedCellCount,
        TimeSpan? petOwnerMergeEnergyInterval = null,
        ISealedPetSnapshotReader? sealedPetSnapshots = null,
        CharacterCalculatedStatsSnapshot? persistedStats = null)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        ArgumentNullException.ThrowIfNull(persistedPets);
        ArgumentNullException.ThrowIfNull(executor);

        var transport = new PetDurableCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            liveCharacter.AccountId,
            liveCharacter);
        var snapshot = CreateSnapshot(
            persistedCharacter,
            persistedPets,
            openedPetShedCells,
            persistedStats);
        var store = new PetHandlerStore();
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            new FixedSnapshotReader(snapshot),
            WorldContentReaderTestFixtures.Empty,
            petDurableCommands: executor,
            petContent: PetContentTestCatalog.Instance,
            petOwnerMergeEnergyInterval: petOwnerMergeEnergyInterval,
            sealedPetSnapshots: sealedPetSnapshots);
        SetField(
            handler,
            "_account",
            new AccountIdentity(
                liveCharacter.AccountId,
                "durable-pet-check"));
        SetField(handler, "_character", liveCharacter);
        return new(session, transport, handler, registry, store);
    }

    public async Task InvokeAsync(GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            Handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;
    }

    public ValueTask DisposeAsync() => Session.DisposeAsync();

    internal static CharacterAccountSnapshot CreateSnapshot(
        GameCharacter character,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        short openedPetShedCells =
            PetShedCapacityPolicy.DefaultOpenedCellCount,
        CharacterCalculatedStatsSnapshot? persistedStats = null)
    {
        var basis = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var current = basis.Character ??
            throw new InvalidOperationException(
                "The shared character snapshot fixture is empty.");
        var loadout = current.Loadout with
        {
            Equipment = character.Equipment,
            KitBag = character.KitBag,
            WeaponRank = character.WeaponRank,
            WeaponAuraEffect = character.WeaponAuraEffect,
            ArmorRank = character.ArmorRank,
            ArmorAuraEffect = character.ArmorAuraEffect
        };
        var stats = (persistedStats ?? current.CalculatedStats) with
        {
            CharacterId = character.Id,
            AccountId = character.AccountId,
            Name = character.Name,
            WeaponRank = character.WeaponRank,
            WeaponAuraEffect = character.WeaponAuraEffect,
            ArmorRank = character.ArmorRank,
            ArmorAuraEffect = character.ArmorAuraEffect
        };
        var vitals = persistedStats is null
            ? current.Vitals
            : current.Vitals with
            {
                PersistedCurrentHp = stats.CurrentHp,
                PersistedCurrentMp = stats.CurrentMp,
                Revision = character.VitalsRevision
            };
        var snapshot = current with
        {
            Identity = current.Identity with
            {
                CharacterId = character.Id,
                AccountId = character.AccountId,
                Name = character.Name
            },
            Loadout = loadout,
            Vitals = vitals,
            CalculatedStats = stats,
            PetShed = new CharacterPetShedSnapshot(
                openedPetShedCells,
                0),
            Pets = pets.Select(ToApplicationPet).ToImmutableArray()
        };
        return basis with
        {
            AccountId = character.AccountId,
            ProviderSnapshotToken = "durable-pet-handler-check",
            Character = snapshot
        };
    }

    private static CharacterPetSnapshot ToApplicationPet(
        PetBootstrapSnapshot pet) =>
        new(
            pet.PetId,
            pet.AccountId,
            pet.OwnerCharacterId,
            pet.SpeciesId,
            pet.Name,
            pet.Sex,
            pet.Level,
            pet.Experience,
            checked((short)pet.Aptitude),
            pet.Rank,
            pet.CompletedRebirths,
            pet.RebirthsRemaining,
            pet.CompletedPetMerges,
            pet.HasSoulContract,
            pet.HasOwnerMergeTalent,
            pet.CurrentEnergy,
            pet.MaximumEnergy,
            pet.Amity,
            pet.Satiety,
            pet.RemainingLifetime,
            pet.AvailableStatPoints,
            pet.GrowthRevealed,
            pet.IsBound,
            pet.ActivityState,
            pet.IsCarried,
            pet.IsSummoned,
            pet.ContributesToCharacter,
            pet.Revision,
            pet.CreatedAt,
            pet.UpdatedAt,
            pet.StatValues.Select(static value =>
                new CharacterPetStatValueSnapshot(
                    value.StatCode,
                    value.InitialSavvy,
                    value.AddedSavvy,
                    value.BaseGrowthRate,
                    value.GrowthAcceleration,
                    value.Revision,
                    value.BirthInitialSavvy,
                    value.RarityAddedSavvy)).ToImmutableArray(),
            pet.CharacterBonuses.Select(static bonus =>
                new CharacterPetBonusSnapshot(
                    bonus.EffectCode,
                    bonus.EffectValue,
                    bonus.Revision)).ToImmutableArray(),
            pet.Skills.Select(static skill =>
                new CharacterPetSkillSnapshot(
                    skill.SkillId,
                    skill.SlotIndex,
                    skill.SkillRank,
                    skill.SkillExperience,
                    skill.IsActive,
                    skill.Revision)).ToImmutableArray(),
            pet.OpenedSkillSlots,
            pet.AvailableSkillSlots,
            pet.TalentMask,
            SoulContractStage: pet.SoulContractStage);

    private sealed class FixedSnapshotReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(snapshot.AccountId, accountId, "pet snapshot account");
            return Task.FromResult(snapshot);
        }
    }
}

internal partial class DelegatingPetDurableCommandExecutor :
    IPetDurableCommandExecutor
{
    public Func<CommandEnvelope<BagItemActivationCommand>,
        PetDurableExecutionResult>? Activate { get; init; }

    public Func<CommandEnvelope<PetLevelUpgradeCommand>,
        PetDurableExecutionResult>? Upgrade { get; init; }

    public Func<CommandEnvelope<PetPresenceTransitionCommand>,
        PetDurableExecutionResult>? Transition { get; init; }

    public Func<CommandEnvelope<PetSkillUnlearnCommand>,
        PetDurableExecutionResult>? UnlearnSkill { get; init; }

    public Func<CommandEnvelope<PetGrowthResetCommand>,
        PetDurableExecutionResult>? ResetGrowth { get; init; }

    public Func<CommandEnvelope<PetBasicSavvyResetCommand>,
        PetDurableExecutionResult>? ResetBasicSavvy { get; init; }

    public Func<CommandEnvelope<PetOwnerMergeToggleCommand>,
        PetDurableExecutionResult>? ToggleOwnerMerge { get; init; }

    public Func<CommandEnvelope<PetToPetMergeCommand>,
        PetDurableExecutionResult>? MergePets { get; init; }

    public Func<CommandEnvelope<PetRebirthCommand>,
        PetDurableExecutionResult>? RebirthPet { get; init; }

    public int ActivateCount { get; private set; }

    public int UpgradeCount { get; private set; }

    public int TransitionCount { get; private set; }

    public int UnlearnSkillCount { get; private set; }

    public int ResetGrowthCount { get; private set; }

    public int ResetBasicSavvyCount { get; private set; }

    public int ToggleOwnerMergeCount { get; private set; }

    public int MergePetsCount { get; private set; }

    public int RebirthPetCount { get; private set; }

    public CommandEnvelope<BagItemActivationCommand>? ActivationEnvelope
    { get; private set; }

    public CommandEnvelope<PetLevelUpgradeCommand>? UpgradeEnvelope
    { get; private set; }

    public CommandEnvelope<PetPresenceTransitionCommand>? TransitionEnvelope
    { get; private set; }

    public CommandEnvelope<PetSkillUnlearnCommand>? UnlearnSkillEnvelope
    { get; private set; }

    public CommandEnvelope<PetGrowthResetCommand>? ResetGrowthEnvelope
    { get; private set; }

    public CommandEnvelope<PetBasicSavvyResetCommand>?
        ResetBasicSavvyEnvelope { get; private set; }

    public CommandEnvelope<PetOwnerMergeToggleCommand>?
        ToggleOwnerMergeEnvelope { get; private set; }

    public CommandEnvelope<PetToPetMergeCommand>? MergePetsEnvelope
    { get; private set; }

    public CommandEnvelope<PetRebirthCommand>? RebirthPetEnvelope
    { get; private set; }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<BagItemActivationCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActivateCount++;
        ActivationEnvelope = envelope;
        return Task.FromResult(
            (Activate ?? throw Missing("activation"))(envelope));
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetLevelUpgradeCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpgradeCount++;
        UpgradeEnvelope = envelope;
        return Task.FromResult(
            (Upgrade ?? throw Missing("level upgrade"))(envelope));
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetPresenceTransitionCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransitionCount++;
        TransitionEnvelope = envelope;
        return Task.FromResult(
            (Transition ?? throw Missing("presence transition"))(envelope));
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetSkillUnlearnCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnlearnSkillCount++;
        UnlearnSkillEnvelope = envelope;
        return Task.FromResult(
            (UnlearnSkill ?? throw Missing("skill unlearn"))(envelope));
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetGrowthResetCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetGrowthCount++;
        ResetGrowthEnvelope = envelope;
        return Task.FromResult(
            (ResetGrowth ?? throw Missing("growth reset"))(envelope));
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetBasicSavvyResetCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetBasicSavvyCount++;
        ResetBasicSavvyEnvelope = envelope;
        return Task.FromResult(
            (ResetBasicSavvy ?? throw Missing("Basic-Savvy reset"))(
                envelope));
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetOwnerMergeToggleCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToggleOwnerMergeCount++;
        ToggleOwnerMergeEnvelope = envelope;
        return Task.FromResult(
            (ToggleOwnerMerge ?? throw Missing("owner Merge toggle"))(
                envelope));
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetToPetMergeCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MergePetsCount++;
        MergePetsEnvelope = envelope;
        return Task.FromResult(
            (MergePets ?? throw Missing("pet-to-pet Merge"))(envelope));
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetRebirthCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RebirthPetCount++;
        RebirthPetEnvelope = envelope;
        return Task.FromResult(
            (RebirthPet ?? throw Missing("pet rebirth"))(envelope));
    }

    private static InvalidOperationException Missing(string operation) =>
        new($"The pet fixture did not configure {operation}.");
}

internal sealed class PetDurableCaptureTransport :
    ILegacyByteTransport,
    ISecureControlChannel,
    ISecureCommandResultTransport
{
    private readonly object _gate = new();
    private readonly List<byte[]> _legacyWriteChunks = [];
    private readonly MemoryStream _legacyWrites = new();
    private readonly List<SecureLegacyCommandResult> _results = [];

    public PetDurableCaptureTransport()
    {
        var connectionId = Enumerable.Repeat(
            (byte)0xA1,
            SecureProtocolConstants.ConnectionIdBytes).ToArray();
        var clientInstanceId = Enumerable.Repeat(
            (byte)0xB2,
            SecureProtocolConstants.ClientInstanceIdBytes).ToArray();
        var originHash = Enumerable.Repeat(
            (byte)0xC3,
            SecureProtocolConstants.BuildHashBytes).ToArray();
        try
        {
            ConnectionContext = new SecureConnectionContext(
                SecureEndpointRole.Game,
                SecureProtocolConstants.ProtocolMajor,
                SecureProtocolConstants.ProtocolMinor,
                connectionId,
                clientInstanceId,
                originHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(connectionId);
            CryptographicOperations.ZeroMemory(clientInstanceId);
            CryptographicOperations.ZeroMemory(originHash);
        }
    }

    public string RemoteEndPoint => "secure-pet-handler-check";

    public SecureConnectionContext ConnectionContext { get; }

    public SecureBoundGamePrincipal? BoundGamePrincipal => null;

    public bool SupportsRealtimeMovement => false;

    public bool IsRealtimeMovementActive => false;

    public IReadOnlyList<SecureLegacyCommandResult> CommandResults
    {
        get
        {
            lock (_gate)
            {
                return _results.ToArray();
            }
        }
    }

    public IReadOnlyList<byte[]> LegacyWriteChunks
    {
        get
        {
            lock (_gate)
            {
                return _legacyWriteChunks
                    .Select(static chunk => chunk.ToArray())
                    .ToArray();
            }
        }
    }

    public ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(0);
    }

    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _legacyWriteChunks.Add(source.ToArray());
            _legacyWrites.Write(source.Span);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask SendLegacyCommandResultAsync(
        SecureLegacyCommandResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _results.Add(result);
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<byte[]> ReadLegacyPackets()
    {
        byte[] clear;
        lock (_gate)
        {
            clear = _legacyWrites.ToArray();
        }
        new PacketCipher().Transform(clear);
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < clear.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clear.AsSpan(offset, 2));
            if (length < 4 || length > clear.Length - offset)
            {
                throw new InvalidDataException(
                    "Captured pet stream has an invalid frame.");
            }
            packets.Add(clear.AsSpan(offset, length).ToArray());
            offset += length;
        }
        return packets;
    }

    public bool TryTakeRealtimeMovement(
        out SecureRealtimeMovementIngress ingress)
    {
        ingress = default;
        return false;
    }

    public bool TryPublishRealtimeSnapshot(
        in SecureRealtimePositionSnapshot snapshot) => false;

    public ValueTask SendGameGrantAsync(
        SecureGameGrant grant,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(
            new InvalidOperationException(
                "Pet handler checks cannot issue login grants."));

    public void MarkAuthenticated()
    {
    }

    public void Disconnect()
    {
    }

    public ValueTask DisposeAsync()
    {
        _legacyWrites.Dispose();
        return ValueTask.CompletedTask;
    }
}
