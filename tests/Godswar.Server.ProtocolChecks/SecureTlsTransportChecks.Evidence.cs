using System.Net.Security;
using System.Text;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static async Task
        CheckControlledHostAcceptanceEvidenceAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"reborn-tls-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "evidence.log");
            var variable =
                ControlledHostPrivacyEvidence.PathEnvironmentVariable;
            var previous =
                Environment.GetEnvironmentVariable(variable);
            var originalOutput = Console.Out;
            var originalError = Console.Error;
            using var operatorOutput = new StringWriter();
            using var operatorError = new StringWriter();
            IDisposable? evidenceSession = null;
            try
            {
                Console.SetOut(operatorOutput);
                Console.SetError(operatorError);
                Environment.SetEnvironmentVariable(variable, path);
                evidenceSession =
                    ControlledHostPrivacyEvidence
                        .TryInstallFromEnvironment();
                Check.True(
                    evidenceSession is not null,
                    "TLS acceptance fixture installs evidence");

                using var certificate =
                    SecureTlsTestCertificate.Create();
                using var gate = new TlsHandshakeGate(1);
                var factory = CreateFactory(
                    certificate,
                    CreateRuntimeOptions(),
                    gate);
                var tlsLine = ControlledHostPrivacyEvidence.GetLine(
                    ControlledHostEvidenceEvent.TlsPolicyAccepted);
                var prefaceLine =
                    ControlledHostPrivacyEvidence.GetLine(
                        ControlledHostEvidenceEvent
                            .AcceptedSecurePrefaceResponseWritten);

                await using (var rejectedPair = await StartPairAsync(
                    factory,
                    NetworkEndpointRole.Login))
                {
                    await rejectedPair.ClientStream
                        .AuthenticateAsClientAsync(
                            certificate.CreateClientOptions());
                    await WaitForActiveEvidenceAsync(path, tlsLine);
                    Check.True(
                        !ReadActiveEvidence(path).Contains(
                            prefaceLine,
                            StringComparer.Ordinal),
                        "TLS acceptance precedes secure preface evidence");

                    var unsupportedBuild = Enumerable.Repeat(
                            (byte)0xA5,
                            SecureProtocolConstants.BuildHashBytes)
                        .ToArray();
                    await rejectedPair.ClientStream.WriteAsync(
                        EncodeClientPreface(
                            SecureEndpointRole.Login,
                            unsupportedBuild));
                    var rejectedResponse = await ReadExactlyAsync(
                        rejectedPair.ClientStream,
                        SecureProtocolConstants.ServerPrefaceBytes);
                    Check.True(
                        SecurePrefaceCodec.TryDecodeServer(
                            rejectedResponse,
                            SecureEndpointRole.Login,
                            out var rejectedPreface) &&
                        rejectedPreface!.Status ==
                            SecureServerPrefaceStatus.UnsupportedBuild,
                        "rejected evidence fixture receives canonical response");
                    await ExpectExceptionAsync<SecureTransportException>(
                        async () => await rejectedPair.TransportTask,
                        "rejected preface does not create a transport");
                    Check.True(
                        !ReadActiveEvidence(path).Contains(
                            prefaceLine,
                            StringComparer.Ordinal),
                        "rejected canonical response is not acceptance evidence");
                }

                await using (var acceptedPair = await StartPairAsync(
                    factory,
                    NetworkEndpointRole.Login))
                {
                    var preface = await AuthenticateAndPrefaceAsync(
                        acceptedPair.ClientStream,
                        certificate,
                        SecureEndpointRole.Login);
                    Check.Equal(
                        (int)SecureServerPrefaceStatus.Ok,
                        (int)preface.Status,
                        "evidence fixture accepts canonical preface");
                    _ = await acceptedPair.TransportTask;
                }
                await WaitForActiveEvidenceAsync(path, prefaceLine);
            }
            finally
            {
                evidenceSession?.Dispose();
                Console.SetOut(originalOutput);
                Console.SetError(originalError);
                Environment.SetEnvironmentVariable(variable, previous);
            }

            var expected = new[]
            {
                "[controlled-host] privacy-safe evidence channel started",
                "[controlled-host] TLS policy accepted",
                "[controlled-host] accepted secure preface response written",
                "[controlled-host] secure server stopping"
            };
            Check.True(
                File.ReadAllLines(path, Encoding.UTF8)
                    .SequenceEqual(expected, StringComparer.Ordinal),
                "TLS acceptance evidence is exact, ordered, and one-shot");
            Check.Equal(
                string.Empty,
                operatorError.ToString(),
                "TLS acceptance evidence writes no ordinary stderr");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForActiveEvidenceAsync(
        string path,
        string expectedLine)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (ReadActiveEvidence(path).Contains(
                    expectedLine,
                    StringComparer.Ordinal))
            {
                return;
            }
            await Task.Delay(10);
        }

        throw new InvalidOperationException(
            $"Assertion failed: fixed evidence was not recorded: {expectedLine}.");
    }

    private static string[] ReadActiveEvidence(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true));
        return reader.ReadToEnd()
            .Split(
                ["\r\n", "\n", "\r"],
                StringSplitOptions.RemoveEmptyEntries);
    }
}
