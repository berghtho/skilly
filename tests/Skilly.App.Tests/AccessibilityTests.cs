using System.IO;
using System.Windows.Automation;

namespace Skilly.App.Tests;

[Collection(PackagedAppCollection.Name)]
public sealed class AccessibilityTests(PackagedAppFixture fixture)
{
    public PackagedAppFixture Fixture { get; } = fixture;

    [Fact]
    public void Workbench_exposes_named_panes_and_keyboard_focusable_controls()
    {
        using var profile = new IsolatedProfile();
        var workingDir = Path.Combine(profile.Root, "cwd");
        Directory.CreateDirectory(workingDir);

        using var instance = SkillyInstance.Start(Fixture.ExePath, profile, workingDir);
        var handle = instance.WaitForMainWindow(TimeSpan.FromMinutes(2));

        try
        {
            var window = AutomationElement.FromHandle(handle);

            AssertElement(window, "Skilly.FilterRail", "Inventory filters", expectKeyboardFocusable: true);
            AssertElement(window, "Skilly.SkillList", "Skill list", expectKeyboardFocusable: true);
            AssertElement(window, "Skilly.DetailsPane", "Skill details", expectKeyboardFocusable: false);
            AssertElement(window, "OperationStatusBar", "Operation status", expectKeyboardFocusable: false);
            AssertElement(window, "Skilly.SourceProvider", "Source provider", expectKeyboardFocusable: true);
            AssertElement(window, "Skilly.SourceReference", "Skill source URL or reference", expectKeyboardFocusable: true);
            AssertElement(window, "Skilly.InspectSource", "Inspect source", expectKeyboardFocusable: true);
            AssertElement(window, "Skilly.RefreshChecks", "Refresh checks", expectKeyboardFocusable: true);
            AssertElement(window, "Skilly.Search", "Search skills", expectKeyboardFocusable: true);

            AssertElement(window, "Skilly.StatusMessage", string.Empty, expectKeyboardFocusable: false);
        }
        finally
        {
            instance.CloseMainWindowAndWait();
        }
    }

    [Fact]
    public void Workbench_supports_keyboard_focus_destinations_source_selection_and_announced_status_changes()
    {
        using var profile = new IsolatedProfile();
        var workingDir = Path.Combine(profile.Root, "cwd");
        Directory.CreateDirectory(workingDir);
        using var instance = SkillyInstance.Start(Fixture.ExePath, profile, workingDir);
        var window = AutomationElement.FromHandle(instance.WaitForMainWindow(TimeSpan.FromMinutes(2)));

        try
        {
            var provider = FindById(window, "Skilly.SourceProvider")!;
            AssertTakesKeyboardFocus(FindById(window, "Skilly.SourceReference")!);
            AssertTakesKeyboardFocus(FindById(window, "Skilly.InspectSource")!);
            AssertTakesKeyboardFocus(FindById(window, "Skilly.RefreshChecks")!);
            AssertTakesKeyboardFocus(FindById(window, "Skilly.FilterRail")!);
            AssertTakesKeyboardFocus(FindById(window, "Skilly.Search")!);
            AssertTakesKeyboardFocus(FindById(window, "Skilly.SkillList")!);

            var selection = (SelectionPattern)provider.GetCurrentPattern(SelectionPattern.Pattern);
            ((ExpandCollapsePattern)provider.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            var apm = provider.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .First(element => string.Equals(element.Current.Name, "Microsoft microsoft/apm apm-cli", StringComparison.Ordinal));
            ((SelectionItemPattern)apm.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Assert.Equal("Microsoft microsoft/apm apm-cli", Assert.Single(selection.Current.GetSelection()).Current.Name);

            var refresh = FindById(window, "Skilly.RefreshChecks")!;
            WaitUntil(() => refresh.Current.IsEnabled, TimeSpan.FromMinutes(1), "Refresh checks did not become available.");
            ((InvokePattern)refresh.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var status = FindById(window, "Skilly.StatusMessage")!;
            WaitUntil(
                () => ReadText(status).Contains("Installed content was not changed", StringComparison.Ordinal),
                TimeSpan.FromMinutes(1),
                "The polite operation status did not announce the read-only check result.");
        }
        finally
        {
            instance.CloseMainWindowAndWait();
        }
    }

    private static void AssertElement(AutomationElement root, string automationId, string expectedName, bool expectKeyboardFocusable)
    {
        var element = FindById(root, automationId);
        Assert.True(element is not null, $"Automation element '{automationId}' was not found in the Workbench.");

        if (expectedName.Length > 0)
        {
            Assert.Equal(expectedName, element!.GetCurrentPropertyValue(AutomationElement.NameProperty));
        }

        var focusable = (bool)element!.GetCurrentPropertyValue(AutomationElement.IsKeyboardFocusableProperty);
        if (expectKeyboardFocusable)
        {
            Assert.True(focusable, $"'{automationId}' should be keyboard focusable.");
        }
    }

    private static AutomationElement? FindById(AutomationElement root, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        return root.FindFirst(TreeScope.Descendants, condition);
    }

    private static void AssertTakesKeyboardFocus(AutomationElement element)
    {
        element.SetFocus();
        Assert.True(element.Current.HasKeyboardFocus, $"'{element.Current.AutomationId}' did not accept keyboard focus.");
    }

    private static string ReadText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
        {
            return ((TextPattern)pattern).DocumentRange.GetText(-1);
        }
        return element.Current.Name;
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(200);
        }
        Assert.Fail(failure);
    }
}
