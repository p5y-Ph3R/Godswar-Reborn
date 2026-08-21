using System.Text;
using System.Text.Json;

namespace Godswar.Server.CombatDummyHost;

internal sealed class CombatDummyReadiness
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<int, DummyState> _states;

    public CombatDummyReadiness(string path)
    {
        _path = Path.GetFullPath(path);
        _states = CombatDummyDefinition.All.ToDictionary(
            static value => value.CharacterId,
            static value => new DummyState(
                value.CharacterId,
                value.CharacterName,
                Ready: false,
                Status: "Starting",
                Detail: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        Publish();
    }

    public void Connecting(CombatDummyDefinition definition) =>
        Update(definition, ready: false, "Connecting", detail: null);

    public void Ready(CombatDummyDefinition definition) =>
        Update(definition, ready: true, "Ready", detail: null);

    public void Heartbeat(CombatDummyDefinition definition)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(
                    definition.CharacterId,
                    out var current) ||
                !current.Ready)
            {
                return;
            }

            _states[definition.CharacterId] = current with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            PublishLocked();
        }
    }

    public void Unavailable(
        CombatDummyDefinition definition,
        string status,
        string? detail) =>
        Update(definition, ready: false, status, Sanitize(detail));

    private void Update(
        CombatDummyDefinition definition,
        bool ready,
        string status,
        string? detail)
    {
        lock (_gate)
        {
            _states[definition.CharacterId] = new DummyState(
                definition.CharacterId,
                definition.CharacterName,
                ready,
                status,
                detail,
                DateTimeOffset.UtcNow);
            PublishLocked();
        }
    }

    private void Publish()
    {
        lock (_gate)
        {
            PublishLocked();
        }
    }

    private void PublishLocked()
    {
        var ordered = _states.Values
            .OrderBy(static value => value.CharacterId)
            .ToArray();
        var snapshot = new ReadinessSnapshot(
            ProcessId: Environment.ProcessId,
            ExpectedCount: ordered.Length,
            ReadyCount: ordered.Count(static value => value.Ready),
            AllReady: ordered.All(static value => value.Ready),
            IdentityManifest: CombatDummyDefinition.IdentityManifest,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            Dummies: ordered);
        var directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException(
                "The readiness path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + $".tmp.{Environment.ProcessId}";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, _path, overwrite: true);
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 240 ? sanitized : sanitized[..240];
    }

    private sealed record ReadinessSnapshot(
        int ProcessId,
        int ExpectedCount,
        int ReadyCount,
        bool AllReady,
        string IdentityManifest,
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyList<DummyState> Dummies);

    private sealed record DummyState(
        int CharacterId,
        string CharacterName,
        bool Ready,
        string Status,
        string? Detail,
        DateTimeOffset UpdatedAtUtc);
}
