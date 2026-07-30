using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Characters;

internal static class CharacterLifecyclePersistenceCodec
{
    public const short ContractVersion = 1;
    public const string PrincipalType = "account";
    public const string AggregateType = "account_character_slot";
    public const string ConsumerKey = "character_lifecycle_v1";
    public const string OrderingPolicy = "strict";
    public const string RetentionPolicy = "permanent";
    public const string CommittedResultCode = "committed";
    public const string TerminalRejectedResultCode = "terminal_rejected";

    public static string AggregateKey(int accountId, short slot)
    {
        if (accountId <= 0 ||
            slot != CharacterLifecycleCommandContract.SingleCharacterSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(accountId));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{accountId}:{slot}");
    }

    public static string FamilyCode(CommandFamily family) =>
        family switch
        {
            CommandFamily.CharacterCreate => "character_create",
            CommandFamily.CharacterDelete => "character_delete",
            CommandFamily.CharacterRestore => "character_restore",
            CommandFamily.CharacterPurge => "character_purge",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string EventType(CommandFamily family) =>
        family switch
        {
            CommandFamily.CharacterCreate => "character.created",
            CommandFamily.CharacterDelete => "character.deleted",
            CommandFamily.CharacterRestore => "character.restored",
            CommandFamily.CharacterPurge => "character.purged",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static string ResultCode(CharacterLifecycleReceipt receipt) =>
        receipt.Succeeded
            ? CommittedResultCode
            : TerminalRejectedResultCode;

    public static byte[] Encode(CharacterLifecycleReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteNumber("family", (ushort)receipt.Family);
            writer.WriteNumber("status", (byte)receipt.Status);
            writer.WriteNumber("accountId", receipt.AccountId);
            writer.WriteNumber("characterSlot", receipt.CharacterSlot);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber(
                "lifecycleVersion",
                receipt.LifecycleVersion);
            writer.WriteString("characterName", receipt.CharacterName);
            WriteOptionalTimestamp(
                writer,
                "restoreUntil",
                receipt.RestoreUntil);
            WriteOptionalTimestamp(
                writer,
                "purgeAfter",
                receipt.PurgeAfter);
            writer.WriteString(
                "auditReference",
                receipt.AuditReference);
            if (receipt.OutboxEventId is { } eventId)
            {
                writer.WriteString("outboxEventId", eventId);
            }
            else
            {
                writer.WriteNull("outboxEventId");
            }
            writer.WriteEndObject();
        }

        EnsurePayloadBound(buffer.WrittenCount);
        return buffer.WrittenSpan.ToArray();
    }

    public static CharacterLifecycleReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        string expectedResultCode,
        long expectedAuditId,
        CommandFamily expectedFamily,
        int expectedAccountId,
        short expectedSlot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        var receipt = Decode(payload);

        if (receipt.Family != expectedFamily ||
            receipt.AccountId != expectedAccountId ||
            receipt.CharacterSlot != expectedSlot ||
            !string.Equals(
                ResultCode(receipt),
                expectedResultCode,
                StringComparison.Ordinal) ||
            expectedAuditId <= 0 ||
            !string.Equals(
                receipt.AuditReference,
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored character lifecycle identity is inconsistent.");
        }

        var actualHash = Hash(Encode(receipt));
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                expectedHash,
                actualHash))
        {
            throw new InvalidDataException(
                "The stored character lifecycle hash is invalid.");
        }

        return receipt;
    }

    public static CharacterLifecycleReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        EnsurePayloadBound(payload.Length);
        CharacterLifecycleReceipt receipt;
        try
        {
            using var document = JsonDocument.Parse(
                payload.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });
            var root = document.RootElement;
            EnsureExactShape(root);
            if (root.GetProperty("contractVersion").GetInt16() !=
                ContractVersion)
            {
                throw new InvalidDataException(
                    "The stored character lifecycle contract is unsupported.");
            }

            receipt = new CharacterLifecycleReceipt(
                (CommandFamily)root.GetProperty("family").GetUInt16(),
                (CharacterLifecycleReceiptStatus)
                    root.GetProperty("status").GetByte(),
                root.GetProperty("accountId").GetInt32(),
                root.GetProperty("characterSlot").GetInt16(),
                root.GetProperty("characterId").GetInt32(),
                root.GetProperty("lifecycleVersion").GetInt64(),
                RequiredString(root, "characterName"),
                OptionalTimestamp(root, "restoreUntil"),
                OptionalTimestamp(root, "purgeAfter"),
                RequiredString(root, "auditReference"),
                OptionalGuid(root, "outboxEventId"));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            InvalidOperationException or
            ArgumentException or
            FormatException or
            OverflowException)
        {
            throw new InvalidDataException(
                "The stored character lifecycle evidence is malformed.",
                exception);
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static void WriteOptionalTimestamp(
        Utf8JsonWriter writer,
        string name,
        DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            writer.WriteString(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static DateTimeOffset? OptionalTimestamp(
        JsonElement root,
        string name) =>
        root.GetProperty(name).ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String =>
                root.GetProperty(name).GetDateTimeOffset(),
            _ => throw new InvalidDataException(
                $"The stored {name} is invalid.")
        };

    private static Guid? OptionalGuid(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => root.GetProperty(name).GetGuid(),
            _ => throw new InvalidDataException(
                $"The stored {name} is invalid.")
        };

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ??
        throw new InvalidDataException($"The stored {name} is missing.");

    private static void EnsureExactShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The lifecycle receipt must be an object.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) ||
                property.Name is not (
                    "contractVersion" or
                    "family" or
                    "status" or
                    "accountId" or
                    "characterSlot" or
                    "characterId" or
                    "lifecycleVersion" or
                    "characterName" or
                    "restoreUntil" or
                    "purgeAfter" or
                    "auditReference" or
                    "outboxEventId"))
            {
                throw new InvalidDataException(
                    "The lifecycle receipt shape is invalid.");
            }
        }

        if (names.Count != 12)
        {
            throw new InvalidDataException(
                "The lifecycle receipt has missing fields.");
        }
    }

    private static void EnsurePayloadBound(int byteCount)
    {
        if (byteCount is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The lifecycle receipt exceeds its payload bound.");
        }
    }
}
