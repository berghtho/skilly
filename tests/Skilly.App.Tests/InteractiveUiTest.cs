namespace Skilly.App.Tests;

public sealed class InteractiveUiFactAttribute : FactAttribute
{
    public InteractiveUiFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SKILLY_RUN_INTERACTIVE_UI_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Interactive packaged UI test skipped. Set SKILLY_RUN_INTERACTIVE_UI_TESTS=1 to allow test windows.";
        }
    }
}
