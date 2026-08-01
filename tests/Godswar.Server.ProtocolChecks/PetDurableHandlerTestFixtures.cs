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

internal sealed class PetDurableHandlerFixture : IAsyncDisposable
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
        GameClientHandler handler)
    {
        Session = session;
        Transport = transport;
        Handler = handler;
    }

    public ClientSession Session { get; }

    public PetDurableCaptureTransport Transport { get; }

    public GameClientHandler Handler { get; }

    public static PetDurableHandlerFixture Create(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter,
        IReadOnlyList<PetBootstrapSnapshot> persistedPets,
        IPetDurableCommandExecutor executor)
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
            persistedPets);
        var handler = new GameClientHandler(
            session,
            new PetHandlerStore(),
            registry,
            new FixedSnapshotReader(snapshot),
            WorldContentReaderTestFixtures.Empty,
            petDurableCommands: executor,
            petContent: PetContentTestCatalog.Instance);
        SetField(
            handler,
            "_account",
            new AccountIdentity(
                liveCharacter.AccountId,
                "durable-pet-check"));
        SetField(handler, "_character", liveCharacter);
        return new(session, transport, handler);
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

    private static CharacterAccountSnapshot CreateSnapshot(
        GameCharacter character,
        IReadOnlyList<PetBootstrapSnapshot> pets)
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
        var stats = current.CalculatedStats with
        {
            CharacterId = character.Id,
            AccountId = character.AccountId,
            Name = character.Name,
            WeaponRank = character.WeaponRank,
            WeaponAuraEffect = character.WeaponAuraEffect,
            ArmorRank = character.ArmorRank,
            ArmorAuraEffect = character.ArmorAuraEffect
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
            CalculatedStats = stats,
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
                    skill.Revision)).ToImmutableArray());

    private static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

    private sealed class PetHandlerStore : GameStoreTestStub
    {
    }

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

internal sealed class DelegatingPetDurableCommandExecutor :
    IPetDurableCommandExecutor
{
    public Func<CommandEnvelope<BagItemActivationCommand>,
        PetDurableExecutionResult>? Activate { get; init; }

    public Func<CommandEnvelope<PetLevelUpgradeCommand>,
        PetDurableExecutionResult>? Upgrade { get; init; }

    public Func<CommandEnvelope<PetPresenceTransitionCommand>,
        PetDurableExecutionResult>? Transition { get; init; }

    public int ActivateCount { get; private set; }

    public int UpgradeCount { get; private set; }

    public int TransitionCount { get; private set; }

    public CommandEnvelope<BagItemActivationCommand>? ActivationEnvelope
    { get; private set; }

    public CommandEnvelope<PetLevelUpgradeCommand>? UpgradeEnvelope
    { get; private set; }

    public CommandEnvelope<PetPresenceTransitionCommand>? TransitionEnvelope
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

    private static InvalidOperationException Missing(string operation) =>
        new($"The pet fixture did not configure {operation}.");
}

internal sealed class PetDurableCaptureTransport :
    ILegacyByteTransport,
    ISecureControlChannel,
    ISecureCommandResultTransport
{
    private readonly object _gate = new();
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
