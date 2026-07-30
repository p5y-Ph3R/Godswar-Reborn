using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Rewards;

internal readonly record struct MonsterDeathRewardCommand(
    Guid DeathEventId,
    Guid RuntimeInstanceId,
    byte MapId,
    uint MonsterObjectId,
    uint SpawnGeneration,
    ulong DeathHealthRevision,
    int AwardedExperience,
    int AwardedTalentExperience);

internal static class MonsterDeathRewardCommandEnvelope
{
    public const int MaximumAwardedExperience = 100_000_000;
    public const int MaximumAwardedTalentExperience = 100_000_000;
    public const ulong MaximumPersistedHealthRevision = long.MaxValue;
    public const ushort CanonicalRequestVersion = 1;

    private const int OperationScopeBytes = 16;
    private const int CanonicalRequestBytes =
        sizeof(ushort) + 16 + sizeof(byte) + sizeof(uint) +
        sizeof(uint) + sizeof(ulong) + sizeof(int) + sizeof(int);
    private static readonly byte[] DeathIdentityDomain =
        Encoding.ASCII.GetBytes("godswar.monster.death.v1\0");

    public static bool TryCreateCommand(
        Guid runtimeInstanceId,
        byte mapId,
        uint monsterObjectId,
        uint spawnGeneration,
        ulong deathHealthRevision,
        int awardedExperience,
        int awardedTalentExperience,
        out MonsterDeathRewardCommand command)
    {
        command = default;
        if (!HasValidIdentity(
                runtimeInstanceId,
                monsterObjectId,
                spawnGeneration,
                deathHealthRevision) ||
            awardedExperience is < 0 or > MaximumAwardedExperience ||
            awardedTalentExperience is
                < 0 or > MaximumAwardedTalentExperience)
        {
            return false;
        }

        var deathEventId = DeriveDeathEventId(
            runtimeInstanceId,
            mapId,
            monsterObjectId,
            spawnGeneration,
            deathHealthRevision);
        command = new MonsterDeathRewardCommand(
            deathEventId,
            runtimeInstanceId,
            mapId,
            monsterObjectId,
            spawnGeneration,
            deathHealthRevision,
            awardedExperience,
            awardedTalentExperience);
        return true;
    }

    public static CommandEnvelope<MonsterDeathRewardCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        MonsterDeathRewardCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The monster-death reward command is invalid.",
                nameof(command));
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteGuid(command.DeathEventId, operationScope);
        Span<byte> canonicalRequest =
            stackalloc byte[CanonicalRequestBytes];
        WriteCanonicalRequest(command, canonicalRequest);
        return CommandEnvelopeContract.Create(
            CommandFamily.MonsterRewardSettlement,
            CommandIdentityStrength.ServerOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            canonicalRequest,
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<MonsterDeathRewardCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValidCommand(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteGuid(envelope.Command.DeathEventId, operationScope);
        Span<byte> canonicalRequest =
            stackalloc byte[CanonicalRequestBytes];
        WriteCanonicalRequest(envelope.Command, canonicalRequest);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.MonsterRewardSettlement,
            CommandIdentityStrength.ServerOperationId,
            operationScope,
            canonicalRequest);
    }

    public static Guid DeriveDeathEventId(
        Guid runtimeInstanceId,
        byte mapId,
        uint monsterObjectId,
        uint spawnGeneration,
        ulong deathHealthRevision)
    {
        if (!HasValidIdentity(
                runtimeInstanceId,
                monsterObjectId,
                spawnGeneration,
                deathHealthRevision))
        {
            throw new ArgumentOutOfRangeException(
                nameof(deathHealthRevision),
                "Monster death identity fields must be non-zero and bounded.");
        }

        Span<byte> identity = stackalloc byte[
            16 + sizeof(byte) + sizeof(uint) + sizeof(uint) +
            sizeof(ulong)];
        WriteGuid(runtimeInstanceId, identity[..16]);
        identity[16] = mapId;
        BinaryPrimitives.WriteUInt32BigEndian(
            identity.Slice(17, sizeof(uint)),
            monsterObjectId);
        BinaryPrimitives.WriteUInt32BigEndian(
            identity.Slice(21, sizeof(uint)),
            spawnGeneration);
        BinaryPrimitives.WriteUInt64BigEndian(
            identity.Slice(25, sizeof(ulong)),
            deathHealthRevision);

        var hashInput = new byte[
            DeathIdentityDomain.Length + identity.Length];
        DeathIdentityDomain.CopyTo(hashInput, 0);
        identity.CopyTo(hashInput.AsSpan(DeathIdentityDomain.Length));
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(hashInput, digest);

        // UUIDv8 marks this as an application-defined, hash-derived UUID.
        digest[6] = (byte)((digest[6] & 0x0F) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest[..16], bigEndian: true);
    }

    private static bool IsValidCommand(
        MonsterDeathRewardCommand command) =>
        HasValidIdentity(
            command.RuntimeInstanceId,
            command.MonsterObjectId,
            command.SpawnGeneration,
            command.DeathHealthRevision) &&
        command.DeathEventId == DeriveDeathEventId(
            command.RuntimeInstanceId,
            command.MapId,
            command.MonsterObjectId,
            command.SpawnGeneration,
            command.DeathHealthRevision) &&
        command.AwardedExperience is
            >= 0 and <= MaximumAwardedExperience &&
        command.AwardedTalentExperience is
            >= 0 and <= MaximumAwardedTalentExperience;

    private static bool HasValidIdentity(
        Guid runtimeInstanceId,
        uint monsterObjectId,
        uint spawnGeneration,
        ulong deathHealthRevision) =>
        runtimeInstanceId != Guid.Empty &&
        monsterObjectId > 0 &&
        spawnGeneration > 0 &&
        deathHealthRevision is
            > 0 and <= MaximumPersistedHealthRevision;

    private static void WriteCanonicalRequest(
        MonsterDeathRewardCommand command,
        Span<byte> destination)
    {
        if (destination.Length != CanonicalRequestBytes)
        {
            throw new ArgumentException(
                "The canonical reward request buffer has an invalid size.",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            CanonicalRequestVersion);
        WriteGuid(command.RuntimeInstanceId, destination.Slice(2, 16));
        destination[18] = command.MapId;
        BinaryPrimitives.WriteUInt32BigEndian(
            destination.Slice(19, sizeof(uint)),
            command.MonsterObjectId);
        BinaryPrimitives.WriteUInt32BigEndian(
            destination.Slice(23, sizeof(uint)),
            command.SpawnGeneration);
        BinaryPrimitives.WriteUInt64BigEndian(
            destination.Slice(27, sizeof(ulong)),
            command.DeathHealthRevision);
        BinaryPrimitives.WriteInt32BigEndian(
            destination.Slice(35, sizeof(int)),
            command.AwardedExperience);
        BinaryPrimitives.WriteInt32BigEndian(
            destination.Slice(39, sizeof(int)),
            command.AwardedTalentExperience);
    }

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(
                destination,
                bigEndian: true,
                out var written) ||
            written != 16)
        {
            throw new ArgumentException(
                "The UUID could not be encoded.",
                nameof(value));
        }
    }
}
