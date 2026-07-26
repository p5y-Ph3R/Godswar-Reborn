using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class ControlledHostTrustReceiptChecks
{
    private const string Password =
        "controlled-host-fixture-password";

    internal static void Run()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"reborn-trust-receipt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            RunInDirectory(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RunInDirectory(string directory)
    {
        var rootPath = Path.Combine(
            directory,
            "reborn-development-root.cer");
        var pfxPath = Path.Combine(
            directory,
            "reborn-development-server.pfx");
        var receiptPath = Path.Combine(
            directory,
            "current-user-trust-receipt.json");

        using var rootKey = RSA.Create(3072);
        using var rootWithKey = CreateRoot(rootKey);
        using var publicRoot =
            X509CertificateLoader.LoadCertificate(
                rootWithKey.RawData);
        using var leaf = CreateLeaf(rootWithKey);
        File.WriteAllBytes(rootPath, publicRoot.RawData);
        WritePfx(pfxPath, leaf, publicRoot);

        var baseline = NewReceipt(publicRoot, rootPath, pfxPath);
        WriteReceipt(receiptPath, baseline);
        ControlledHostValidationCommand
            .ValidateCertificateBundleForChecks(
                pfxPath,
                rootPath,
                receiptPath,
                Password);
        ControlledHostValidationCommand
            .ValidateTrustReceiptBindingsForChecks(
                rootPath,
                pfxPath,
                receiptPath);

        var migrated = Clone(baseline);
        migrated["migrationUtc"] =
            DateTimeOffset.UtcNow.ToString("O");
        WriteReceipt(receiptPath, migrated);
        ControlledHostValidationCommand
            .ValidateCertificateBundleForChecks(
                pfxPath,
                rootPath,
                receiptPath,
                Password);

        foreach (var state in new[]
        {
            "NoChange",
            "PendingInstall",
            "RemovalPending",
            "Removed"
        })
        {
            Reject(
                baseline,
                receiptPath,
                rootPath,
                pfxPath,
                receipt => receipt["state"] = state,
                $"trust receipt state {state}");
        }
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["schemaVersion"] = "2",
            "string trust receipt schema");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["schemaVersion"] = 3,
            "unknown trust receipt schema");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["installedByScript"] = "true",
            "string installedByScript");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["installedByScript"] = false,
            "non-script trust receipt");

        RejectField(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            "storeLocation",
            "LocalMachine");
        RejectField(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            "storeName",
            "My");
        RejectField(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            "subject",
            "CN=Attacker Root");
        RejectField(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            "thumbprint",
            new string('A', 40));
        RejectField(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            "rootSha256",
            new string('B', 64));
        RejectField(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            "rootCertificateFile",
            "other-root.cer");
        RejectField(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            "rootCertificateSha256",
            new string('C', 64));
        RejectField(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            "serverPfxFile",
            "other-server.pfx");
        RejectField(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            "serverPfxSha256",
            new string('D', 64));
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["rootSha256"] =
                ((string)receipt["rootSha256"]!).ToLowerInvariant(),
            "lowercase root hash");

        CheckTimestampRejections(
            baseline,
            receiptPath,
            rootPath,
            pfxPath);

        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["unexpected"] = "rejected",
            "extra trust receipt property");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt.Remove("serverPfxSha256"),
            "missing trust receipt property");

        var legacy = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["storeLocation"] = "CurrentUser",
            ["storeName"] = "Root",
            ["thumbprint"] = publicRoot.Thumbprint,
            ["rootSha256"] =
                Convert.ToHexString(
                    SHA256.HashData(publicRoot.RawData)),
            ["installedByScript"] = true
        };
        WriteReceipt(receiptPath, legacy);
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateTrustReceiptBindingsForChecks(
                    rootPath,
                    pfxPath,
                    receiptPath),
            "legacy trust receipt outside migration tool");

        WriteReceipt(receiptPath, baseline);
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateCertificateBundleForChecks(
                    pfxPath,
                    rootPath,
                    receiptPath,
                    "wrong-password"),
            "wrong PFX password");

        using var extra = CreateUnrelatedCertificate();
        var extraCollection =
            new X509Certificate2Collection();
        extraCollection.Add(leaf);
        extraCollection.Add(publicRoot);
        extraCollection.Add(extra);
        File.WriteAllBytes(
            pfxPath,
            extraCollection.Export(
                X509ContentType.Pkcs12,
                Password) ??
                throw new CryptographicException(
                    "The extra-certificate PFX export failed."));
        var extraReceipt =
            NewReceipt(publicRoot, rootPath, pfxPath);
        WriteReceipt(receiptPath, extraReceipt);
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateCertificateBundleForChecks(
                    pfxPath,
                    rootPath,
                    receiptPath,
                    Password),
            "PFX with an extra certificate");
    }

    private static void CheckTimestampRejections(
        Dictionary<string, object?> baseline,
        string receiptPath,
        string rootPath,
        string pfxPath)
    {
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["createdUtc"] = "not-a-time",
            "invalid created timestamp");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["createdUtc"] =
                DateTimeOffset.UtcNow.ToOffset(
                    TimeSpan.FromHours(12)).ToString("O"),
            "non-UTC created timestamp");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["installedUtc"] =
                DateTimeOffset.UtcNow.AddDays(-2).ToString("O"),
            "installed timestamp before creation");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["migrationUtc"] =
                DateTimeOffset.UtcNow.AddDays(-2).ToString("O"),
            "migration timestamp before creation");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["removalStartedUtc"] =
                DateTimeOffset.UtcNow.ToString("O"),
            "installed receipt with removal start");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["removedUtc"] =
                DateTimeOffset.UtcNow.ToString("O"),
            "installed receipt with removal completion");
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt["installedUtc"] = 7,
            "numeric installed timestamp");
    }

    private static void RejectField(
        Dictionary<string, object?> baseline,
        string receiptPath,
        string rootPath,
        string pfxPath,
        string field,
        object value) =>
        Reject(
            baseline,
            receiptPath,
            rootPath,
            pfxPath,
            receipt => receipt[field] = value,
            $"trust receipt {field} binding");

    private static void Reject(
        Dictionary<string, object?> baseline,
        string receiptPath,
        string rootPath,
        string pfxPath,
        Action<Dictionary<string, object?>> mutate,
        string description)
    {
        var receipt = Clone(baseline);
        mutate(receipt);
        WriteReceipt(receiptPath, receipt);
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateTrustReceiptBindingsForChecks(
                    rootPath,
                    pfxPath,
                    receiptPath),
            description);
    }

    private static Dictionary<string, object?> Clone(
        Dictionary<string, object?> source) =>
        source.ToDictionary(
            static entry => entry.Key,
            static entry => entry.Value,
            StringComparer.Ordinal);

    private static Dictionary<string, object?> NewReceipt(
        X509Certificate2 root,
        string rootPath,
        string pfxPath)
    {
        var created = DateTimeOffset.UtcNow.AddMinutes(-1);
        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["state"] = "Installed",
            ["storeLocation"] = "CurrentUser",
            ["storeName"] = "Root",
            ["subject"] = "CN=Reborn Development Root CA",
            ["thumbprint"] = root.Thumbprint,
            ["rootSha256"] =
                Convert.ToHexString(SHA256.HashData(root.RawData)),
            ["rootCertificateFile"] =
                "reborn-development-root.cer",
            ["rootCertificateSha256"] =
                Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(rootPath))),
            ["serverPfxFile"] =
                "reborn-development-server.pfx",
            ["serverPfxSha256"] =
                Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(pfxPath))),
            ["installedByScript"] = true,
            ["createdUtc"] = created.ToString("O"),
            ["installedUtc"] =
                created.AddSeconds(1).ToString("O"),
            ["migrationUtc"] = null,
            ["removalStartedUtc"] = null,
            ["removedUtc"] = null
        };
    }

    private static X509Certificate2 CreateRoot(RSA key)
    {
        var request = new CertificateRequest(
            "CN=Reborn Development Root CA",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign |
                    X509KeyUsageFlags.CrlSign,
                critical: true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddHours(2));
    }

    private static X509Certificate2 CreateLeaf(
        X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=login.reborn.test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature |
                    X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        var usages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1")
        };
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                usages,
                critical: true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("login.reborn.test");
        san.AddDnsName("game.reborn.test");
        request.CertificateExtensions.Add(san.Build());
        using var publicLeaf = request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16));
        return publicLeaf.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 CreateUnrelatedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=unrelated.test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var withKey = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadCertificate(
            withKey.RawData);
    }

    private static void WritePfx(
        string path,
        X509Certificate2 leaf,
        X509Certificate2 root)
    {
        var collection = new X509Certificate2Collection();
        collection.Add(leaf);
        collection.Add(root);
        File.WriteAllBytes(
            path,
            collection.Export(
                X509ContentType.Pkcs12,
                Password) ??
                throw new CryptographicException(
                    "The exact fixture PFX export failed."));
    }

    private static void WriteReceipt(
        string path,
        Dictionary<string, object?> receipt) =>
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(receipt));
}
