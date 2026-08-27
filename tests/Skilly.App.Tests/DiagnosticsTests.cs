using System.IO;
using Skilly.Infrastructure;

namespace Skilly.App.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void Process_logs_are_actionable_and_redact_credentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "skilly-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string password = "password-canary-must-not-escape";
            const string token = "gho_123456789012345678901234567890";
            var logDirectory = Path.Combine(root, "logs");
            var runner = new ProcessRunner(new RollingLog(logDirectory));

            var result = runner.Run(
                "cmd.exe",
                ["/d", "/c", "exit", "0", "--password", password, $"--token={token}", $"https://user:{password}@example.test/library?token={token}"]);

            Assert.Equal(0, result.ExitCode);
            var log = string.Join('\n', Directory.EnumerateFiles(logDirectory).Select(ReadShared));
            Assert.Contains("Process start: cmd.exe", log);
            Assert.Contains("Process exit: cmd.exe code=0", log);
            Assert.Contains("<redacted>", log);
            Assert.DoesNotContain(password, log, StringComparison.Ordinal);
            Assert.DoesNotContain(token, log, StringComparison.Ordinal);
        }
        finally
        {
            PackagedAppFixture.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Skills_provider_rejects_embedded_credentials_before_process_execution()
    {
        using var fixture = new SkillsCliProviderFixture();
        const string secret = "source-secret-canary";

        var result = fixture.Provider.Inspect($"https://user:{secret}@example.test/acme/library.git");

        Assert.False(result.Succeeded);
        Assert.Contains("must not embed credentials", result.Diagnostics);
        Assert.False(File.Exists(fixture.InvocationsPath));
        Assert.DoesNotContain(secret, string.Join('\n', Directory.EnumerateFiles(Path.Combine(fixture.Root, "logs")).Select(ReadShared)));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
