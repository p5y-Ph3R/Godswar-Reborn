using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Godswar.Server.Operations;

internal static partial class ControlledHostValidationCommand
{
    private const string ServerAuthenticationOid =
        "1.3.6.1.5.5.7.3.1";
    private const string Sha256WithRsaOid =
        "1.2.840.113549.1.1.11";
    private const string ExpectedRootSubject =
        "CN=Reborn Development Root CA";
    private const string ExpectedLeafSubject =
        "CN=login.reborn.test";
    private static readonly string[] ExpectedDnsIdentities =
        ["game.reborn.test", "login.reborn.test"];

    internal static void ValidateCertificateBundle(
        string pfxPath,
        string rootCertificatePath,
        string trustReceiptPath,
        string password) =>
        ValidateCertificateBundleCore(
            pfxPath,
            rootCertificatePath,
            trustReceiptPath,
            password,
            requireInstalledRoot: true);

    internal static void ValidateCertificateBundleForChecks(
        string pfxPath,
        string rootCertificatePath,
        string trustReceiptPath,
        string password) =>
        ValidateCertificateBundleCore(
            pfxPath,
            rootCertificatePath,
            trustReceiptPath,
            password,
            requireInstalledRoot: false);

    private static void ValidateCertificateBundleCore(
        string pfxPath,
        string rootCertificatePath,
        string trustReceiptPath,
        string password,
        bool requireInstalledRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        AssertBoundedFile(pfxPath, 64 * 1024);
        AssertBoundedFile(rootCertificatePath, 16 * 1024);
        AssertBoundedFile(trustReceiptPath, 16 * 1024);

        var receipt = ReadTrustReceipt(trustReceiptPath);
        using var issuedRoot =
            X509CertificateLoader.LoadCertificateFromFile(
                rootCertificatePath);
        ValidateIssuedRootProfile(issuedRoot);
        ValidateRootReceipt(
            issuedRoot,
            receipt,
            rootCertificatePath,
            pfxPath);
        if (requireInstalledRoot)
        {
            ValidateInstalledRoot(issuedRoot, receipt);
        }

        var collection =
            X509CertificateLoader.LoadPkcs12CollectionFromFile(
                pfxPath,
                password,
                X509KeyStorageFlags.EphemeralKeySet);
        try
        {
            if (collection.Count != 2)
            {
                throw new InvalidDataException(
                    "The controlled-host PFX shape is not exact.");
            }
            var privateCertificates =
                collection.Where(static certificate =>
                    certificate.HasPrivateKey).ToArray();
            var publicCertificates =
                collection.Where(static certificate =>
                    !certificate.HasPrivateKey).ToArray();
            if (privateCertificates.Length != 1 ||
                publicCertificates.Length != 1 ||
                !publicCertificates[0].RawData.AsSpan().SequenceEqual(
                    issuedRoot.RawData))
            {
                throw new InvalidDataException(
                    "The controlled-host PFX is not leaf plus issued root.");
            }

            var leaf = privateCertificates[0];
            ValidateCertificateIdentities(leaf);
            ValidateLeafProfile(leaf, issuedRoot);
            ValidateExactChain(leaf, issuedRoot);
        }
        finally
        {
            foreach (var certificate in collection)
            {
                certificate.Dispose();
            }
        }
    }

    internal static void ValidateTrustReceiptBindingsForChecks(
        string rootCertificatePath,
        string pfxPath,
        string trustReceiptPath)
    {
        AssertBoundedFile(rootCertificatePath, 16 * 1024);
        AssertBoundedFile(pfxPath, 64 * 1024);
        AssertBoundedFile(trustReceiptPath, 16 * 1024);
        using var root =
            X509CertificateLoader.LoadCertificateFromFile(
                rootCertificatePath);
        var receipt = ReadTrustReceipt(trustReceiptPath);
        ValidateRootReceipt(
            root,
            receipt,
            rootCertificatePath,
            pfxPath);
    }

