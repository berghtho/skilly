using System.Diagnostics;
using System.IO;

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

        using var first = SkillyInstance.Start(Fixture.ExePath, profile, workingDir);
        first.WaitForReadyStateFile(TimeSpan.FromMinutes(1));

        using var second = SkillyInstance.Start(Fixture.ExePath, profile, workingDir);
        Assert.True(
            second.WaitForExit(TimeSpan.FromSeconds(20)),
            "Second launch did not exit; two instances are running concurrently.");
        Assert.Equal(0, second.Process.ExitCode);
        Assert.False(first.Process.HasExited, "First instance terminated when a second launch occurred.");

        first.CloseMainWindowAndWait();
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
