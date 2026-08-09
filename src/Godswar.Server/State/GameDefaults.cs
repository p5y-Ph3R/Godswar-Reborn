namespace Godswar.Server.State;

internal static class GameDefaults
{
    public const byte SpartaCamp = 0;

    public const byte AthensCamp = 1;

    public const byte SpartaCapitalMap = 0;

    public const byte AthensCapitalMap = 1;

    public const float StartingPositionX = 165.0f;

    public const float StartingPositionZ = -97.0f;

    public const string EmptyKitBag =
        "[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#";

    public const string StarterKitBag =
        "[4000,,,,,,0,10,1,1,0]#[4030,,,,,,0,10,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#";

    public static void InitializeStartingLocation(GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);

        character.Camp = character.Camp == SpartaCamp ? SpartaCamp : AthensCamp;
        character.CurrentMap = character.Camp == SpartaCamp ? SpartaCapitalMap : AthensCapitalMap;
        character.PositionX = StartingPositionX;
        character.PositionZ = StartingPositionZ;
    }

    public static string DefaultEquipment(byte profession)
    {
        return profession switch
        {
            0 => "[]#[]#[]#[2100,,,,,,1,1,1,1,0]#[]#[]#[2900,,,,,,1,1,1,1,0]#[]#[]#[]#[1000,,,,,,1,1,1,1,0]#[2000,,,,,,1,1,1,1,0]#[8040,,,,,,1,1,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#",
            1 => "[]#[]#[]#[2100,,,,,,1,1,1,1,0]#[]#[]#[2900,,,,,,1,1,1,1,0]#[]#[]#[]#[1400,,,,,,1,1,1,1,0]#[]#[8040,,,,,,1,1,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#",
            2 => "[]#[]#[]#[2100,,,,,,1,1,1,1,0]#[]#[]#[2900,,,,,,1,1,1,1,0]#[]#[]#[]#[1700,,,,,,1,1,1,1,0]#[2000,,,,,,1,1,1,1,0]#[8040,,,,,,1,1,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#",
            3 => "[]#[]#[]#[2100,,,,,,1,1,1,1,0]#[]#[]#[2900,,,,,,1,1,1,1,0]#[]#[]#[]#[1800,,,,,,1,1,1,1,0]#[]#[8040,,,,,,1,1,1,1,0]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#[]#",
            _ => DefaultEquipment(0)
        };
    }
}