    internal static void ValidateCertificateIdentities(
        X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var extensions = certificate.Extensions
            .Cast<X509Extension>()
            .Where(static extension =>
                extension.Oid?.Value == "2.5.29.17")
            .ToArray();
        if (extensions.Length != 1)
        {
            throw new InvalidDataException(
                "The TLS leaf must have exactly one SAN extension.");
        }

        var reader =
            new AsnReader(
                extensions[0].RawData,
                AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var dnsNames = new List<string>();
        var dnsTag = new Asn1Tag(TagClass.ContextSpecific, 2);
        while (sequence.HasData)
        {
            var tag = sequence.PeekTag();
            if (!tag.HasSameClassAndValue(dnsTag))
            {
                throw new InvalidDataException(
                    "The TLS SAN contains an unexpected identity type.");
            }
            dnsNames.Add(
                sequence.ReadCharacterString(
                    UniversalTagNumber.IA5String,
                    dnsTag));
        }
        reader.ThrowIfNotEmpty();

        dnsNames.Sort(StringComparer.OrdinalIgnoreCase);
        if (dnsNames.Count != ExpectedDnsIdentities.Length ||
            !dnsNames.SequenceEqual(
                ExpectedDnsIdentities,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The TLS SAN identities are not the exact acceptance set.");
        }
    }

    internal static void ValidateLeafUsage(X509Certificate2 leaf)
    {
        var constraints = leaf.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .ToArray();
        var keyUsage = leaf.Extensions
            .OfType<X509KeyUsageExtension>()
            .ToArray();
        var enhancedUsage = leaf.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .ToArray();
        var expectedKeyUsage =
            X509KeyUsageFlags.DigitalSignature |
            X509KeyUsageFlags.KeyEncipherment;
        if (constraints.Length != 1 ||
            constraints[0].CertificateAuthority ||
            !constraints[0].Critical ||
            constraints[0].HasPathLengthConstraint ||
            keyUsage.Length != 1 ||
            !keyUsage[0].Critical ||
            keyUsage[0].KeyUsages != expectedKeyUsage ||
            enhancedUsage.Length != 1 ||
            !enhancedUsage[0].Critical ||
            enhancedUsage[0].EnhancedKeyUsages.Count != 1 ||
            enhancedUsage[0].EnhancedKeyUsages[0].Value !=
                ServerAuthenticationOid)
        {
            throw new InvalidDataException(
                "The TLS leaf usage is not exact server authentication.");
        }
    }

    internal static void ValidateIssuedRootProfile(
        X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var constraints = root.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .ToArray();
        var keyUsage = root.Extensions
            .OfType<X509KeyUsageExtension>()
            .ToArray();
        var expectedKeyUsage =
            X509KeyUsageFlags.KeyCertSign |
            X509KeyUsageFlags.CrlSign;
        using var rsa = root.GetRSAPublicKey();
        if (root.HasPrivateKey ||
            root.Subject != ExpectedRootSubject ||
            root.Issuer != ExpectedRootSubject ||
            !root.SubjectName.RawData.AsSpan().SequenceEqual(
                root.IssuerName.RawData) ||
            root.SignatureAlgorithm.Value != Sha256WithRsaOid ||
            rsa is null ||
            rsa.KeySize != 3072 ||
            constraints.Length != 1 ||
            !constraints[0].Critical ||
            !constraints[0].CertificateAuthority ||
            constraints[0].HasPathLengthConstraint ||
            keyUsage.Length != 1 ||
            !keyUsage[0].Critical ||
            keyUsage[0].KeyUsages != expectedKeyUsage)
        {
            throw new InvalidDataException(
                "The issued development root profile is not exact.");
        }
        ValidateCurrentValidity(root, "issued development root");

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode =
            X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode =
            X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (!chain.Build(root) ||
            chain.ChainElements.Count != 1 ||
            !chain.ChainElements[0].Certificate.RawData
                .AsSpan().SequenceEqual(root.RawData))
        {
            throw new InvalidDataException(
                "The issued development root is not exactly self-signed.");
        }
    }

    internal static void ValidateLeafProfile(
        X509Certificate2 leaf,
        X509Certificate2 issuedRoot)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        ArgumentNullException.ThrowIfNull(issuedRoot);
        using var rsa = leaf.GetRSAPublicKey();
        if (!leaf.HasPrivateKey ||
            leaf.Subject != ExpectedLeafSubject ||
            !leaf.IssuerName.RawData.AsSpan().SequenceEqual(
                issuedRoot.SubjectName.RawData) ||
            leaf.SignatureAlgorithm.Value != Sha256WithRsaOid ||
            rsa is null ||
            rsa.KeySize != 2048)
        {
            throw new InvalidDataException(
                "The TLS leaf certificate profile is not exact.");
        }
        ValidateCurrentValidity(leaf, "TLS leaf");
        ValidateLeafUsage(leaf);
    }

    private static void ValidateCurrentValidity(
        X509Certificate2 certificate,
        string label)
    {
        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() ||
            now > certificate.NotAfter.ToUniversalTime())
        {
            throw new InvalidDataException(
                $"The {label} is outside its validity window.");
        }
    }

