using System.Net;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecurePhase4AcceptanceFaultOptions
{
    internal const string EnabledEnvironmentVariable =
        "GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED";
    internal const string RuntimeEnvironmentVariable =
        "DOTNET_ENVIRONMENT";
    internal const string AspNetRuntimeEnvironmentVariable =
        "ASPNETCORE_ENVIRONMENT";
    internal const string RequiredRuntimeEnvironment = "Development";

    private string? _aspNetRuntimeEnvironment;
    private string? _runtimeEnvironment;

    public bool Enabled { get; private set; }

    internal void ApplyEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable(
            EnabledEnvironmentVariable);
        if (configured is null)
        {
            Enabled = false;
            _runtimeEnvironment = null;
            _aspNetRuntimeEnvironment = null;
            return;
        }
        if (!bool.TryParse(configured, out var enabled))
        {
            throw new InvalidDataException(
                $"{EnabledEnvironmentVariable} must be exactly true or false.");
        }

        Enabled = enabled;
        _runtimeEnvironment = Environment.GetEnvironmentVariable(
            RuntimeEnvironmentVariable);
        _aspNetRuntimeEnvironment =
            Environment.GetEnvironmentVariable(
                AspNetRuntimeEnvironmentVariable);
    }

    internal void Validate(SecureNetworkOptions secure)
    {
        ArgumentNullException.ThrowIfNull(secure);
        if (!Enabled)
        {
            return;
        }

        if (!secure.Enabled ||
            !secure.Udp.Enabled ||
            !secure.Udp.GameplayMovementEnabled)
        {
            throw new InvalidDataException(
                "Phase 4 acceptance faults require secure TLS, UDP, and authoritative movement.");
        }
        if (!string.Equals(
                _runtimeEnvironment,
                RequiredRuntimeEnvironment,
                StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(
                    _aspNetRuntimeEnvironment) &&
             !string.Equals(
                 _aspNetRuntimeEnvironment,
                 RequiredRuntimeEnvironment,
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Phase 4 acceptance faults require an unambiguous Development runtime environment.");
        }
        if (!IsLiteralLoopback(secure.Login.BindHost) ||
            !IsLiteralLoopback(secure.Game.BindHost) ||
            !IsLiteralLoopback(secure.Udp.BindHost))
        {
            throw new InvalidDataException(
                "Phase 4 acceptance faults require loopback-only TLS and UDP binds.");
        }
    }

    private static bool IsLiteralLoopback(string value) =>
        IPAddress.TryParse(value, out var address) &&
        IPAddress.IsLoopback(address);
}
