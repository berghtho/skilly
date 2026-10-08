using System.Text;
using Skilly.Infrastructure;

namespace Skilly.App.Tests;

[CollectionDefinition("Process output encoding", DisableParallelization = true)]
public sealed class ProcessOutputEncodingCollection;

[Collection("Process output encoding")]
public sealed class ProcessRunnerTests
{
    [Fact]
    public void Cancelling_a_running_check_stops_its_child_process()
    {
        using var fixture = new SkillsCliProviderFixture();
        var marker = System.IO.Path.Combine(fixture.Root, "child-started.txt");
        var fake = FakeExecutable();
        using var cancellation = new CancellationTokenSource();
        var task = Task.Run(() => new ProcessRunner(fixture.Log).Run(fake, ["--delay-probe", marker],
            cancellationToken: cancellation.Token));
        Assert.True(SpinWait.SpinUntil(() => System.IO.File.Exists(marker) && new System.IO.FileInfo(marker).Length > 0, TimeSpan.FromSeconds(5)));
        var pid = int.Parse(System.IO.File.ReadAllText(marker));
        cancellation.Cancel();
        Assert.True(SpinWait.SpinUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)));
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        try
        {
            using var child = System.Diagnostics.Process.GetProcessById(pid);
            Assert.True(child.HasExited || child.WaitForExit(1000));
        }
        catch (ArgumentException) { /* Process already exited. */ }
    }

    [Fact]
    public void Utf8_child_output_is_preserved_under_a_legacy_Windows_console_encoding()
    {
        var previous = Console.OutputEncoding;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            Console.OutputEncoding = Encoding.GetEncoding(437);
            using var fixture = new SkillsCliProviderFixture();
            var result = new ProcessRunner(fixture.Log).Run(FakeExecutable(), ["--utf8-probe"]);
            Assert.True(result.Succeeded);
            Assert.Equal("│    alpha\n", result.StandardOutput);
            Assert.Equal("✓", result.StandardError);
        }
        finally { Console.OutputEncoding = previous; }
    }

    private static string FakeExecutable() => System.IO.Path.Combine(PackagedAppFixture.FindRepoRoot(), "tests", "FakeSkills", "bin",
        new System.IO.DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0-windows", "FakeSkills.exe");
}
