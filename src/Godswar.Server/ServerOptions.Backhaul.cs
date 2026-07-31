namespace Godswar.Server;

internal sealed partial class ServerOptions
{
    private void ApplyBackhaulEnvironment()
    {
        Backhaul.Enabled = ReadBool(
            "GODSWAR_BACKHAUL_WORKER_ENABLED",
            Backhaul.Enabled);
        Backhaul.BindHost =
            Environment.GetEnvironmentVariable(
                "GODSWAR_BACKHAUL_WORKER_BIND_HOST") ??
            Backhaul.BindHost;
        Backhaul.Port = ReadInt(
            "GODSWAR_BACKHAUL_WORKER_PORT",
            Backhaul.Port);
        Backhaul.ReplayCapacity = ReadInt(
            "GODSWAR_BACKHAUL_WORKER_REPLAY_CAPACITY",
            Backhaul.ReplayCapacity);
        Backhaul.CertificatePath =
            Environment.GetEnvironmentVariable(
                "GODSWAR_BACKHAUL_WORKER_CERTIFICATE_PATH") ??
            Backhaul.CertificatePath;
    }
}
