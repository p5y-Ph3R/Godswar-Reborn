using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Stable stage-operation identities derived from the server-issued admission
/// identity. They are deterministic across crash/retry and are never sampled
/// from a clock or regenerated with Guid.NewGuid during execution.
/// </summary>
internal static class MedusaAdmissionSagaOperationIds
{
    public static Guid RuntimeReady(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "runtime-ready");

    public static Guid RuntimeTransferToken(
        MedusaAdmissionId admissionId,
        string admissionRequestHash) =>
        DeriveBound(
            admissionId,
            "runtime-transfer-token",
            admissionRequestHash);

    public static Guid TransferPrepare(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "transfer-prepare");

    public static Guid TransferBarrier(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "transfer-barrier");

    public static Guid TransferCommit(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "transfer-commit");

    public static Guid TransferAbort(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "transfer-abort");

    public static Guid ConsumedRunning(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "consumed-running");

    public static Guid RuntimeStart(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "runtime-start");

    public static Guid RuntimeRelease(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "runtime-release");

    public static Guid DurableRelease(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "durable-release");

    public static Guid Completed(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "terminal-completed");

    public static Guid Abandoned(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "terminal-abandoned");

    public static Guid TimedOut(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "terminal-timed-out");

    public static Guid RuntimeRetire(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "runtime-retire");

    public static Guid RosterEgress(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "roster-egress");

    public static Guid CleanupCompleted(MedusaAdmissionId admissionId) =>
        Derive(admissionId, "cleanup-completed");

    private static Guid Derive(
        MedusaAdmissionId admissionId,
        string stage)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        var stageBytes = Encoding.ASCII.GetBytes(
            $"medusa-admission-saga-v1:{stage}:");
        Span<byte> identity = stackalloc byte[16];
        if (!admissionId.Value.TryWriteBytes(
                identity,
                bigEndian: true,
                out var written) ||
            written != identity.Length)
        {
            throw new InvalidOperationException(
                "The admission identity could not be encoded.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(stageBytes);
        hash.AppendData(identity);
        var bytes = hash.GetHashAndReset();
        // Mark this deterministic opaque ID as RFC-4122 variant/version 5.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    private static Guid DeriveBound(
        MedusaAdmissionId admissionId,
        string stage,
        string admissionRequestHash)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        MedusaDurableAdmissionPolicy.ValidateHash(
            admissionRequestHash,
            nameof(admissionRequestHash));
        var stageBytes = Encoding.ASCII.GetBytes(
            $"medusa-admission-saga-v1:{stage}:");
        Span<byte> identity = stackalloc byte[16];
        if (!admissionId.Value.TryWriteBytes(
                identity,
                bigEndian: true,
                out var written) ||
            written != identity.Length)
        {
            throw new InvalidOperationException(
                "The admission identity could not be encoded.");
        }
        var requestHashBytes = Convert.FromHexString(admissionRequestHash);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(stageBytes);
        hash.AppendData(identity);
        hash.AppendData(requestHashBytes);
        var bytes = hash.GetHashAndReset();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
}
