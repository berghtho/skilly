using System.IO;

namespace Skilly.App.Tests;

[Collection(PackagedAppCollection.Name)]
public sealed class LaunchAndStorageTests(PackagedAppFixture fixture)
{
    public PackagedAppFixture Fixture { get; } = fixture;

    [Fact]
    public void Launch_creates_state_and_logs_under_isolated_LocalAppData()
    {
        using var profile = new IsolatedProfile();
        var workingDir = Path.Combine(profile.Root, "unrelated-cwd");
        Directory.CreateDirectory(workingDir);

        using var instance = SkillyInstance.Start(Fixture.ExePath, profile, workingDir);
        instance.WaitForMainWindow(TimeSpan.FromMinutes(2));
        instance.CloseMainWindowAndWait();

        Assert.True(Directory.Exists(profile.SkillyRoot), "Skilly root was not created under the isolated LocalAppData root.");
        Assert.True(Directory.Exists(profile.LogsDirectory), "Logs directory was not created under the isolated LocalAppData root.");
        Assert.NotEmpty(Directory.GetFiles(profile.LogsDirectory, "skilly-*.log"));
        Assert.True(profile.StateFileExists(), "state.json was not created under the isolated LocalAppData root.");

        Assert.False(File.Exists(Path.Combine(workingDir, "state.json")), "State leaked into the working directory.");
        Assert.Empty(Directory.GetFiles(workingDir));
        Assert.False(File.Exists(Path.Combine(Fixture.PublishDirectory, "state.json")), "State leaked next to the executable.");
        Assert.False(Directory.Exists(Path.Combine(Fixture.PublishDirectory, "logs")), "Logs leaked next to the executable.");
    }

}
