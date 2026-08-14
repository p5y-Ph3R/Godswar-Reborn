using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetPresenceProtocolChecks
{
    private static readonly MethodInfo RestorePetPresenceMethod =
        typeof(GameClientHandler).GetMethod(
            "RestorePetPresenceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "GameClientHandler.RestorePetPresenceAsync was not found.");

    private static async Task CheckPersistedPresenceRestoreAsync()
    {
        CheckLoginLifecycleIdentityTransportSemantics();
        await CheckPersistedSummonedPetRestoreAsync();
        await CheckLoginCallsOutPersistedRecalledPetAsync();
        await CheckLoginCallOutRetryIsIdempotentAsync();
        await CheckMapRestorePreservesPersistedRecallAsync();
        await CheckMergedPetRestoreUsesUniteProjectionAsync();
    }

    private static async Task CheckMergedPetRestoreUsesUniteProjectionAsync()
    {
        var pet = CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 9) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = true
        };
        await using var fixture = CreateRestoreFixture(pet);

        await InvokeRestoreAsync(
            fixture.Handler,
            [pet],
            summonCarriedPet: false);

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(
            packets.Any(packet =>
                BinaryPrimitives.ReadUInt16LittleEndian(
                    packet.AsSpan(2)) ==
                Opcodes.PetOwnerMergeStarted),
            "persisted active Merge restores the native unite presentation");
        Check.True(
            packets.All(packet =>
                BinaryPrimitives.ReadUInt16LittleEndian(
                    packet.AsSpan(2)) is not (
                        10237 or Opcodes.PetOperationResult)),
            "active Merge never rebuilds 10237 or calls out its hidden companion");
    }

    private static async Task CheckPersistedSummonedPetRestoreAsync()
    {
        var pet = CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 7);
        await using var fixture = CreateRestoreFixture(pet);

        await InvokeRestoreAsync(
            fixture.Handler,
            [pet],
            summonCarriedPet: true);

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            2,
            packets.Count,
            "persisted summoned pet emits exactly two presentation frames");
        Check.True(
            packets[0].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    PetId,
                    PetOperationResultCode.CallOutSucceeded)),
            "persisted summoned pet replays Call Out success first");
        Check.True(
            packets[1].SequenceEqual(
                PacketBuilder.PetWorldPresence(
                    PetId,
                    0x0000_1448u)),
            "persisted summoned pet binds world presence second");
    }

    private static async Task CheckLoginCallsOutPersistedRecalledPetAsync()
    {
        var recalled = CreatePet(
            isCarried: true,
            isSummoned: false,
            revision: 8);
        var summoned = recalled with
        {
            IsSummoned = true,
            Revision = 9
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Transition = envelope =>
                PetDurableExecutionResult.Committed(
                    new PetDurableReceipt(
                        CommandFamily.PetPresenceTransition,
                        PetDurableReceiptStatus.PresenceChanged,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        KitBagSlot: -1,
                        EquipmentSlot: -1,
                        PetId,
                        PetLevel: 1,
                        PetExperience: 0,
                        PetRevision: summoned.Revision,
                        IsCarried: true,
                        IsSummoned: true,
                        PresenceOperation: 2,
                        AggregateRevision: summoned.Revision,
                        AuditReference: "login-pet-call-out-check",
                        OutboxEventId: Guid.NewGuid()))
        };
        await using var fixture = CreateRestoreFixture(
            summoned,
            executor);

        await InvokeRestoreAsync(
            fixture.Handler,
            [recalled],
            summonCarriedPet: true);

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            2,
            packets.Count,
            "login call-out emits one durable result and one world binding");
        Check.True(
            packets[0].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    PetId,
                    PetOperationResultCode.CallOutSucceeded)) &&
            packets[1].SequenceEqual(
                PacketBuilder.PetWorldPresence(
                    PetId,
                    0x0000_1448u)),
            "login calls out the recalled pet before binding its model");
        Check.True(
            fixture.Session.IsSecure &&
            executor.TransitionCount == 1 &&
            executor.TransitionEnvelope?.Command is
                {
                    Operation: PetPresenceCommandOperation.CallOut,
                    Identity.IsServerSessionLifecycle: true
                },
            "secure login uses one authoritative session-lifecycle command");
    }

    private static async Task CheckLoginCallOutRetryIsIdempotentAsync()
    {
        var recalled = CreatePet(
            isCarried: true,
            isSummoned: false,
            revision: 12);
        var summoned = recalled with
        {
            IsSummoned = true,
            Revision = 13
        };
        var operationIds = new List<Guid>();
        PetDurableReceipt? committedReceipt = null;
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Transition = envelope =>
            {
                operationIds.Add(envelope.Command.Identity.OperationId);
                committedReceipt ??= new PetDurableReceipt(
                    CommandFamily.PetPresenceTransition,
                    PetDurableReceiptStatus.PresenceChanged,
                    envelope.Subject.AccountId,
                    envelope.Subject.CharacterId,
                    KitBagSlot: -1,
                    EquipmentSlot: -1,
                    PetId,
                    PetLevel: 1,
                    PetExperience: 0,
                    PetRevision: summoned.Revision,
                    IsCarried: true,
                    IsSummoned: true,
                    PresenceOperation: 2,
                    AggregateRevision: summoned.Revision,
                    AuditReference: "login-pet-call-out-retry-check",
                    OutboxEventId: Guid.NewGuid());
                return operationIds.Count == 1
                    ? PetDurableExecutionResult.Committed(committedReceipt)
                    : PetDurableExecutionResult.Duplicate(committedReceipt);
            }
        };
        await using var fixture = CreateRestoreFixture(summoned, executor);

        await InvokeRestoreAsync(
            fixture.Handler,
            [recalled],
            summonCarriedPet: true);
        await InvokeRestoreAsync(
            fixture.Handler,
            [recalled],
            summonCarriedPet: true);

        Check.True(
            operationIds.Count == 2 &&
            operationIds[0] != Guid.Empty &&
            operationIds[0] == operationIds[1],
            "login Call Out retry reuses its durable operation identity");
        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            4,
            packets.Count,
            "each login restore attempt emits one result and one world binding");
        for (var offset = 0; offset < packets.Count; offset += 2)
        {
            Check.True(
                packets[offset].SequenceEqual(
                    PacketBuilder.PetOperationResult(
                        PetId,
                        PetOperationResultCode.CallOutSucceeded)) &&
                packets[offset + 1].SequenceEqual(
                    PacketBuilder.PetWorldPresence(
                        PetId,
                        0x0000_1448u)),
                "login Call Out retry preserves the exact projection order");
        }
    }

    private static void CheckLoginLifecycleIdentityTransportSemantics()
    {
        var operationId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var identity =
            PetCommandOperationIdentity.ServerSessionLifecycle(
                operationId,
                connectionId);
        var rawIdentity = PetCommandOperationIdentity.RawLocalServer(
            operationId,
            connectionId);

        foreach (var transport in new[]
                 {
                     CommandTransportKind.LegacyTcp,
                     CommandTransportKind.SecureTlsLegacy
                 })
        {
            var correlation = new CommandConnectionCorrelation(
                connectionId,
                transport);
            var envelope = PetPresenceTransitionCommandEnvelope
                .CreateServerSessionLifecycle(
                    new CommandSubject(AccountId, CharacterId),
                    correlation,
                    DateTimeOffset.UtcNow,
                    new PetPresenceTransitionCommand(
                        identity,
                        PetId,
                        PetPresenceCommandOperation.CallOut));
            Check.True(
                PetPresenceTransitionCommandEnvelope.Validate(envelope) ==
                    CommandEnvelopeValidation.Valid,
                $"login lifecycle Call Out accepts {transport}");
        }

        Check.True(
            !PetDurableCommandContract.OperationScope(identity)
                .SequenceEqual(
                    PetDurableCommandContract.OperationScope(rawIdentity)),
            "server lifecycle and raw-client identities cannot collide");
    }

    private static async Task CheckMapRestorePreservesPersistedRecallAsync()
    {
        var pet = CreatePet(
            isCarried: true,
            isSummoned: false,
            revision: 10);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Transition = _ => throw new InvalidOperationException(
                "A map restore cannot change recalled pet state.")
        };
        await using var fixture = CreateRestoreFixture(pet, executor);

        await InvokeRestoreAsync(
            fixture.Handler,
            [pet],
            summonCarriedPet: false);

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            1,
            packets.Count,
            "map restore emits one recalled-pet selection frame");
        Check.True(
            packets[0].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    PetId,
                    PetOperationResultCode.TakeSucceeded)) &&
            executor.TransitionCount == 0,
            "map restore preserves an explicit in-session Recall");
    }

    private static PetDurableHandlerFixture CreateRestoreFixture(
        PetBootstrapSnapshot pet,
        DelegatingPetDurableCommandExecutor? executor = null)
    {
        var character = CreateCharacter();
        return PetDurableHandlerFixture.Create(
            character,
            character,
            [pet],
            executor ?? new DelegatingPetDurableCommandExecutor
            {
                Transition = _ => throw new InvalidOperationException(
                    "Presentation restore cannot execute a durable command.")
            });
    }

    private static async Task InvokeRestoreAsync(
        GameClientHandler handler,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        bool summonCarriedPet)
    {
        var task = RestorePetPresenceMethod.Invoke(
            handler,
            [pets, summonCarriedPet, CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "RestorePetPresenceAsync returned no task.");
        await task;
    }
}
