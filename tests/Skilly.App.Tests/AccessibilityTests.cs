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
}