    private static void ValidateExactChain(
        X509Certificate2 leaf,
        X509Certificate2 issuedRoot)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode =
            X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(issuedRoot);
        chain.ChainPolicy.RevocationMode =
            X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(
            new Oid(ServerAuthenticationOid));
        if (!chain.Build(leaf) ||
            chain.ChainElements.Count != 2 ||
            !chain.ChainElements[0].Certificate.RawData
                .AsSpan().SequenceEqual(leaf.RawData) ||
            !chain.ChainElements[1].Certificate.RawData
                .AsSpan().SequenceEqual(issuedRoot.RawData))
        {
            throw new InvalidDataException(
                "The TLS leaf does not build only to the issued root.");
        }
    }

    private static TrustReceipt ReadTrustReceipt(string path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(path));
        var root = document.RootElement;
        var expectedProperties = new HashSet<string>(
            [
                "schemaVersion",
                "state",
                "storeLocation",
                "storeName",
                "subject",
                "thumbprint",
                "rootSha256",
                "rootCertificateFile",
                "rootCertificateSha256",
                "serverPfxFile",
                "serverPfxSha256",
                "installedByScript",
                "createdUtc",
                "installedUtc",
                "migrationUtc",
                "removalStartedUtc",
                "removedUtc"
            ],
            StringComparer.Ordinal);
        var observedProperties =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!observedProperties.Add(property.Name) ||
                !expectedProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    "The trust receipt property set is not exact.");
            }
        }
        if (!observedProperties.SetEquals(expectedProperties))
        {
            throw new InvalidDataException(
                "The trust receipt property set is incomplete.");
        }

        var createdUtc =
            ReadRequiredUtcTimestamp(root, "createdUtc");
        var installedUtc =
            ReadRequiredUtcTimestamp(root, "installedUtc");
        var migrationUtc =
            ReadOptionalUtcTimestamp(root, "migrationUtc");
        if (installedUtc < createdUtc ||
            (migrationUtc is not null &&
             migrationUtc.Value < createdUtc) ||
            root.GetProperty("removalStartedUtc").ValueKind !=
                JsonValueKind.Null ||
            root.GetProperty("removedUtc").ValueKind !=
                JsonValueKind.Null)
        {
            throw new InvalidDataException(
                "The installed trust receipt lifecycle is invalid.");
        }
        return new TrustReceipt(
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("state").GetString() ?? "",
            root.GetProperty("storeLocation").GetString() ?? "",
            root.GetProperty("storeName").GetString() ?? "",
            root.GetProperty("subject").GetString() ?? "",
            root.GetProperty("thumbprint").GetString() ?? "",
            root.GetProperty("rootSha256").GetString() ?? "",
            root.GetProperty("rootCertificateFile").GetString() ?? "",
            root.GetProperty("rootCertificateSha256").GetString() ?? "",
            root.GetProperty("serverPfxFile").GetString() ?? "",
            root.GetProperty("serverPfxSha256").GetString() ?? "",
            root.GetProperty("installedByScript").GetBoolean());
    }

    private static DateTimeOffset ReadRequiredUtcTimestamp(
        JsonElement root,
        string propertyName)
    {
        var value = root.GetProperty(propertyName).GetString();
        if (value is null ||
            !DateTimeOffset.TryParseExact(
                value,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var timestamp) ||
            timestamp.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "The trust receipt timestamp is invalid.");
        }
        return timestamp;
    }

    private static DateTimeOffset? ReadOptionalUtcTimestamp(
        JsonElement root,
        string propertyName)
    {
        var property = root.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return ReadRequiredUtcTimestamp(root, propertyName);
    }

    private static void ValidateRootReceipt(
        X509Certificate2 root,
        TrustReceipt receipt,
        string rootCertificatePath,
        string pfxPath)
    {
        var rootHash =
            Convert.ToHexString(SHA256.HashData(root.RawData));
        var rootFileHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(rootCertificatePath)));
        var pfxHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(pfxPath)));
        if (receipt.SchemaVersion != 2 ||
            receipt.State != "Installed" ||
            receipt.StoreLocation != "CurrentUser" ||
            receipt.StoreName != "Root" ||
            receipt.Subject != ExpectedRootSubject ||
            receipt.RootCertificateFile !=
                "reborn-development-root.cer" ||
            receipt.ServerPfxFile !=
                "reborn-development-server.pfx" ||
            !receipt.InstalledByScript ||
            !receipt.Thumbprint.Equals(
                root.Thumbprint,
                StringComparison.OrdinalIgnoreCase) ||
            !receipt.RootSha256.Equals(
                rootHash,
                StringComparison.Ordinal) ||
            receipt.RootCertificateSha256 != rootFileHash ||
            receipt.RootCertificateSha256 != rootHash ||
            receipt.ServerPfxSha256 != pfxHash ||
            !IsUppercaseHex(receipt.Thumbprint, 40) ||
            !IsUppercaseHex(receipt.RootSha256, 64) ||
            !IsUppercaseHex(receipt.RootCertificateSha256, 64) ||
            !IsUppercaseHex(receipt.ServerPfxSha256, 64))
        {
            throw new InvalidDataException(
                "The issued root and guarded trust receipt do not match.");
        }
    }

    private static bool IsUppercaseHex(string value, int length) =>
        value.Length == length &&
        value.All(static character =>
            character is >= '0' and <= '9' or
                >= 'A' and <= 'F');

    private static void ValidateInstalledRoot(
        X509Certificate2 issuedRoot,
        TrustReceipt receipt)
    {
        using var store =
            new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            receipt.Thumbprint,
            validOnly: false);
        try
        {
            if (matches.Count != 1 ||
                !matches[0].RawData.AsSpan().SequenceEqual(
                    issuedRoot.RawData))
            {
                throw new InvalidDataException(
                    "The guarded issued root is not installed exactly.");
            }
        }
        finally
        {
            foreach (var certificate in matches)
            {
                certificate.Dispose();
            }
        }
    }

    private static void AssertBoundedFile(string path, long maximumBytes)
    {
        if (!Path.IsPathFullyQualified(path) ||
            new FileInfo(path).Length is <= 0 ||
            new FileInfo(path).Length > maximumBytes)
        {
            throw new InvalidDataException(
                "A controlled-host certificate input is invalid.");
        }
    }

    private sealed record TrustReceipt(
        int SchemaVersion,
        string State,
        string StoreLocation,
        string StoreName,
        string Subject,
        string Thumbprint,
        string RootSha256,
        string RootCertificateFile,
        string RootCertificateSha256,
        string ServerPfxFile,
        string ServerPfxSha256,
        bool InstalledByScript);
}
