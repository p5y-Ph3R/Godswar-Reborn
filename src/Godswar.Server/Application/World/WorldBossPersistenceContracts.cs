namespace Godswar.Server.Application.World;

internal interface IWorldBossAreaControlStore
{
    Task<WorldBossAreaActivationResult> ActivateAsync(
        WorldBossAreaActivation activation,
        CancellationToken cancellationToken = default);
}

internal interface IWorldBossRespawnReader
{
    Task<WorldBossRespawnSnapshot?> ReadActiveAsync(
        WorldBossRespawnReadRequest request,
        CancellationToken cancellationToken = default);
}

internal readonly record struct WorldBossAreaActivation(
    short MapId,
    string BossTemplateKey,
    byte ControllingCamp,
    DateTimeOffset KilledAtUtc,
    string DeathToken);

internal readonly record struct WorldBossRespawnReadRequest(
    short MapId,
    DateTimeOffset ReadAtUtc);

internal sealed record WorldBossAreaControlSnapshot(
    short MapId,
    byte ControllingCamp,
    string BossTemplateKey,
    string DeathToken,
    int BonusBasisPoints,
    DateTimeOffset ActivatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed record WorldBossRespawnSnapshot(
    short MapId,
    string BossTemplateKey,
    DateTimeOffset RespawnAtUtc);

internal enum WorldBossAreaActivationDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    Stale = 3,
    NotConfigured = 4,
    Invalid = 5
}

internal sealed record WorldBossAreaActivationResult(
    WorldBossAreaActivationDisposition Disposition,
    WorldBossAreaControlSnapshot? Control = null)
{
    public bool IsSuccess => Disposition is
        WorldBossAreaActivationDisposition.Committed or
        WorldBossAreaActivationDisposition.Duplicate;

    public static WorldBossAreaActivationResult Committed(
        WorldBossAreaControlSnapshot control) =>
        new(WorldBossAreaActivationDisposition.Committed, control);

    public static WorldBossAreaActivationResult Duplicate(
        WorldBossAreaControlSnapshot control) =>
        new(WorldBossAreaActivationDisposition.Duplicate, control);

    public static WorldBossAreaActivationResult Stale(
        WorldBossAreaControlSnapshot control) =>
        new(WorldBossAreaActivationDisposition.Stale, control);

    public static WorldBossAreaActivationResult NotConfigured() =>
        new(WorldBossAreaActivationDisposition.NotConfigured);

    public static WorldBossAreaActivationResult Invalid() =>
        new(WorldBossAreaActivationDisposition.Invalid);
}

internal static class WorldBossPersistenceContract
{
    public const int MaximumBossTemplateKeyLength = 128;
    public const int MaximumDeathTokenLength = 64;

    public static bool IsValid(WorldBossAreaActivation activation) =>
        activation.MapId >= 0 &&
        activation.ControllingCamp <= 1 &&
        IsBoundedText(
            activation.BossTemplateKey,
            MaximumBossTemplateKeyLength) &&
        IsBoundedText(
            activation.DeathToken,
            MaximumDeathTokenLength) &&
        IsUtc(activation.KilledAtUtc);

    public static void Validate(WorldBossRespawnReadRequest request)
    {
        if (request.MapId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The world-boss map ID cannot be negative.");
        }

        if (!IsUtc(request.ReadAtUtc))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The world-boss read timestamp must be non-default UTC.");
        }
    }

    public static void Validate(WorldBossAreaControlSnapshot control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control.MapId < 0 ||
            control.ControllingCamp > 1 ||
            !IsBoundedText(
                control.BossTemplateKey,
                MaximumBossTemplateKeyLength) ||
            !IsBoundedText(
                control.DeathToken,
                MaximumDeathTokenLength) ||
            control.BonusBasisPoints < 0 ||
            !IsUtc(control.ActivatedAtUtc) ||
            !IsUtc(control.ExpiresAtUtc) ||
            control.ExpiresAtUtc <= control.ActivatedAtUtc)
        {
            throw new InvalidDataException(
                "The world-boss area-control projection is invalid.");
        }
    }

    public static void Validate(WorldBossRespawnSnapshot respawn)
    {
        ArgumentNullException.ThrowIfNull(respawn);
        if (respawn.MapId < 0 ||
            !IsBoundedText(
                respawn.BossTemplateKey,
                MaximumBossTemplateKeyLength) ||
            !IsUtc(respawn.RespawnAtUtc))
        {
            throw new InvalidDataException(
                "The world-boss respawn projection is invalid.");
        }
    }

    private static bool IsBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.IndexOf('\0') < 0;

    private static bool IsUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;
}
