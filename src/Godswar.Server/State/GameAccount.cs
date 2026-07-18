namespace Godswar.Server.State;

internal sealed class GameAccount
{
    public int Id { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public VipTier VipTier { get; set; }

    public DateTimeOffset? VipExpiresAt { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
