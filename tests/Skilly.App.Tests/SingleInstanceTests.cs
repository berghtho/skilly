using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Skilly.App.Tests;

[Collection(PackagedAppCollection.Name)]
public sealed class SingleInstanceTests(PackagedAppFixture fixture)
{
    public PackagedAppFixture Fixture { get; } = fixture;

    [Fact]
    public void Second_launch_exits_and_first_instance_keeps_running()
    {
        using var profile = new IsolatedProfile();
        var workingDir = Path.Combine(profile.Root, "cwd");
        Directory.CreateDirectory(workingDir);
        var hostTrace = Path.Combine(profile.Root, "host-trace.txt");

        using var first = SkillyInstance.Start(Fixture.ExePath, profile, workingDir, new Dictionary<string, string?>
        {
            ["COREHOST_TRACE"] = "1",
            ["COREHOST_TRACEFILE"] = hostTrace,
            ["COREHOST_TRACE_VERBOSITY"] = "4",
        });
        first.WaitForReadyStateFile(TimeSpan.FromMinutes(1));

        Assert.True(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000), "The clean-profile gate requires Windows 11 or newer.");
        Assert.Equal(Architecture.X64, RuntimeInformation.OSArchitecture);

        using var second = SkillyInstance.Start(Fixture.ExePath, profile, workingDir);
        Assert.True(
            second.WaitForExit(TimeSpan.FromSeconds(20)),
            "Second launch did not exit; two instances are running concurrently.");
        Assert.Equal(0, second.Process.ExitCode);
        Assert.False(first.Process.HasExited, "First instance terminated when a second launch occurred.");

        first.CloseMainWindowAndWait();
        var trace = File.ReadAllText(hostTrace);
        Assert.Contains("Detected Single-File app bundle", trace, StringComparison.Ordinal);
        Assert.Contains("Using internal fxr", trace, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"), trace, StringComparison.OrdinalIgnoreCase);
        LiveGateEvidence.Write("clean-windows-profile", new
        {
            windowsVersion = Environment.OSVersion.Version.ToString(),
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            runtime = "Skilly self-contained single-file bundle (internal hostfxr)",
            systemRuntimeLookupDisabled = true,
            isolatedUserProfile = true,
            secondLaunchExitCode = second.Process.ExitCode,
            cleanShutdownExitCode = first.Process.ExitCode,
        });
    }

    [Fact]
    public void Closing_idle_window_terminates_process_cleanly()
    {
        using var profile = new IsolatedProfile();
        var workingDir = Path.Combine(profile.Root, "cwd");
        Directory.CreateDirectory(workingDir);

        using var instance = SkillyInstance.Start(Fixture.ExePath, profile, workingDir);
        instance.WaitForMainWindow(TimeSpan.FromMinutes(2));

        instance.CloseMainWindowAndWait();

        Assert.True(instance.Process.HasExited);
        Assert.Equal(0, instance.Process.ExitCode);
    }
}
