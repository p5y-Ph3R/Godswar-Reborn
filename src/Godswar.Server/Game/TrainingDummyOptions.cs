using System.Collections.Frozen;
using System.Globalization;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class TrainingDummyOptions
{
    public bool Enabled { get; set; }

    public TrainingDummyIdentityOptions[] Identities { get; set; } = [];

    public static TrainingDummyIdentityOptions[] ParseIdentities(
        string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(ParseIdentity)
            .ToArray();
    }

    public void Normalize()
    {
        Identities ??= [];
        foreach (var identity in Identities)
        {
            identity?.NormalizeAndValidate();
        }

        if (Identities.Any(static identity => identity is null))
        {
            throw new InvalidDataException(
                "Game.TrainingDummies.Identities cannot contain null.");
        }
        if (Enabled && Identities.Length == 0)
        {
            throw new InvalidDataException(
                "Game.TrainingDummies requires at least one immutable " +
                "identity when enabled.");
        }
        if (Identities.Select(static value => value.CharacterId)
            .Distinct().Count() != Identities.Length)
        {
            throw new InvalidDataException(
                "Training-dummy character IDs must be unique.");
        }
        if (Identities.Select(static value => value.AccountId)
            .Distinct().Count() != Identities.Length)
        {
            throw new InvalidDataException(
                "Training-dummy account IDs must be unique.");
        }
    }

    private static TrainingDummyIdentityOptions ParseIdentity(string value)
    {
        var fields = value.Split(':', StringSplitOptions.TrimEntries);
        if (fields.Length != 7 ||
            !int.TryParse(fields[0], out var characterId) ||
            !int.TryParse(fields[1], out var accountId) ||
            !byte.TryParse(fields[3], out var camp) ||
            !byte.TryParse(fields[4], out var mapId) ||
            !float.TryParse(
                fields[5],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var positionX) ||
            !float.TryParse(
                fields[6],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var positionZ) ||
            !float.IsFinite(positionX) ||
            !float.IsFinite(positionZ))
        {
            throw new InvalidDataException(
                "GODSWAR_TRAINING_DUMMY_IDENTITIES entries must use " +
                "characterId:accountId:name:camp:map:x:z with finite " +
                "coordinates.");
        }

        return new TrainingDummyIdentityOptions
        {
            CharacterId = characterId,
            AccountId = accountId,
            Name = fields[2],
            Camp = camp,
            MapId = mapId,
            PositionX = positionX,
            PositionZ = positionZ
        };
    }
}

internal sealed class TrainingDummyIdentityOptions
{
    public int CharacterId { get; set; }

    public int AccountId { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte Camp { get; set; }

    public byte MapId { get; set; }

    public float PositionX { get; set; }

    public float PositionZ { get; set; }

    internal void NormalizeAndValidate()
    {
        Name = Name.Trim();
        if (CharacterId <= 0 || AccountId <= 0 ||
            string.IsNullOrEmpty(Name) ||
            Camp is not (GameDefaults.SpartaCamp or GameDefaults.AthensCamp) ||
            MapId is not (GameDefaults.SpartaCamp or GameDefaults.AthensCamp) ||
            MapId == Camp ||
            !float.IsFinite(PositionX) ||
            !float.IsFinite(PositionZ))
        {
            throw new InvalidDataException(
                "Each training dummy requires positive character/account " +
                "IDs, an exact name, an opposing camp on a Sparta/Athens " +
                "capital map, and " +
                "finite coordinates.");
        }
    }
}

internal readonly record struct TrainingDummyIdentity(
    int CharacterId,
    int AccountId,
    string Name,
    byte Camp,
    byte MapId,
    float PositionX,
    float PositionZ)
{
    public bool MatchesCore(GameCharacter character) =>
        character.Id == CharacterId &&
        character.AccountId == AccountId &&
        string.Equals(character.Name, Name, StringComparison.Ordinal) &&
        character.Camp == Camp;

    public bool MatchesPlacement(GameCharacter character) =>
        MatchesCore(character) &&
        character.CurrentMap == MapId &&
        character.PositionX == PositionX &&
        character.PositionZ == PositionZ;
}

internal sealed class TrainingDummyPolicy
{
    private readonly FrozenDictionary<int, TrainingDummyIdentity> _identities;

    private TrainingDummyPolicy(
        bool enabled,
        FrozenDictionary<int, TrainingDummyIdentity> identities)
    {
        Enabled = enabled;
        _identities = identities;
    }

    public static TrainingDummyPolicy Disabled { get; } = new(
        false,
        Array.Empty<KeyValuePair<int, TrainingDummyIdentity>>()
            .ToFrozenDictionary());

    public bool Enabled { get; }

    public static TrainingDummyPolicy Create(
        TrainingDummyOptions? options,
        ValidatedServerRuntimeProfile runtimeProfile)
    {
        ArgumentNullException.ThrowIfNull(runtimeProfile);
        if (options?.Enabled != true)
        {
            return Disabled;
        }
        if (runtimeProfile.RuntimeProfile !=
            ServerRuntimeProfileKind.LocalDevelopment)
        {
            throw new InvalidOperationException(
                "Training-dummy combat is restricted to LocalDevelopment.");
        }

        return new TrainingDummyPolicy(
            true,
            options.Identities
                .Select(static value => new TrainingDummyIdentity(
                    value.CharacterId,
                    value.AccountId,
                    value.Name,
                    value.Camp,
                    value.MapId,
                    value.PositionX,
                    value.PositionZ))
                .ToFrozenDictionary(static value => value.CharacterId));
    }

    public bool Contains(GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return Enabled &&
            _identities.TryGetValue(character.Id, out var identity) &&
            identity.MatchesPlacement(character);
    }

    public bool TryGetCoreIdentity(
        GameCharacter character,
        out TrainingDummyIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(character);
        identity = default;
        return Enabled &&
            _identities.TryGetValue(character.Id, out identity) &&
            identity.MatchesCore(character);
    }
}
