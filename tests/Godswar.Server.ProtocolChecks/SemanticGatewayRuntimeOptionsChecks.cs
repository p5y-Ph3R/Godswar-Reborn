using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking.Backhaul;
using Godswar.Server.Networking.SemanticGateway;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulProtocolChecks
{
    public const string RuntimeOptionsCheckName =
        "B18C2 semantic gateway runtime options fail closed";

    public static async Task RunRuntimeOptionsAsync()
    {
        var directory = Directory.CreateTempSubdirectory(
            "reborn-b18c2-options-");
        const string passwordVariable =
            "GODSWAR_B18C2_OPTIONS_TEST_PASSWORD";
        const string password = "runtime-options-test-only";
        var previousPassword =
            Environment.GetEnvironmentVariable(passwordVariable);
        Environment.SetEnvironmentVariable(
            passwordVariable,
            password);
        try
        {
            var gatewayPath = Path.Combine(
                directory.FullName,
                "gateway-client.pfx");
            var wrongEkuPath = Path.Combine(
                directory.FullName,
                "wrong-server.pfx");
            await WriteCertificateAsync(
                gatewayPath,
                password,
                ClientAuthenticationOid);
            await WriteCertificateAsync(
                wrongEkuPath,
                password,
                ServerAuthenticationOid);
            var valid = ValidOptionsNode(
                Path.GetFileName(gatewayPath),
                passwordVariable);
            var validPath = await WriteOptionsAsync(
                directory.FullName,
                "valid.json",
                valid);
            using (var loaded =
                   await SemanticGatewayRuntimeOptions.LoadAsync(
                       validPath))
            {
                Check.True(
                    loaded.TryResolveMap(
                        new Godswar.Server.Domain.World.Instances.MapId(4),
                        out var route) &&
                    route == loaded.BootstrapTarget,
                    "valid generated certificate and strict config load");
            }

            var unknown = valid.DeepClone().AsObject();
            unknown["UnknownStartupInput"] = true;
            await ExpectInvalidAsync(
                directory.FullName,
                "unknown.json",
                unknown,
                "unknown JSON member");

            var nonLoopbackBind = valid.DeepClone().AsObject();
            nonLoopbackBind["Login"]!["BindHost"] =
                "192.0.2.10";
            await ExpectInvalidAsync(
                directory.FullName,
                "non-loopback-bind.json",
                nonLoopbackBind,
                "non-loopback bind");

            var publicRedirect = valid.DeepClone().AsObject();
            publicRedirect["Game"]!["PublicHost"] =
                "192.0.2.11";
            await ExpectInvalidAsync(
                directory.FullName,
                "public-redirect.json",
                publicRedirect,
                "non-loopback public redirect");

            var duplicateMap = valid.DeepClone().AsObject();
            var mapRoute = duplicateMap["Routes"]!
                .AsArray()[0]!.DeepClone();
            mapRoute!["WorldInstanceId"] =
                "22222222-2222-4222-8222-222222222222";
            mapRoute["Bootstrap"] = false;
            duplicateMap["Routes"]!.AsArray().Add(mapRoute);
            await ExpectInvalidAsync(
                directory.FullName,
                "duplicate-map.json",
                duplicateMap,
                "duplicate map route");

            var duplicateWorld = valid.DeepClone().AsObject();
            var worldRoute = duplicateWorld["Routes"]!
                .AsArray()[0]!.DeepClone();
            worldRoute!["MapId"] = 5;
            worldRoute["Bootstrap"] = false;
            duplicateWorld["Routes"]!.AsArray().Add(worldRoute);
            await ExpectInvalidAsync(
                directory.FullName,
                "duplicate-world.json",
                duplicateWorld,
                "duplicate world-instance route");

            var noBootstrap = valid.DeepClone().AsObject();
            noBootstrap["Routes"]!.AsArray()[0]!["Bootstrap"] =
                false;
            await ExpectInvalidAsync(
                directory.FullName,
                "no-bootstrap.json",
                noBootstrap,
                "missing bootstrap route");

            var duplicateEndpoint = valid.DeepClone().AsObject();
            var endpointWorker = duplicateEndpoint["Workers"]!
                .AsArray()[0]!.DeepClone();
            endpointWorker!["ServerNodeId"] = "worker-b";
            duplicateEndpoint["Workers"]!.AsArray().Add(
                endpointWorker);
            await ExpectInvalidAsync(
                directory.FullName,
                "duplicate-endpoint.json",
                duplicateEndpoint,
                "duplicate worker endpoint");

            var duplicateNode = valid.DeepClone().AsObject();
            var nodeWorker = duplicateNode["Workers"]!
                .AsArray()[0]!.DeepClone();
            nodeWorker!["BackhaulPort"] = 32002;
            duplicateNode["Workers"]!.AsArray().Add(nodeWorker);
            await ExpectInvalidAsync(
                directory.FullName,
                "duplicate-node.json",
                duplicateNode,
                "duplicate worker node");

            var badPin = valid.DeepClone().AsObject();
            badPin["Workers"]!.AsArray()[0]!
                ["AllowedWorkerCertificateSha256"]!
                .AsArray()[0] = "not-a-sha256-pin";
            await ExpectInvalidAsync(
                directory.FullName,
                "bad-pin.json",
                badPin,
                "malformed worker certificate pin");

            var wrongEku = valid.DeepClone().AsObject();
            wrongEku["GatewayCertificate"]!["Path"] =
                Path.GetFileName(wrongEkuPath);
            await ExpectInvalidAsync(
                directory.FullName,
                "wrong-eku.json",
                wrongEku,
                "gateway certificate with server-only EKU");

            var oversizedPath = Path.Combine(
                directory.FullName,
                "oversized.json");
            await File.WriteAllBytesAsync(
                oversizedPath,
                new byte[
                    SemanticGatewayRuntimeOptions
                        .MaximumConfigurationBytes + 1]);
            await ExpectInvalidPathAsync(
                oversizedPath,
                "oversized configuration");

            await VerifyWorkerRouteOwnershipFailsClosedAsync(
                wrongEkuPath,
                passwordVariable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                passwordVariable,
                previousPassword);
            directory.Delete(recursive: true);
        }
    }

    private static async Task VerifyWorkerRouteOwnershipFailsClosedAsync(
        string certificatePath,
        string passwordVariable)
    {
        var ownedInstanceId =
            "33333333-3333-4333-8333-333333333333";
        var worldInstances = new WorldInstanceRuntimeOptions
        {
            ServerNodeId = "worker-a",
            MaximumRuntimes = 8,
            MaximumPlayerAssignments = 16,
            MaximumRetiredInstanceIds = 64,
            DefaultOpenWorldPlayerCapacity = 8,
            MailboxCapacity = 16,
            OwnerInvocationTimeoutMilliseconds = 2_000,
            ShutdownDrainTimeoutMilliseconds = 2_000,
            MaximumFanoutConcurrency = 2,
            StaticOpenWorldInstances =
            [
                new StaticOpenWorldInstanceOptions
                {
                    RealmId = 1,
                    MapId = 4,
                    WorldInstanceId = ownedInstanceId
                }
            ]
        };
        var worker = new BackhaulWorkerRuntimeOptions
        {
            Enabled = true,
            AdmissionCapacity = 9,
            ReplayCapacity = 17,
            CertificatePath = certificatePath,
            CertificatePasswordEnvironmentVariable = passwordVariable,
            AllowedGatewayCertificateSha256 =
            [
                Convert.ToHexString(
                    SHA256.HashData("gateway-pin"u8))
            ]
        };
        worker.NormalizeAndValidate(
            certificatePath,
            worldInstances,
            new SecureNetworkOptions());
        Check.True(
            worldInstances.RequireStaticOpenWorldOwnership,
            "worker startup requires exact static open-world ownership");
        using (var admissions =
               worker.BuildAdmissionRegistry(worldInstances))
        {
            var snapshot = admissions.GetSnapshot();
            Check.True(
                snapshot.Capacity == 9 &&
                snapshot.ReplayCapacity == 17,
                "worker config keeps live and replay budgets separate");
        }

        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = new GameSessionRegistry(
            worldInstanceOptions: worldInstances);
        var character = new GameCharacter
        {
            Id = 9_001,
            AccountId = 901,
            Name = "UnownedRouteHero",
            CurrentMap = 5
        };
        Check.Throws<InvalidOperationException>(
            () => registry.JoinMap(
                socket.Session,
                character.AccountId,
                character,
                0x7A01),
            "worker refuses an unassigned open-world map");
    }

    private static JsonObject ValidOptionsNode(
        string certificatePath,
        string passwordVariable)
    {
        var workerPin = Convert.ToHexString(
            SHA256.HashData(
                "integration-worker-pin"u8));
        return JsonSerializer.SerializeToNode(
            new
            {
                Login = new
                {
                    BindHost = "127.0.0.1",
                    BindPort = 31001
                },
                Game = new
                {
                    BindHost = "127.0.0.1",
                    BindPort = 31002,
                    PublicHost = "127.0.0.1",
                    PublicPort = 31002
                },
                GatewayCertificate = new
                {
                    Path = certificatePath,
                    PasswordEnvironmentVariable = passwordVariable
                },
                Limits = new { },
                Authority = new { },
                Workers = new[]
                {
                    new
                    {
                        ServerNodeId = "worker-a",
                        BackhaulHost = "127.0.0.1",
                        BackhaulPort = 32001,
                        TlsHost = "worker-a.internal",
                        AllowedWorkerCertificateSha256 =
                            new[] { workerPin },
                        AdmissionCapacity = 8,
                        InitialState = "available"
                    }
                },
                Routes = new[]
                {
                    new
                    {
                        RealmId = 1,
                        MapId = 4,
                        WorldInstanceId =
                            "11111111-1111-4111-8111-111111111111",
                        ServerNodeId = "worker-a",
                        AdmissionCapacity = 8,
                        Bootstrap = true
                    }
                }
            })!.AsObject();
    }

    private static async Task<string> WriteOptionsAsync(
        string directory,
        string name,
        JsonObject options)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(
            path,
            options.ToJsonString());
        return path;
    }

    private static async Task ExpectInvalidAsync(
        string directory,
        string name,
        JsonObject options,
        string description)
    {
        var path = await WriteOptionsAsync(
            directory,
            name,
            options);
        await ExpectInvalidPathAsync(path, description);
    }

    private static async Task ExpectInvalidPathAsync(
        string path,
        string description)
    {
        try
        {
            using var unexpected =
                await SemanticGatewayRuntimeOptions.LoadAsync(path);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description} was accepted.");
    }

    private static async Task WriteCertificateAsync(
        string path,
        string password,
        string enhancedKeyUsageOid)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=runtime-options-test",
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
                X509KeyUsageFlags.DigitalSignature,
                critical: true));
        var usages = new OidCollection
        {
            new Oid(enhancedKeyUsageOid)
        };
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                usages,
                critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
        var pkcs12 = certificate.Export(
            X509ContentType.Pkcs12,
            password);
        try
        {
            await File.WriteAllBytesAsync(path, pkcs12);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }
}
