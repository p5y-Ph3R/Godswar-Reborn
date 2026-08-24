using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Characters;

namespace Godswar.Server.Application.Commands;

internal enum CommandFamily : ushort
{
    TalentUpgrade = 1,
    PetLevelUpgrade = 2,
    EquipmentForge = 3,
    DeveloperItemGrant = 4,
    DeveloperBagClear = 5,
    GearMentorMakeAttributeStone = 6,
    GearMentorTransformCrystal = 7,
    GearMentorCombineGemPieces = 8,
    GearMentorDecomposeGear = 9,
    GearMentorEnhanceAttribute = 10,
    GearMentorAddAttribute = 11,
    GearMentorDeleteAttribute = 12,
    KitBagItemDelete = 13,
    KitBagItemMove = 14,
    EquipmentBagTransfer = 15,
    HolyStoneMount = 16,
    HolyStoneRemove = 17,
    HolyStoneDrill = 18,
    ZodiacSkillGridActivation = 19,
    ZodiacSkillGridUpgrade = 20,
    ZodiacSkillGridSelection = 21,
    CharacterCreate = 22,
    CharacterDelete = 23,
    CharacterRestore = 24,
    CharacterPurge = 25,
    BagItemActivation = 26,
    PetPresenceTransition = 27,
    MonsterRewardSettlement = 28,
    ProgressionIntervalSettlement = 29,
    HolySuitStoreExperience = 30,
    HolySuitTransferExperience = 31,
    HolySuitConsumeWare = 32,
    HolySuitTransformExperience = 33,
    ClassSuitExchangeTierI = 34,
    ClassSuitConvertToCommon = 35,
    ClassSuitUpgradeTierII = 36,
    ClassSuitUpgradeTierIII = 37,
    ClassSuitUpgradeTierIV = 38,
    ClassSuitAddAttribute = 39,
    ClassSuitDeleteAttribute = 40,
    HolyStoneAdvancedDrill = 41,
    HolyStoneUpgrade = 42,
    HolyStoneCombine = 43,
    HolyStoneImplementSpirit = 44,
    MountGearDrill = 45,
    PetSkillUnlearn = 46,
    PetGrowthReset = 47,
    PetOwnerMergeToggle = 48,
    PetToPetMerge = 49,
    PetRebirth = 50,
    PetBasicSavvyReset = 51,
    PetAppearanceChange = 52,
    PetBind = 53,
    PetSoulContract = 54,
    PetManagerUtility = 55,
    WarehouseTransfer = 58,
    WarehouseExpansion = 59
}

internal enum CommandIdentityStrength : byte
{
    LegacyAggregateVersion = 1,
    ClientOperationId = 2,
    UnsupportedLegacyRetry = 3,
    ServerOperationId = 4
}

internal enum CommandTransportKind : byte
{
    LegacyTcp = 1,
    SecureTlsLegacy = 2,
    SecureCommand = 3
}

internal enum CommandEnvelopeValidation : byte
{
    Valid = 1,
    UnsupportedVersion = 2,
    InvalidFamily = 3,
    InvalidSubject = 4,
    InvalidCorrelation = 5,
    InvalidReceivedAt = 6,
    InvalidDigest = 7,
    RequestHashConflict = 8,
    OperationIdentityConflict = 9,
    InvalidCommand = 10,
    BoundsExceeded = 11,
    InvalidOwnership = 12
}

internal readonly record struct CommandSubject(
    int AccountId,
    int CharacterId);

internal readonly record struct CommandConnectionCorrelation(
    Guid ConnectionId,
    CommandTransportKind Transport);

internal sealed record CommandEnvelope<TCommand>(
    int ContractVersion,
    CommandFamily Family,
    CommandIdentityStrength IdentityStrength,
    CommandSubject Subject,
    CommandConnectionCorrelation Connection,
    DateTimeOffset ReceivedAt,
    string OperationId,
    string RequestHash,
    TCommand Command)
{
    /// <summary>
    /// Session-wide PostgreSQL fencing identity. It is deliberately excluded
    /// from the command digest because reconnecting owners must be able to
    /// replay the same durable operation identity.
    /// </summary>
    public PlayerOwnershipFence Ownership { get; init; }
}

internal static class CommandEnvelopeContract
{
    public const int CurrentVersion = 1;
    public const int DigestBytes = 32;
    public const int DigestHexLength = DigestBytes * 2;
    public const int MaximumOperationScopeBytes = 256;
    public const int MaximumCanonicalRequestBytes = 1_024;

    private static readonly byte[] OperationDomain =
        Encoding.ASCII.GetBytes("godswar.command.operation.v1\0");
    private static readonly byte[] RequestDomain =
        Encoding.ASCII.GetBytes("godswar.command.request.v1\0");

