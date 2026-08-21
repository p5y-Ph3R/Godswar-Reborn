using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Characters;

internal readonly record struct CharacterCreateCommand(
    Guid ClientOperationId,
    short CharacterSlot,
    string Name,
    byte Gender,
    byte Camp,
    byte Profession,
    byte ZodiacType,
    byte Hair,
    byte Face,
    byte Faith) : IRealmScopedCharacterLifecycleCommand
{
    public RealmId RealmId { get; init; } = RealmId.Tempest;
}

internal readonly record struct CharacterDeleteCommand(
    Guid ClientOperationId,
    short CharacterSlot,
    string Name,
    int? ExpectedActiveCharacterId = null,
    long? ExpectedLifecycleVersion = null) :
    IRealmScopedCharacterLifecycleCommand
{
    public RealmId RealmId { get; init; } = RealmId.Tempest;
}

internal readonly record struct CharacterRestoreCommand(
    Guid ClientOperationId,
    short CharacterSlot,
    int CharacterId,
    long ExpectedLifecycleVersion) : IRealmScopedCharacterLifecycleCommand
{
    public RealmId RealmId { get; init; } = RealmId.Tempest;
}

internal readonly record struct CharacterPurgeCommand(
    Guid ClientOperationId,
    short CharacterSlot,
    int CharacterId,
    long ExpectedLifecycleVersion) : IRealmScopedCharacterLifecycleCommand
{
    public RealmId RealmId { get; init; } = RealmId.Tempest;
}

internal interface IRealmScopedCharacterLifecycleCommand
{
    RealmId RealmId { get; }
}

internal static class CharacterLifecycleCommandContract
{
    public const short SingleCharacterSlot = 0;
    public const int MaximumNameUtf8Bytes = 32;
    private const ushort CanonicalRequestVersion = 1;
    private const ushort RealmScopedCanonicalRequestVersion = 2;

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
            name.Any(char.IsControl))
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(name) <= MaximumNameUtf8Bytes;
    }

    public static bool IsTrustedTransport(CommandTransportKind transport) =>
        transport is CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;

    public static byte[] OperationScope(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty operation UUID is required.",
                nameof(operationId));
        }

        var result = new byte[16];
        if (!operationId.TryWriteBytes(
                result,
                bigEndian: true,
                out var written) ||
            written != result.Length)
        {
            throw new ArgumentException(
                "The operation UUID could not be encoded.",
                nameof(operationId));
        }

        return result;
    }

    public static byte[] CanonicalCreate(CharacterCreateCommand command)
    {
        var name = EncodeName(command.Name);
        var result = new byte[2 + 2 + name.Length + 8];
        BinaryPrimitives.WriteUInt16BigEndian(
            result,
            CanonicalRequestVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            result.AsSpan(2),
            checked((ushort)name.Length));
        name.CopyTo(result.AsSpan(4));
        var offset = 4 + name.Length;
        result[offset++] = checked((byte)command.CharacterSlot);
        result[offset++] = command.Gender;
        result[offset++] = command.Camp;
        result[offset++] = command.Profession;
        result[offset++] = command.ZodiacType;
        result[offset++] = command.Hair;
        result[offset++] = command.Face;
        result[offset] = command.Faith;
        return AddRealmScope(command.RealmId, result);
    }

    public static byte[] CanonicalDelete(CharacterDeleteCommand command)
    {
        var name = EncodeName(command.Name);
        var result = new byte[2 + 2 + name.Length + 1];
        BinaryPrimitives.WriteUInt16BigEndian(
            result,
            CanonicalRequestVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            result.AsSpan(2),
            checked((ushort)name.Length));
        name.CopyTo(result.AsSpan(4));
        var offset = 4 + name.Length;
        result[offset] = checked((byte)command.CharacterSlot);
        return AddRealmScope(command.RealmId, result);
    }

    public static byte[] CanonicalTarget(
        short characterSlot,
        int characterId,
        long expectedLifecycleVersion,
        RealmId realmId)
    {
        var result = new byte[2 + 1 + 4 + 8];
        BinaryPrimitives.WriteUInt16BigEndian(
            result,
            CanonicalRequestVersion);
        result[2] = checked((byte)characterSlot);
        BinaryPrimitives.WriteInt32BigEndian(
            result.AsSpan(3),
            characterId);
        BinaryPrimitives.WriteInt64BigEndian(
            result.AsSpan(7),
            expectedLifecycleVersion);
        return AddRealmScope(realmId, result);
    }

    private static byte[] AddRealmScope(
        RealmId realmId,
        byte[] tempestCanonical)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        if (realmId == RealmId.Tempest)
        {
            return tempestCanonical;
        }

        var realmCanonical = new byte[tempestCanonical.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(
            realmCanonical,
            RealmScopedCanonicalRequestVersion);
        BinaryPrimitives.WriteInt32BigEndian(
            realmCanonical.AsSpan(2),
            realmId.Value);
        tempestCanonical.AsSpan(2).CopyTo(realmCanonical.AsSpan(6));
        return realmCanonical;
    }

    private static byte[] EncodeName(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        return Encoding.UTF8.GetBytes(name);
    }
}
