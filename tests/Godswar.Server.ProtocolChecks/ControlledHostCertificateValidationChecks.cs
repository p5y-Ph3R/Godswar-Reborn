using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class ControlledHostCertificateValidationChecks
{
    private const X509KeyUsageFlags ExpectedKeyUsage =
        X509KeyUsageFlags.DigitalSignature |
        X509KeyUsageFlags.KeyEncipherment;

    internal static Task RunAsync()
    {
        using (var exact = CreateLeaf(
            ["login.reborn.test", "game.reborn.test"],
            ExpectedKeyUsage,
            ["1.3.6.1.5.5.7.3.1"]))
        {
            ControlledHostValidationCommand
                .ValidateCertificateIdentities(exact);
            ControlledHostValidationCommand.ValidateLeafUsage(exact);
        }

        RejectIdentities(
            ["login.reborn.test.evil", "game.reborn.test"],
            "suffix SAN identity");
        RejectIdentities(
            [
                "login.reborn.test",
                "game.reborn.test",
                "extra.reborn.test"
            ],
            "extra SAN identity");
        RejectIdentities(
            ["login.reborn.test", "login.reborn.test"],
            "duplicate SAN identity");

        using (var uri = CreateLeaf(
            ["login.reborn.test", "game.reborn.test"],
            ExpectedKeyUsage,
            ["1.3.6.1.5.5.7.3.1"],
            includeUri: true))
        {
            Check.Throws<Exception>(
                () => ControlledHostValidationCommand
                    .ValidateCertificateIdentities(uri),
                "non-DNS SAN identity");
        }
        RejectUsage(
            X509KeyUsageFlags.DigitalSignature,
            ["1.3.6.1.5.5.7.3.1"],
            "missing key encipherment");
        RejectUsage(
            ExpectedKeyUsage,
            ["1.3.6.1.5.5.7.3.2"],
            "clientAuth instead of serverAuth");
        RejectUsage(
            ExpectedKeyUsage,
            [
                "1.3.6.1.5.5.7.3.1",
                "1.3.6.1.5.5.7.3.2"
            ],
            "extra enhanced key usage");

        using (var exactRoot = CreateRoot(
            "CN=Reborn Development Root CA",
            3072,
            X509KeyUsageFlags.KeyCertSign |
                X509KeyUsageFlags.CrlSign,
            criticalConstraints: true))
        {
            ControlledHostValidationCommand
                .ValidateIssuedRootProfile(exactRoot);
        }
        RejectRoot(
            "CN=Attacker Development Root CA",
            3072,
            X509KeyUsageFlags.KeyCertSign |
                X509KeyUsageFlags.CrlSign,
            true,
            "arbitrary self-consistent root");
        RejectRoot(
            "CN=Reborn Development Root CA",
            2048,
            X509KeyUsageFlags.KeyCertSign |
                X509KeyUsageFlags.CrlSign,
            true,
            "undersized root key");
        RejectRoot(
            "CN=Reborn Development Root CA",
            3072,
            X509KeyUsageFlags.KeyCertSign,
            true,
            "root missing CRL signing");
        RejectRoot(
            "CN=Reborn Development Root CA",
            3072,
            X509KeyUsageFlags.KeyCertSign |
                X509KeyUsageFlags.CrlSign,
            false,
            "noncritical root constraints");

        using (var rootKey = RSA.Create(3072))
        using (var rootWithPrivateKey =
            CreateRootWithPrivateKey(rootKey))
        using (var publicRoot =
            X509CertificateLoader.LoadCertificate(
                rootWithPrivateKey.RawData))
        using (var exactLeaf = CreateIssuedLeaf(
            rootWithPrivateKey,
            "CN=login.reborn.test",
            2048))
        {
            ControlledHostValidationCommand.ValidateLeafProfile(
                exactLeaf,
                publicRoot);
            using var wrongSubject = CreateIssuedLeaf(
                rootWithPrivateKey,
                "CN=game.reborn.test",
                2048);
            Check.Throws<Exception>(
                () => ControlledHostValidationCommand
                    .ValidateLeafProfile(wrongSubject, publicRoot),
                "unexpected leaf subject");
            using var oversizedKey = CreateIssuedLeaf(
                rootWithPrivateKey,
                "CN=login.reborn.test",
                3072);
            Check.Throws<Exception>(
                () => ControlledHostValidationCommand
                    .ValidateLeafProfile(oversizedKey, publicRoot),
                "unexpected leaf key profile");
        }
        ControlledHostTrustReceiptChecks.Run();
        return Task.CompletedTask;
    }

    private static void RejectIdentities(
        string[] dnsNames,
        string description)
    {
        using var certificate = CreateLeaf(
            dnsNames,
            ExpectedKeyUsage,
            ["1.3.6.1.5.5.7.3.1"]);
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateCertificateIdentities(certificate),
            description);
    }

    private static void RejectUsage(
        X509KeyUsageFlags keyUsage,
        string[] enhancedKeyUsage,
        string description)
    {
        using var certificate = CreateLeaf(
            ["login.reborn.test", "game.reborn.test"],
            keyUsage,
            enhancedKeyUsage);
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateLeafUsage(certificate),
            description);
    }

    private static X509Certificate2 CreateLeaf(
        string[] dnsNames,
        X509KeyUsageFlags keyUsage,
        string[] enhancedKeyUsage,
        bool includeUri = false)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=login.reborn.test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(keyUsage, critical: true));
        var usages = new OidCollection();
        foreach (var oid in enhancedKeyUsage)
        {
            usages.Add(new Oid(oid));
        }
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                usages,
                critical: true));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dnsName in dnsNames)
        {
            san.AddDnsName(dnsName);
        }
        if (includeUri)
        {
            san.AddUri(new Uri("https://login.reborn.test/"));
        }
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
    }

    private static void RejectRoot(
        string subject,
        int keySize,
        X509KeyUsageFlags keyUsage,
        bool criticalConstraints,
        string description)
    {
        using var certificate = CreateRoot(
            subject,
            keySize,
            keyUsage,
            criticalConstraints);
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateIssuedRootProfile(certificate),
            description);
    }

    private static X509Certificate2 CreateRoot(
        string subject,
        int keySize,
        X509KeyUsageFlags keyUsage,
        bool criticalConstraints)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: criticalConstraints));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(keyUsage, critical: true));
        using var withPrivateKey = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadCertificate(
            withPrivateKey.RawData);
    }

    private static X509Certificate2 CreateRootWithPrivateKey(
        RSA rsa)
    {
        var request = new CertificateRequest(
            "CN=Reborn Development Root CA",
            rsa,
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

    private static X509Certificate2 CreateIssuedLeaf(
        X509Certificate2 issuer,
        string subject,
        int keySize)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest(
            subject,
            rsa,
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
                ExpectedKeyUsage,
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
        var serial = RandomNumberGenerator.GetBytes(16);
        using var publicLeaf = request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1),
            serial);
        return publicLeaf.CopyWithPrivateKey(rsa);
    }

}
