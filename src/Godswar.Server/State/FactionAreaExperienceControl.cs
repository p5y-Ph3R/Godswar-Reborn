namespace Godswar.Server.State;

internal sealed class FactionAreaExperienceControl
{
    public byte MapId { get; set; }

    public byte ControllingCamp { get; set; }

    public string BossTemplateKey { get; set; } = string.Empty;

    public string DeathToken { get; set; } = string.Empty;

    public int BonusBasisPoints { get; set; } = 2_500;

    public DateTimeOffset ActivatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
