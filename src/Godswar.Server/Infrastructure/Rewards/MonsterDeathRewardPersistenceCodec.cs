using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Rewards;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Rewards;

internal static class MonsterDeathRewardPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ResultCode = "committed";
    public const string ConsumerKey = "progression_reward_projection_v1";
    public const string AggregateType = "character_progression";
    public const string EventType = "progression.monster_reward_settled";
    public const string OrderingPolicy = "strict";
    public const string CommandFamily = "monster_reward_settlement";
    public const string PrincipalType = "account";
    public const string RetentionPolicy = "permanent";

    private static readonly string[] RequiredProperties =
    [
        "contractVersion",
        "deathEventId",
        "runtimeInstanceId",
        "mapId",
        "monsterObjectId",
        "spawnGeneration",
        "deathHealthRevision",
        "characterId",
        "requestedExperience",
        "requestedTalentExperience",
        "experienceGained",
        "previousLevel",
        "currentLevel",
        "previousExperience",
        "currentExperience",
        "nextLevelExperience",
        "levelUps",
        "talentExperienceGained",
        "previousTalentExperience",
        "currentTalentExperience",
        "talentPointsGained",
        "previousTalentPoints",
        "currentTalentPoints",
        "progressionRevision",
        "auditReference",
        "outboxEventId"
    ];

    public static string AggregateKey(int characterId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:progression");
    }

    public static byte[] Encode(
        MonsterDeathRewardExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var buffer = new ArrayBufferWriter<byte>(1_024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteString("deathEventId", receipt.DeathEventId);
            writer.WriteString(
                "runtimeInstanceId",
                receipt.RuntimeInstanceId);
            writer.WriteNumber("mapId", receipt.MapId);
            writer.WriteNumber(
                "monsterObjectId",
                receipt.MonsterObjectId);
            writer.WriteNumber(
                "spawnGeneration",
                receipt.SpawnGeneration);
            writer.WriteNumber(
                "deathHealthRevision",
                receipt.DeathHealthRevision);
            writer.WriteNumber("characterId", receipt.CharacterId);
            writer.WriteNumber(
                "requestedExperience",
                receipt.RequestedExperience);
            writer.WriteNumber(
                "requestedTalentExperience",
                receipt.RequestedTalentExperience);
            writer.WriteNumber(
                "experienceGained",
                receipt.ExperienceGained);
            writer.WriteNumber("previousLevel", receipt.PreviousLevel);
            writer.WriteNumber("currentLevel", receipt.CurrentLevel);
            writer.WriteNumber(
                "previousExperience",
                receipt.PreviousExperience);
            writer.WriteNumber(
                "currentExperience",
                receipt.CurrentExperience);
            writer.WriteNumber(
                "nextLevelExperience",
                receipt.NextLevelExperience);
            writer.WriteStartArray("levelUps");
            foreach (var levelUp in receipt.LevelUps)
            {
                writer.WriteStartObject();
                writer.WriteNumber("level", levelUp.Level);
                writer.WriteNumber(
                    "currentExperience",
                    levelUp.CurrentExperience);
                writer.WriteNumber(
                    "nextLevelExperience",
                    levelUp.NextLevelExperience);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber(
                "talentExperienceGained",
                receipt.TalentExperienceGained);
            writer.WriteNumber(
                "previousTalentExperience",
                receipt.PreviousTalentExperience);
            writer.WriteNumber(
                "currentTalentExperience",
                receipt.CurrentTalentExperience);
            writer.WriteNumber(
                "talentPointsGained",
                receipt.TalentPointsGained);
            writer.WriteNumber(
                "previousTalentPoints",
                receipt.PreviousTalentPoints);
            writer.WriteNumber(
                "currentTalentPoints",
                receipt.CurrentTalentPoints);
            writer.WriteNumber(
                "progressionRevision",
                receipt.ProgressionRevision);
            writer.WriteString(
                "auditReference",
                receipt.AuditReference);
            writer.WriteString(
                "outboxEventId",
                receipt.OutboxEventId);
            writer.WriteEndObject();
        }

        EnsurePayloadBound(buffer.WrittenCount);
        return buffer.WrittenSpan.ToArray();
    }

    public static MonsterDeathRewardExecutionReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        EnsurePayloadBound(payload.Length);
        try
        {
            using var document = JsonDocument.Parse(
                payload.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 6
                });
            var root = document.RootElement;
            EnsureExactShape(root);
            if (root.GetProperty("contractVersion").GetInt16() !=
                ContractVersion)
            {
                throw new InvalidDataException(
                    "The stored monster reward contract is unsupported.");
            }

            var levelUps = ReadLevelUps(
                root.GetProperty("levelUps"));
            return new MonsterDeathRewardExecutionReceipt(
                root.GetProperty("deathEventId").GetGuid(),
                root.GetProperty("runtimeInstanceId").GetGuid(),
                root.GetProperty("mapId").GetByte(),
                root.GetProperty("monsterObjectId").GetUInt32(),
                root.GetProperty("spawnGeneration").GetUInt32(),
                root.GetProperty("deathHealthRevision").GetUInt64(),
                root.GetProperty("characterId").GetInt32(),
                root.GetProperty("requestedExperience").GetInt32(),
                root.GetProperty(
                    "requestedTalentExperience").GetInt32(),
                root.GetProperty("experienceGained").GetInt32(),
                root.GetProperty("previousLevel").GetInt32(),
                root.GetProperty("currentLevel").GetInt32(),
                root.GetProperty("previousExperience").GetInt32(),
                root.GetProperty("currentExperience").GetInt32(),
                root.GetProperty("nextLevelExperience").GetInt32(),
                levelUps,
                root.GetProperty(
                    "talentExperienceGained").GetInt32(),
                root.GetProperty(
                    "previousTalentExperience").GetInt32(),
                root.GetProperty(
                    "currentTalentExperience").GetInt32(),
                root.GetProperty("talentPointsGained").GetInt32(),
                root.GetProperty("previousTalentPoints").GetInt32(),
                root.GetProperty("currentTalentPoints").GetInt32(),
                root.GetProperty("progressionRevision").GetInt64(),
                RequiredString(root, "auditReference"),
                root.GetProperty("outboxEventId").GetGuid());
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            FormatException or
            InvalidOperationException or
            ArgumentException or
            OverflowException)
        {
            throw new InvalidDataException(
                "Stored monster reward evidence is malformed.",
                exception);
        }
    }

    public static MonsterDeathRewardExecutionReceipt DecodeAndVerify(
        string payloadJson,
        ReadOnlySpan<byte> expectedHash,
        string expectedResultCode,
        long expectedAuditId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (!string.Equals(
                expectedResultCode,
                ResultCode,
                StringComparison.Ordinal) ||
            expectedAuditId <= 0)
        {
            throw new InvalidDataException(
                "Stored monster reward identity is inconsistent.");
        }

        var receipt = Decode(Encoding.UTF8.GetBytes(payloadJson));
        if (!string.Equals(
                receipt.AuditReference,
                expectedAuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Stored monster reward audit identity is inconsistent.");
        }

        var actualHash = Hash(Encode(receipt));
        if (expectedHash.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(
                expectedHash,
                actualHash))
        {
            throw new InvalidDataException(
                "Stored monster reward result hash is invalid.");
        }

        return receipt;
    }

    public static byte[] Hash(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(payload);

    private static IReadOnlyList<MonsterDeathRewardLevelUp>
        ReadLevelUps(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() >
                MonsterDeathRewardProgressionContract
                    .MaximumCharacterLevel)
        {
            throw new InvalidDataException(
                "Stored monster reward level-ups are invalid.");
        }

        var levelUps =
            new List<MonsterDeathRewardLevelUp>(
                element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            EnsureLevelUpShape(item);
            levelUps.Add(new MonsterDeathRewardLevelUp(
                item.GetProperty("level").GetInt32(),
                item.GetProperty("currentExperience").GetInt32(),
                item.GetProperty("nextLevelExperience").GetInt32()));
        }
        return levelUps;
    }

    private static void EnsureExactShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Stored monster reward result must be an object.");
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!RequiredProperties.Contains(
                    property.Name,
                    StringComparer.Ordinal) ||
                !found.Add(property.Name))
            {
                throw new InvalidDataException(
                    "Stored monster reward result has unknown or duplicate fields.");
            }
        }
        if (found.Count != RequiredProperties.Length)
        {
            throw new InvalidDataException(
                "Stored monster reward result has missing fields.");
        }
    }

    private static void EnsureLevelUpShape(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Stored level-up evidence must be an object.");
        }
        var names = element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        if (names.Length != 3 ||
            names.Distinct(StringComparer.Ordinal).Count() != 3 ||
            !names.Contains("level", StringComparer.Ordinal) ||
            !names.Contains("currentExperience", StringComparer.Ordinal) ||
            !names.Contains(
                "nextLevelExperience",
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Stored level-up evidence has an invalid shape.");
        }
    }

    private static string RequiredString(
        JsonElement root,
        string propertyName) =>
        root.GetProperty(propertyName).GetString() ??
        throw new InvalidDataException(
            $"Stored monster reward has no {propertyName}.");

    private static void EnsurePayloadBound(int payloadBytes)
    {
        if (payloadBytes is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "Stored monster reward result exceeds its bound.");
        }
    }
}
