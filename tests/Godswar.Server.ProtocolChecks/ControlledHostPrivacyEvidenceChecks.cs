using System.Text;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class ControlledHostPrivacyEvidenceChecks
{
    internal static Task RunAsync()
    {
        CheckInactiveBehavior();
        CheckDedicatedBoundedEvidence();
        CheckCreateNewAndPathGuards();
        return Task.CompletedTask;
    }

    private static void CheckInactiveBehavior()
    {
        var variable =
            ControlledHostPrivacyEvidence.PathEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            Console.SetOut(output);
            using var session =
                ControlledHostPrivacyEvidence.TryInstallFromEnvironment();
            Check.True(
                session is null,
                "ordinary server mode does not install evidence suppression");
            ControlledHostPrivacyEvidence.RecordIfActive(
                ControlledHostEvidenceEvent.TlsClientAuthenticated);
            Check.Equal(
                string.Empty,
                output.ToString(),
                "inactive controlled-host-only evidence is silent");
            ControlledHostPrivacyEvidence.Record(
                ControlledHostEvidenceEvent
                    .Phase4TlsFallbackObserved);
            Check.True(
                output.ToString().Contains(
                    "[secure-acceptance] one-way TLS fallback observed",
                    StringComparison.Ordinal),
                "ordinary fault diagnostics preserve prior console behavior");
        }
        finally
        {
            Console.SetOut(original);
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static void CheckDedicatedBoundedEvidence()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"reborn-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "evidence.log");
        var variable =
            ControlledHostPrivacyEvidence.PathEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var operatorOutput = new StringWriter();
        using var operatorError = new StringWriter();
        IDisposable? session = null;
        try
        {
            Console.SetOut(operatorOutput);
            Console.SetError(operatorError);
            Environment.SetEnvironmentVariable(variable, path);
            session =
                ControlledHostPrivacyEvidence.TryInstallFromEnvironment();
            Check.True(
                session is not null,
                "controlled-host mode installs the dedicated evidence sink");

            // These inputs include an exact allowlisted line, fragments,
            // attacker-controlled line breaks, ANSI/control bytes, and an
            // oversized unterminated string. None uses the trusted enum API.
            Console.Out.Write("[secure-accept");
            Console.Out.WriteLine(
                "ance] one-way TLS fallback observed");
            Console.Out.WriteLine(
                "character=Alice\r\n" +
                "[controlled-host] TLS client authenticated");
            Console.Error.WriteLine(
                "\u001b[31maccount=7 packet=DEADBEEF\u001b[0m");
            Console.Out.Write(new string('X', 2_000_000));

            ControlledHostPrivacyEvidence.RecordIfActive(
                ControlledHostEvidenceEvent.TlsPolicyAccepted);
            ControlledHostPrivacyEvidence.RecordIfActive(
                ControlledHostEvidenceEvent
                    .AcceptedSecurePrefaceResponseWritten);
            Parallel.For(
                0,
                128,
                _ => ControlledHostPrivacyEvidence.RecordIfActive(
                    ControlledHostEvidenceEvent
                        .TlsClientAuthenticated));
            ControlledHostPrivacyEvidence.RecordIfActive(
                ControlledHostEvidenceEvent.UdpEndpointBound);
            ControlledHostPrivacyEvidence.Record(
                ControlledHostEvidenceEvent
                    .Phase4TlsFallbackObserved);
            ControlledHostPrivacyEvidence.Record(
                ControlledHostEvidenceEvent
                    .Phase4TlsFallbackObserved);
        }
        finally
        {
            session?.Dispose();
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable(variable, previous);
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            Check.True(
                bytes.Length <= 1_536,
                "evidence bytes remain under the fixed sink bound");
            Check.True(
                bytes.Length < 3 ||
                bytes[0] != 0xEF ||
                bytes[1] != 0xBB ||
                bytes[2] != 0xBF,
                "evidence is BOM-free UTF-8");
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var expected = new[]
            {
                "[controlled-host] privacy-safe evidence channel started",
                "[controlled-host] TLS policy accepted",
                "[controlled-host] accepted secure preface response written",
                "[controlled-host] TLS client authenticated",
                "[controlled-host] UDP endpoint authenticated and bound",
                "[secure-acceptance] one-way TLS fallback observed",
                "[controlled-host] secure server stopping"
            };
            Check.True(
                lines.SequenceEqual(expected, StringComparer.Ordinal),
                "only trusted one-shot enum events reach evidence");
            var operatorText = operatorOutput.ToString();
            Check.True(
                expected.All(line =>
                    operatorText.Contains(
                        line,
                        StringComparison.Ordinal)) &&
                !operatorText.Contains(
                    "Alice",
                    StringComparison.Ordinal) &&
                !operatorText.Contains(
                    "DEADBEEF",
                    StringComparison.Ordinal),
                "operator output is also privacy filtered");
            Check.Equal(
                string.Empty,
                operatorError.ToString(),
                "ordinary stderr is discarded without persistence");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckCreateNewAndPathGuards()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"reborn-evidence-guards-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var existing = Path.Combine(root, "existing.log");
        File.WriteAllText(existing, "do not overwrite");
        var variable =
            ControlledHostPrivacyEvidence.PathEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, existing);
            Check.Throws<IOException>(
                () => ControlledHostPrivacyEvidence
                    .TryInstallFromEnvironment(),
                "evidence never overwrites an existing file");
            Check.Equal(
                "do not overwrite",
                File.ReadAllText(existing),
                "existing evidence bytes remain exact");

            Environment.SetEnvironmentVariable(
                variable,
                "relative-evidence.log");
            Check.Throws<InvalidOperationException>(
                () => ControlledHostPrivacyEvidence
                    .TryInstallFromEnvironment(),
                "evidence rejects a relative path");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            Directory.Delete(root, recursive: true);
        }
    }
}