    public static CommandEnvelope<TCommand> Create<TCommand>(
        CommandFamily family,
        CommandIdentityStrength identityStrength,
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        ReadOnlySpan<byte> operationScope,
        ReadOnlySpan<byte> canonicalRequest,
        TCommand command)
    {
        if (operationScope.Length > MaximumOperationScopeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationScope));
        }

        if (canonicalRequest.Length > MaximumCanonicalRequestBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicalRequest));
        }

        var requestHash = ComputeRequestHash(
            family,
            canonicalRequest);
        var operationId = ComputeOperationId(
            family,
            subject,
            operationScope);

        return new CommandEnvelope<TCommand>(
            CurrentVersion,
            family,
            identityStrength,
            subject,
            connection,
            receivedAt,
            operationId,
            requestHash,
            command);
    }

    internal static string DeriveOperationId(
        CommandFamily family,
        CommandSubject subject,
        ReadOnlySpan<byte> operationScope)
    {
        if (!Enum.IsDefined(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family));
        }

        if (!IsValidSubject(family, subject))
        {
            throw new ArgumentOutOfRangeException(nameof(subject));
        }

        if (operationScope.Length > MaximumOperationScopeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationScope));
        }

        return ComputeOperationId(family, subject, operationScope);
    }

    public static CommandEnvelopeValidation Validate<TCommand>(
        CommandEnvelope<TCommand> envelope,
        CommandFamily expectedFamily,
        CommandIdentityStrength expectedIdentityStrength,
        ReadOnlySpan<byte> operationScope,
        ReadOnlySpan<byte> canonicalRequest)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (operationScope.Length > MaximumOperationScopeBytes ||
            canonicalRequest.Length > MaximumCanonicalRequestBytes)
        {
            return CommandEnvelopeValidation.BoundsExceeded;
        }

        if (envelope.ContractVersion != CurrentVersion)
        {
            return CommandEnvelopeValidation.UnsupportedVersion;
        }

        if (envelope.Family != expectedFamily ||
            !Enum.IsDefined(envelope.Family) ||
            envelope.IdentityStrength != expectedIdentityStrength)
        {
            return CommandEnvelopeValidation.InvalidFamily;
        }

        if (!IsValidSubject(envelope.Family, envelope.Subject))
        {
            return CommandEnvelopeValidation.InvalidSubject;
        }

        if (envelope.Connection.ConnectionId == Guid.Empty ||
            !Enum.IsDefined(envelope.Connection.Transport))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        if (envelope.ReceivedAt == default)
        {
            return CommandEnvelopeValidation.InvalidReceivedAt;
        }

        if (!IsDigest(envelope.RequestHash) ||
            !IsDigest(envelope.OperationId))
        {
            return CommandEnvelopeValidation.InvalidDigest;
        }

        var expectedRequestHash = ComputeRequestHash(
            expectedFamily,
            canonicalRequest);
        if (!FixedTimeEquals(envelope.RequestHash, expectedRequestHash))
        {
            return CommandEnvelopeValidation.RequestHashConflict;
        }

        var expectedOperationId = ComputeOperationId(
            expectedFamily,
            envelope.Subject,
            operationScope);
        return FixedTimeEquals(envelope.OperationId, expectedOperationId)
            ? CommandEnvelopeValidation.Valid
            : CommandEnvelopeValidation.OperationIdentityConflict;
    }

    public static CommandEnvelope<TCommand> BindOwnership<TCommand>(
        CommandEnvelope<TCommand> envelope,
        PlayerOwnershipFence ownership)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ownership.Validate();
        if (envelope.Subject.CharacterId <= 0)
        {
            throw new ArgumentException(
                "Player ownership can be bound only to a character command.",
                nameof(envelope));
        }

        return envelope with { Ownership = ownership };
    }

    public static CommandEnvelopeValidation ValidateOwnership<TCommand>(
        CommandEnvelope<TCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Subject.CharacterId > 0 &&
            envelope.Ownership.IsValid
                ? CommandEnvelopeValidation.Valid
                : CommandEnvelopeValidation.InvalidOwnership;
    }

    private static string ComputeRequestHash(
        CommandFamily family,
        ReadOnlySpan<byte> canonicalRequest)
    {
        var input = new byte[
            RequestDomain.Length +
            sizeof(int) +
            sizeof(ushort) +
            canonicalRequest.Length];
        RequestDomain.CopyTo(input, 0);
        var offset = RequestDomain.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset, sizeof(int)),
            CurrentVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt16BigEndian(
            input.AsSpan(offset, sizeof(ushort)),
            (ushort)family);
        offset += sizeof(ushort);
        canonicalRequest.CopyTo(input.AsSpan(offset));
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string ComputeOperationId(
        CommandFamily family,
        CommandSubject subject,
        ReadOnlySpan<byte> operationScope)
    {
        var input = new byte[
            OperationDomain.Length +
            sizeof(int) +
            sizeof(ushort) +
            (sizeof(int) * 2) +
            operationScope.Length];
        OperationDomain.CopyTo(input, 0);
        var offset = OperationDomain.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset, sizeof(int)),
            CurrentVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt16BigEndian(
            input.AsSpan(offset, sizeof(ushort)),
            (ushort)family);
        offset += sizeof(ushort);
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset, sizeof(int)),
            subject.AccountId);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset, sizeof(int)),
            subject.CharacterId);
        offset += sizeof(int);
        operationScope.CopyTo(input.AsSpan(offset));
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static bool IsValidSubject(
        CommandFamily family,
        CommandSubject subject)
    {
        if (subject.AccountId <= 0)
        {
            return false;
        }

        return family is
            CommandFamily.CharacterCreate or
            CommandFamily.CharacterDelete or
            CommandFamily.CharacterRestore or
            CommandFamily.CharacterPurge
                ? subject.CharacterId == 0
                : subject.CharacterId > 0;
    }

    private static bool IsDigest(string? value)
    {
        Span<byte> decoded = stackalloc byte[DigestBytes];
        return TryDecodeDigest(value, decoded);
    }

    private static bool FixedTimeEquals(string? left, string? right)
    {
        Span<byte> leftBytes = stackalloc byte[DigestBytes];
        Span<byte> rightBytes = stackalloc byte[DigestBytes];
        return TryDecodeDigest(left, leftBytes) &&
            TryDecodeDigest(right, rightBytes) &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool TryDecodeDigest(
        string? value,
        Span<byte> destination)
    {
        if (value is null ||
            value.Length != DigestHexLength ||
            destination.Length < DigestBytes)
        {
            return false;
        }

        for (var index = 0; index < DigestBytes; index++)
        {
            var high = HexValue(value[index * 2]);
            var low = HexValue(value[(index * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static int HexValue(char value) =>
        value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1
        };
}
