using System.Globalization;

namespace Godswar.Server.CombatDummyHost;

internal sealed record CombatDummyDefinition(
    int AccountId,
    int CharacterId,
    string AccountUsername,
    string CharacterName,
    byte Camp,
    byte Profession,
    byte MapId,
    float PositionX,
    float PositionZ)
{
    public static IReadOnlyList<CombatDummyDefinition> All { get; } =
    [
        new(
            AccountId: 7001,
            CharacterId: 7001,
            "dummy_ares_bulwark",
            "AresBulwark",
            Camp: 1,
            Profession: 0,
            MapId: 0,
            PositionX: 148f,
            PositionZ: -154f),
        new(
            AccountId: 7002,
            CharacterId: 7002,
            "dummy_ares_mirage",
            "AresMirage",
            Camp: 1,
            Profession: 1,
            MapId: 0,
            PositionX: 148f,
            PositionZ: -162f),
        new(
            AccountId: 7003,
            CharacterId: 7003,
            "dummy_athena_bulwark",
            "AthenaBulwark",
            Camp: 0,
            Profession: 0,
            MapId: 1,
            PositionX: 148f,
            PositionZ: -154f),
        new(
            AccountId: 7004,
            CharacterId: 7004,
            "dummy_athena_mirage",
            "AthenaMirage",
            Camp: 0,
            Profession: 1,
            MapId: 1,
            PositionX: 148f,
            PositionZ: -162f)
    ];

    public static string IdentityManifest { get; } = string.Join(
        ',',
        All.Select(static value =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}:{2}:{3}:{4}:{5:0.###}:{6:0.###}",
                value.CharacterId,
                value.AccountId,
                value.CharacterName,
                value.Camp,
                value.MapId,
                value.PositionX,
                value.PositionZ)));
}
