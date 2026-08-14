using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetBasicSavvyResetHandlerChecks
{
    public const string CheckName =
        "Authoritative Fairy Basic-Savvy reset handler";

    private const int FairyFeatherSlot = 27;

    private static readonly MethodInfo HandlePetManagerMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePetManagerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePetManagerAsync was not found.");

    private static readonly PetContentStatVector PreviewValues = new(
        Agility: 600m,
        Strength: 77m,
        Accuracy: 79m,
        Technique: 80m,
        Wisdom: 81m,
        Luck: 83m);

    private static readonly PetContentStatVector LaterValues = new(
        Agility: 100m,
        Strength: 120m,
        Accuracy: 140m,
        Technique: 160m,
        Wisdom: 180m,
        Luck: 300m);

    private static GamePacket CreateResetPacket(
        Guid operationId,
        bool accept = false,
        bool nested = true,
        int corruptPaddingIndex = -1)
    {
        var arguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        var subId = nested
            ? PetManagerProtocol.BasicSavvyResetMenuSubId
            : PetManagerProtocol.BasicSavvyResetActionSubId;
        if (nested)
        {
            arguments[0] = PetManagerProtocol.BasicSavvyResetActionSubId;
        }
        if (accept)
        {
            arguments[nested ? 1 : 0] = 0;
        }
        if (corruptPaddingIndex >= 0)
        {
            arguments[corruptPaddingIndex] = 0;
        }

        var bytes = new byte[20 + arguments.Length * sizeof(int)];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            PetManagerProtocol.AthensNpcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            PetManagerProtocol.PointResetDialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            PetManagerProtocol.PointResetDialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + index * sizeof(int)),
                arguments[index]);
        }
        return new GamePacket(bytes, operationId);
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var bytes = packet.Buffer.AsSpan();
        Check.Equal(92, bytes.Length,
            "stock Fairy action frame length");
        var arguments = new int[PetManagerProtocol.FunctionArgumentCount];
        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.Slice(20 + index * sizeof(int), sizeof(int)));
        }
        var task = HandlePetManagerMethod.Invoke(
            handler,
            [
                packet,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16, 4)),
                arguments,
                CancellationToken.None
            ]) as Task ?? throw new InvalidOperationException(
                "Pet Manager Fairy handler returned no task.");
        await task;
    }

    private static GameCharacter CharacterWithFairyFeather(short stack)
    {
        var feather = CompactItemEntry.Parse(
            $"[{PetItemCatalog.FairyFeather},,,,,,0,1,1,{stack},0,0]");
        return new GameCharacter
        {
            Id = PetEggHatchProtocolChecks.CharacterId,
            AccountId = PetEggHatchProtocolChecks.AccountId,
            Name = "test2",
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                FairyFeatherSlot,
                feather.ToCompactString()),
            Equipment = GameDefaults.DefaultEquipment(1)
        };
    }

    private static PetBootstrapSnapshot CreatePet(long revision = 8)
    {
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            50m,
            new Random(50));
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_500,
            new Random(3_500));
        return PetEggHatchProtocolChecks.CreatePet(savvy, growth) with
        {
            IsCarried = true,
            IsSummoned = true,
            ContributesToCharacter = false,
            Revision = revision
        };
    }

    private static PetBootstrapSnapshot ApplyPreviewValues(
        PetBootstrapSnapshot pet) =>
        ApplyValues(pet, PreviewValues);

    private static PetBootstrapSnapshot ApplyValues(
        PetBootstrapSnapshot pet,
        PetContentStatVector next)
    {
        var values = OrderedValues(next);
        return pet with
        {
            Revision = pet.Revision + 1,
            StatValues = pet.StatValues
                .OrderBy(static stat => stat.StatCode)
                .Select((stat, index) => stat with
                {
                    InitialSavvy = values[index],
                    Revision = stat.Revision + 1
                })
                .ToArray()
        };
    }

    private static PetDurableReceipt Receipt(
        CommandEnvelope<PetBasicSavvyResetCommand> envelope,
        PetDurableReceiptStatus status,
        PetBootstrapSnapshot pet)
    {
        var succeeded = status is
            PetDurableReceiptStatus.PetBasicSavvyPreviewed or
            PetDurableReceiptStatus.PetBasicSavvyAccepted;
        var hasRoll = status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewed ||
            status == PetDurableReceiptStatus.PetBasicSavvyAccepted &&
            envelope.Command.Operation ==
                PetBasicSavvyResetOperation.Preview;
        return new PetDurableReceipt(
            CommandFamily.PetBasicSavvyReset,
            status,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            hasRoll ? FairyFeatherSlot : -1,
            EquipmentSlot: -1,
            pet.PetId,
            pet.Level,
            pet.Experience,
            succeeded ? pet.Revision : 0,
            pet.IsCarried,
            pet.IsSummoned,
            PresenceOperation: 0,
            AggregateRevision: succeeded ? pet.Revision : 0,
            AuditReference: "fairy-basic-savvy-handler-check",
            OutboxEventId: succeeded ? Guid.NewGuid() : null,
            BasicSavvyPreview: hasRoll
                ? new PetBasicSavvyPreviewSnapshot(
                    envelope.Command.Identity.OperationId,
                    pet.PetId,
                    pet.Level,
                    pet.Revision,
                    PreviewValues,
                    DateTimeOffset.UtcNow.AddMinutes(2))
                : null);
    }

    private static decimal[] OrderedValues(PetContentStatVector values) =>
    [
        values.Agility,
        values.Strength,
        values.Accuracy,
        values.Technique,
        values.Wisdom,
        values.Luck
    ];

    private static byte[] PreviewPage() =>
        ResultPage(PreviewValues);

    private static byte[] ResultPage(PetContentStatVector values) =>
        PacketBuilder.NpcFunctionActionResponse(
            PetManagerProtocol.AthensNpcId,
            PetManagerProtocol.PointResetDialogIndex,
            PetManagerProtocol.BuildBasicSavvyResetSuccessPage(
                OrderedValues(values)));

    private static byte[] TerminalPage(int resultSubId) =>
        PacketBuilder.NpcFunctionActionResponse(
            PetManagerProtocol.AthensNpcId,
            PetManagerProtocol.PointResetDialogIndex,
            resultSubId);

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));

    private static bool HasNoPresenceProjection(
        IReadOnlyList<byte[]> packets) =>
        packets.All(packet => ReadOpcode(packet) is not (10_237 or
            Opcodes.PetOperationResult));
}
