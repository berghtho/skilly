using System.Diagnostics;
using System.IO;
using Xunit;

namespace Skilly.App.Tests;

[CollectionDefinition(Name)]
public sealed class PackagedAppCollection : ICollectionFixture<PackagedAppFixture>
{
    public const string Name = "packaged-app";
}

public sealed class PackagedAppFixture : IAsyncLifetime
{
    private const int PublishTimeoutMinutes = 8;

    public string PublishDirectory { get; private set; } = string.Empty;

    public string ExePath => Path.Combine(PublishDirectory, "Skilly.exe");

    public Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        PublishDirectory = Path.Combine(
            Path.GetTempPath(),
            "skilly-packaged-tests-" + Guid.NewGuid().ToString("N")[..10]);

        if (Directory.Exists(PublishDirectory))
        {
            Directory.Delete(PublishDirectory, recursive: true);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            Arguments =
                $"publish src{Path.DirectorySeparatorChar}Skilly -c Release -r win-x64 --self-contained true -o \"{PublishDirectory}\"",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet publish.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(PublishTimeoutMinutes).TotalMilliseconds))
        {
            throw new TimeoutException("dotnet publish did not finish in time.");
        }

        var output = stdoutTask.Result + Environment.NewLine + stderrTask.Result;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish failed with exit code {process.ExitCode}.{Environment.NewLine}{output}");
        }

        if (!File.Exists(ExePath))
        {
            var listing = Directory.Exists(PublishDirectory)
                ? string.Join(", ", Directory.GetFiles(PublishDirectory).Select(Path.GetFileName))
                : "(directory missing)";
            throw new FileNotFoundException(
                $"Expected published executable was not found. Exit code was {process.ExitCode}. Output dir: [{listing}]. Publish output:{Environment.NewLine}{output}",
                ExePath);
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        TryDeleteDirectory(PublishDirectory);
        return Task.CompletedTask;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
               && !File.Exists(Path.Combine(current.FullName, "Skilly.sln"))
               && !File.Exists(Path.Combine(current.FullName, "Skilly.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root containing the Skilly solution.");
    }

    internal static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500 * (attempt + 1));
            }
        }
    }
}
