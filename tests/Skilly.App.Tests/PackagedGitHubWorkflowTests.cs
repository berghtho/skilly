using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using Skilly.Infrastructure;
using Skilly.Providers;
using Skilly.Providers.Apm;
using Skilly.Providers.SkillsCli;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.App.Tests;

[Collection(PackagedAppCollection.Name)]
public sealed class PackagedGitHubWorkflowTests(PackagedAppFixture fixture)
{
    public PackagedAppFixture Fixture { get; } = fixture;

    [Fact]
    public void Workbench_installs_selected_GitHub_Skill_and_exposes_observable_postconditions()
    {
        using var source = new GitHubProviderFixture();
        using var profile = new IsolatedProfile();
        var workingDirectory = Path.Combine(profile.Root, "unchanged-project");
        var tools = Path.Combine(profile.Root, "tools");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(tools);
        CopyFakeGh(tools);
        var invocations = Path.Combine(profile.Root, "gh-invocations.jsonl");
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["FAKE_GH_FIXTURE_ROOT"] = source.FixtureRoot,
            ["FAKE_GH_STATE_PATH"] = profile.StateFilePath,
            ["FAKE_GH_INVOCATIONS"] = invocations,
        };

        using var app = SkillyInstance.Start(Fixture.ExePath, profile, workingDirectory, environment);
        var main = AutomationElement.FromHandle(app.WaitForMainWindow(TimeSpan.FromMinutes(2)));
        try
        {
            var status = Find(main, "Skilly.StatusMessage");
            WaitUntil(
                () => status.Current.Name.Contains("Checked 0 managed Skill(s)", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                "Launch update Check did not finish before source inspection.");
            SetValue(Find(main, "Skilly.SourceReference"), source.Reference.Original);
            var inspect = Find(main, "Skilly.InspectSource");
            var invoke = Task.Run(() => ((InvokePattern)inspect.GetCurrentPattern(InvokePattern.Pattern)).Invoke());

            var inspector = WaitForWindow(
                app.Process.Id,
                ["Source inspector", "Inspect Skill Library"],
                TimeSpan.FromSeconds(30),
                () => $"Invoke completed={invoke.IsCompleted}. " + status.Current.Name + " Windows: " + string.Join(", ", AutomationElement.RootElement
                    .FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Process.Id))
                    .Cast<AutomationElement>().Select(element => $"'{element.Current.Name}'")) + (File.Exists(invocations)
                    ? " Fake gh invocations: " + File.ReadAllText(invocations)
                    : " Fake gh was not invoked.") + " Logs: " + ReadLogs(profile.LogsDirectory));
            Assert.False(invoke.IsFaulted, invoke.Exception?.ToString());
            SetValue(Find(inspector, "Skilly.ExactSelection"), "Alpha Display");
            ((InvokePattern)Find(inspector, "Skilly.SelectExact").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var install = Find(inspector, "Skilly.InstallSelected");
            WaitUntil(() => install.Current.IsEnabled, TimeSpan.FromSeconds(10), "Install selected did not become available.");
            ((InvokePattern)install.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var sourceStatus = Find(inspector, "Skilly.SourceStatus");
            WaitUntil(
                () => !IsWindowAvailable(inspector)
                      || sourceStatus.Current.Name.Contains("Installation failed", StringComparison.Ordinal),
                TimeSpan.FromMinutes(2),
                "Source inspector did not finish installation.",
                () => IsWindowAvailable(inspector)
                    ? sourceStatus.Current.Name + "\n" + ReadLogs(profile.LogsDirectory)
                    : "Inspector closed.");
            if (IsWindowAvailable(inspector))
            {
                Assert.Fail($"Source inspector retained a failed installation: {sourceStatus.Current.Name}\n{ReadLogs(profile.LogsDirectory)}");
            }

            var canonical = Path.Combine(profile.Home, ".agents", "skills", "alpha");
            var claude = Path.Combine(profile.Home, ".claude", "skills", "alpha");
            Assert.True(File.Exists(Path.Combine(canonical, "scripts", "run.ps1")));
            Assert.True(Junction.IsJunctionTo(claude, canonical));
            var state = JsonSerializer.Deserialize<SkillyState>(File.ReadAllText(profile.StateFilePath), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            })!;
            var record = Assert.Single(state.Records);
            Assert.Equal("github", record.Provenance.SourceProvider);
            Assert.Equal(GitHubProviderFixture.CommitSha, record.InstalledRevision);
            Assert.Equal(PayloadHasher.HashFolder(canonical), record.InstalledPayloadHash);
            Assert.Null(state.PendingOperation);
            Assert.False(Directory.Exists(Path.Combine(profile.SkillyRoot, "recovery")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(workingDirectory));

            var skillList = Find(main, "Skilly.SkillList");
            WaitUntil(
                () => skillList.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                    .Any(element => string.Equals(element.Current.Name, "alpha", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(20),
                "Installed Skill did not appear in the Workbench inventory.");
            Assert.Contains("Installed 1 Skill(s)", status.Current.Name);
        }
        finally
        {
            app.CloseMainWindowAndWait();
        }
    }

    [Fact]
    public async Task Workbench_Managed_Reinstall_confirmation_shows_exact_path_and_revision_and_cancel_preserves_local_content()
    {
        using var source = new GitHubProviderFixture();
        var inspection = source.Provider.Inspect(source.Reference).ValueOrThrow();
        source.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(source.StateStore.Load().Records);
        var localOnly = Path.Combine(record.CanonicalPath, "local-only.txt");
        File.WriteAllText(localOnly, "confirmed content must remain after cancel");

        using var profile = new IsolatedProfile();
        Directory.CreateDirectory(Path.GetDirectoryName(profile.StateFilePath)!);
        File.Copy(source.StatePath, profile.StateFilePath);
        var workingDirectory = Path.Combine(profile.Root, "unchanged-project");
        var tools = Path.Combine(profile.Root, "tools");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(tools);
        CopyFakeGh(tools);
        var environment = new Dictionary<string, string?>
        {
            ["USERPROFILE"] = source.Home,
            ["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["FAKE_GH_FIXTURE_ROOT"] = source.FixtureRoot,
            ["FAKE_GH_STATE_PATH"] = profile.StateFilePath,
            ["FAKE_GH_INVOCATIONS"] = Path.Combine(profile.Root, "gh-invocations.jsonl"),
        };

        using var app = SkillyInstance.Start(Fixture.ExePath, profile, workingDirectory, environment);
        var main = AutomationElement.FromHandle(app.WaitForMainWindow(TimeSpan.FromMinutes(2)));
        try
        {
            var status = Find(main, "Skilly.StatusMessage");
            WaitUntil(
                () => status.Current.Name.Contains("Checked 1 managed Skill(s)", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                "Launch update Check did not finish before Managed Reinstall confirmation.");
            var list = Find(main, "Skilly.SkillList");
            AutomationElement? alpha = null;
            WaitUntil(() =>
            {
                alpha = list.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                    .FirstOrDefault(element => element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)
                                               && element.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                                                   .Any(child => string.Equals(child.Current.Name, "alpha", StringComparison.Ordinal)));
                return alpha is not null;
            }, TimeSpan.FromSeconds(30), "Locally modified managed Skill did not appear in inventory.",
                () => string.Join(", ", list.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                    .Select(static element => $"{element.Current.ControlType.ProgrammaticName}='{element.Current.Name}'")));
            ((SelectionItemPattern)alpha!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var reinstall = Find(main, "Skilly.ManagedReinstall");
            WaitUntil(() => reinstall.Current.IsEnabled, TimeSpan.FromSeconds(10), "Managed Reinstall did not become available.");
            var invoke = Task.Run(() => ((InvokePattern)reinstall.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
            var confirmation = WaitForWindow(
                app.Process.Id,
                ["Confirm Managed Reinstall"],
                TimeSpan.FromSeconds(30),
                () => $"Invoke completed={invoke.IsCompleted}; fault={invoke.Exception}; status={status.Current.Name}; logs={ReadLogs(profile.LogsDirectory)}");
            var confirmationText = string.Join("\n", confirmation.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>().Select(static element => element.Current.Name));

            Assert.Contains(record.CanonicalPath, confirmationText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(record.InstalledRevision, confirmationText, StringComparison.Ordinal);
            var cancel = confirmation.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().Single(element => string.Equals(element.Current.Name, "Cancel", StringComparison.Ordinal));
            ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            await invoke.WaitAsync(TimeSpan.FromSeconds(10));
            WaitUntil(() => status.Current.Name.Contains("cancelled", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10), "Cancellation was not announced in the persistent status area.");
            Assert.Equal("confirmed content must remain after cancel", File.ReadAllText(localOnly));
            Assert.Empty(Directory.EnumerateFileSystemEntries(workingDirectory));
        }
        finally
        {
            app.CloseMainWindowAndWait();
        }
    }

    [Fact]
    public async Task Workbench_routes_skills_provider_Managed_Reinstall_to_exact_confirmation_without_mutating_on_cancel()
    {
        using var source = new SkillsCliProviderFixture();
        var inspection = source.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        source.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(source.StateStore.Load().Records);
        var localOnly = Path.Combine(record.CanonicalPath, "local-only.txt");
        File.WriteAllText(localOnly, "skills content preserved after cancel");
        var plan = source.Provider.PlanManagedReinstall(record).ValueOrThrow();
        using var profile = PreparePackagedState(source.StatePath);
        var tools = Path.Combine(profile.Root, "tools");
        Directory.CreateDirectory(tools);
        PrepareFakeSkillsTools(tools);
        var environment = new Dictionary<string, string?>
        {
            ["USERPROFILE"] = source.Home,
            ["HOME"] = source.Home,
            ["XDG_STATE_HOME"] = Path.Combine(source.Root, "provider-state"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(source.Home, ".claude"),
            ["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["FAKE_SKILLS_SOURCE_ROOT"] = source.SourceRoot,
            ["FAKE_SKILLS_INVOCATIONS"] = source.InvocationsPath,
        };

        await AssertPackagedManagedReinstallConfirmation(
            profile,
            environment,
            record,
            plan,
            localOnly,
            "skills content preserved after cancel");
    }

    [Fact]
    public async Task Workbench_routes_APM_Managed_Reinstall_to_exact_confirmation_without_mutating_on_cancel()
    {
        using var source = new ApmProviderFixture();
        var inspection = source.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        source.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(source.StateStore.Load().Records);
        var localOnly = Path.Combine(record.CanonicalPath, "local-only.txt");
        File.WriteAllText(localOnly, "APM content preserved after cancel");
        var plan = source.Provider.PlanManagedReinstall(record).ValueOrThrow();
        using var profile = PreparePackagedState(source.StatePath);
        var tools = Path.Combine(profile.Root, "tools");
        Directory.CreateDirectory(tools);
        CopyFakeOutput("FakeApm", "net10.0-windows", tools, "apm.exe");
        var environment = new Dictionary<string, string?>
        {
            ["USERPROFILE"] = source.Home,
            ["HOME"] = source.Home,
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(source.Home, ".claude"),
            ["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["FAKE_APM_SOURCE_ROOT"] = source.SourceRoot,
            ["FAKE_APM_SOURCE"] = ApmProviderFixture.Source,
            ["FAKE_APM_INVOCATIONS"] = source.InvocationsPath,
            ["APM_PROGRESS"] = "never",
        };

        await AssertPackagedManagedReinstallConfirmation(
            profile,
            environment,
            record,
            plan,
            localOnly,
            "APM content preserved after cancel");
    }

    private async Task AssertPackagedManagedReinstallConfirmation(
        IsolatedProfile profile,
        IReadOnlyDictionary<string, string?> environment,
        ManagementRecord record,
        IManagedReinstallPlan plan,
        string localFile,
        string expectedContent)
    {
        var workingDirectory = Path.Combine(profile.Root, "unchanged-project");
        Directory.CreateDirectory(workingDirectory);
        using var app = SkillyInstance.Start(Fixture.ExePath, profile, workingDirectory, environment);
        var main = AutomationElement.FromHandle(app.WaitForMainWindow(TimeSpan.FromMinutes(2)));
        try
        {
            var status = Find(main, "Skilly.StatusMessage");
            WaitUntil(
                () => status.Current.Name.Contains("Checked 1 managed Skill(s)", StringComparison.Ordinal),
                TimeSpan.FromMinutes(1),
                "Launch Check did not finish before provider Managed Reinstall confirmation.");
            var list = Find(main, "Skilly.SkillList");
            AutomationElement? row = null;
            WaitUntil(() =>
            {
                row = list.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                    .FirstOrDefault(element => element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)
                                               && element.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                                                   .Any(child => string.Equals(child.Current.Name, Path.GetFileName(record.CanonicalPath), StringComparison.Ordinal)));
                return row is not null;
            }, TimeSpan.FromSeconds(30), "Managed provider Skill did not appear in packaged inventory.");
            ((SelectionItemPattern)row!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var reinstall = Find(main, "Skilly.ManagedReinstall");
            WaitUntil(() => reinstall.Current.IsEnabled, TimeSpan.FromSeconds(10), "Provider Managed Reinstall did not become available.");
            var invoke = Task.Run(() => ((InvokePattern)reinstall.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
            var confirmation = WaitForWindow(
                app.Process.Id,
                ["Confirm Managed Reinstall"],
                TimeSpan.FromMinutes(1),
                () => $"Invoke completed={invoke.IsCompleted}; status={status.Current.Name}; logs={ReadLogs(profile.LogsDirectory)}");
            var confirmationText = string.Join("\n", confirmation.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>().Select(static element => element.Current.Name));
            Assert.Contains(record.CanonicalPath, confirmationText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(plan.Revision, confirmationText, StringComparison.Ordinal);
            var cancel = confirmation.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().Single(element => string.Equals(element.Current.Name, "Cancel", StringComparison.Ordinal));
            ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            await invoke.WaitAsync(TimeSpan.FromSeconds(10));
            WaitUntil(() => status.Current.Name.Contains("cancelled", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10), "Provider Managed Reinstall cancellation was not announced.");
            Assert.Equal(expectedContent, File.ReadAllText(localFile));
            Assert.Empty(Directory.EnumerateFileSystemEntries(workingDirectory));
        }
        finally
        {
            app.CloseMainWindowAndWait();
        }
    }

    private static IsolatedProfile PreparePackagedState(string sourceState)
    {
        var profile = new IsolatedProfile();
        Directory.CreateDirectory(Path.GetDirectoryName(profile.StateFilePath)!);
        File.Copy(sourceState, profile.StateFilePath);
        return profile;
    }

    private static void PrepareFakeSkillsTools(string tools)
    {
        CopyFakeOutput("FakeSkills", "net10.0-windows", tools, "FakeSkills.exe");
        File.WriteAllText(Path.Combine(tools, "npm.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(tools, "npx.cmd"), string.Empty);
        var npmBin = Path.Combine(tools, "node_modules", "npm", "bin");
        Directory.CreateDirectory(npmBin);
        File.WriteAllText(Path.Combine(npmBin, "npm-cli.js"), "console.log('12.0.2');");
        File.WriteAllText(
            Path.Combine(npmBin, "npx-cli.js"),
            "const {spawnSync}=require('child_process');const path=require('path');const r=spawnSync(path.join(__dirname,'..','..','..','FakeSkills.exe'),process.argv.slice(2),{stdio:'inherit'});process.exit(r.status??1);");
    }

    private static void CopyFakeOutput(string project, string targetFramework, string tools, string executableName)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var source = Path.Combine(PackagedAppFixture.FindRepoRoot(), "tests", project, "bin", configuration, targetFramework);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)), overwrite: true);
        }
        File.Copy(Path.Combine(source, project + ".exe"), Path.Combine(tools, executableName), overwrite: true);
    }

    internal static void CopyFakeGh(string tools)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var source = Path.Combine(
            PackagedAppFixture.FindRepoRoot(), "tests", "FakeGh", "bin", configuration, "net10.0");
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
        }
        File.Copy(Path.Combine(source, "FakeGh.exe"), Path.Combine(tools, "gh.exe"));
    }

    private static AutomationElement Find(AutomationElement root, string automationId)
        => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
           ?? throw new InvalidOperationException($"Automation element '{automationId}' was not found.");

    private static void SetValue(AutomationElement element, string value)
        => ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    private static string ReadLogs(string directory)
        => Directory.Exists(directory)
            ? string.Join("\n", Directory.EnumerateFiles(directory).Select(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }))
            : "(none)";

    private static AutomationElement WaitForWindow(
        int processId,
        IReadOnlyList<string> names,
        TimeSpan timeout,
        Func<string> diagnostic)
    {
        AutomationElement? found = null;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var processCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            found = AutomationElement.RootElement.FindAll(TreeScope.Descendants, processCondition)
                .Cast<AutomationElement>()
                .FirstOrDefault(element => element.Current.ControlType == ControlType.Window
                                           && names.Contains(element.Current.Name, StringComparer.Ordinal));
            if (found is not null) return found;
            Thread.Sleep(200);
        }
        throw new Xunit.Sdk.XunitException(
            $"Window '{string.Join("' or '", names)}' was not found. Last status: {diagnostic()}");
    }

    private static bool IsWindowAvailable(AutomationElement window)
    {
        try
        {
            var processId = window.Current.ProcessId;
            var candidates = AutomationElement.RootElement.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
            return candidates.Cast<AutomationElement>().Any(candidate =>
                candidate.Current.ControlType == ControlType.Window && Automation.Compare(candidate, window));
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string failure, Func<string>? diagnostic = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(200);
        }
        Assert.Fail(failure + (diagnostic is null ? string.Empty : " " + diagnostic()));
    }
}
